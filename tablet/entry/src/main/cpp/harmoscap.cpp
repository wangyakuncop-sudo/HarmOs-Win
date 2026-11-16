// HarmOsWin 设备端实时采集编码模块（GATE-02）
//
// 链路：OH_AVScreenCapture（HOME_SCREEN，Surface 模式，零拷贝）
//        → OH_VideoEncoder（H.264 / AVC）输入 Surface
//        → OnNewOutputBuffer 取出 Annex-B 码流
//        → napi_threadsafe_function 回传 ArkTS
//
// 出帧有两条路：
//   1) 直发模式（默认，GATE-02 帧率优化）：编码器回调线程内直接按 HWP 分片 sendto，
//      不经过 JS 线程。每帧 ~60 个分片若走 ArkTS 会在单线程上排队，实测把 30fps 压到 6.4fps。
//   2) 回调模式（回落）：TSFN 把整帧送回 ArkTS，由 UdpTransport.sendStreamFrame 分片发送。
// 包头与分片元数据必须与 ArkTS transport/HwpPacket.ets、core/SessionTypes.ets 逐字节对齐。

#include <arpa/inet.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <sys/un.h>
#include <unistd.h>

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <sys/stat.h>
#include <iterator>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include <hilog/log.h>
#include <napi/native_api.h>

#include "multimedia/player_framework/native_avbuffer.h"
#include "multimedia/player_framework/native_avbuffer_info.h"
#include "multimedia/player_framework/native_avcodec_base.h"
#include "multimedia/player_framework/native_avcodec_videoencoder.h"
#include "multimedia/player_framework/native_avformat.h"
#include "multimedia/player_framework/native_avscreen_capture.h"
#include "multimedia/player_framework/native_avscreen_capture_base.h"
#include "multimedia/player_framework/native_avscreen_capture_errors.h"

#undef LOG_DOMAIN
#undef LOG_TAG
#define LOG_DOMAIN 0x3201
#define LOG_TAG "HarmOsCap"

#define CAPI_LOGI(...) OH_LOG_Print(LOG_APP, LOG_INFO, LOG_DOMAIN, LOG_TAG, __VA_ARGS__)
#define CAPI_LOGW(...) OH_LOG_Print(LOG_APP, LOG_WARN, LOG_DOMAIN, LOG_TAG, __VA_ARGS__)
#define CAPI_LOGE(...) OH_LOG_Print(LOG_APP, LOG_ERROR, LOG_DOMAIN, LOG_TAG, __VA_ARGS__)

namespace {

// 错误码：与 ArkTS 侧约定，0 = 成功
enum : int32_t {
    OK = 0,
    ERR_ENCODER_CREATE = 1,
    ERR_ENCODER_CALLBACK = 2,
    ERR_ENCODER_CONFIGURE = 3,
    ERR_ENCODER_PREPARE = 4,
    ERR_ENCODER_START = 5,
    ERR_ENCODER_SURFACE = 6,
    ERR_CAPTURE_CREATE = 7,
    ERR_CAPTURE_INIT = 8,
    ERR_CAPTURE_START = 9,
    ERR_ALREADY_RUNNING = 10,
    ERR_NOT_RUNNING = 11,
    ERR_TSFN = 12
};

struct FramePayload {
    std::vector<uint8_t> data;
    bool keyframe = false;
    int64_t ptsUs = 0;
};

// ---------- HWP v1 常量（与 ArkTS/C# 三端对齐，禁止改动） ----------
constexpr uint16_t kHwpMagic = 0x4857;
constexpr uint8_t kHwpVersion = 1;
constexpr int32_t kHwpHeaderSize = 16;
constexpr int32_t kHwpMtu = 1200;
constexpr int32_t kStreamMetaSize = 8;
constexpr int32_t kHwpDataMax = kHwpMtu - kHwpHeaderSize - kStreamMetaSize; // 1176
constexpr uint8_t kChStream = 5;
constexpr uint8_t kFlagKeyframe = 0x08;
constexpr uint8_t kFlagLast = 0x10;

OH_AVCodec *g_codec = nullptr;
OH_AVScreenCapture *g_capture = nullptr;
napi_threadsafe_function g_tsfn = nullptr;
std::mutex g_lock;

std::atomic<int64_t> g_frames{0};
std::atomic<int64_t> g_bytes{0};
std::atomic<int64_t> g_keyframes{0};
std::atomic<int32_t> g_width{0};
std::atomic<int32_t> g_height{0};
std::atomic<int32_t> g_captureState{-1};

// 直发通道：编码器回调线程直接 sendto，绕开 JS 线程
std::mutex g_sockLock;
int32_t g_streamSock = -1;
sockaddr_in g_streamPeer{};
std::atomic<bool> g_peerReady{false};
std::atomic<bool> g_directSend{false};
// 推流开关：暂停时只跳过发送，采集与编码继续跑。
// 目的：避免每次「停止推流→再开始」都重新申请录屏权限（实测每次弹窗并白等约 9.9s）。
std::atomic<bool> g_streamActive{false};
std::atomic<uint32_t> g_seq{0};
std::atomic<uint32_t> g_frameId{0};
std::atomic<int64_t> g_packets{0};
std::atomic<int64_t> g_sendErrors{0};

// 诊断：每秒输出编码器真实出帧率。用来区分「编码器只出 N fps」与「出满帧但网络只送到 N fps」。
std::atomic<int64_t> g_windowStartUs{0};
std::atomic<int32_t> g_windowFrames{0};

// 发送锁：编码器回调线程与 JS 线程（缓存 IDR 重放）都会调 SendFrameDirect，
// 并发 sendto 会让同一帧的分片交错，PC 端重组按 frameId 判定会直接丢帧，必须串行。
std::mutex g_sendLock;

// 最近一个 IDR 缓存。用途：恢复推流/PC 重连时立刻重放，不必等采集出新帧。
// OH_AVScreenCapture 只在画面变化时出帧，静止画面下请求 IDR 也拿不到输出，实测要等 1~10s。
// 代价：每个关键帧多一次 memcpy（约几十~几百 KB，GOP 秒级一次），可忽略。
std::mutex g_idrLock;
std::vector<uint8_t> g_lastIdr;
// 最近一组 SPS/PPS。编码器把参数集单独成帧输出（flags 带 CODEC_DATA），IDR 帧本身不含 SPS/PPS。
// 不攒下来的话，重放的 IDR 就缺参数集，PC 端既拿不到 SPS 自举分辨率、也解不出画面
// （2026-11-08 实测：PC 新进程接入后一直报「尚未获得分辨率」，等 2s beacon 才能恢复）。
std::vector<uint8_t> g_lastParamSets;
// 缓存 IDR 的原始编码时刻。重放时必须沿用原时间戳，否则 PC 会把它算成「超低延迟新帧」，
// 污染端到端统计；用原时间戳时 PC 得到的是一个偏大的真实值，如实说明这是旧帧。
uint32_t g_lastIdrTs = 0;

// 统计：缓存 IDR 重放次数与节省的等待，便于真机核对
std::atomic<int64_t> g_idrReplays{0};

// 延迟追加重放：一次重放不够。
// 实测 PC 端在 CMD_STREAM=0 期间会重置解码器（需要重新 Configure），
// 恢复瞬间那一次重放常被丢弃，真正出画面要等下一个 GOP（实测 924ms）。
// 因此在恢复后按 120/600/1200/1900/2500ms 各补发一次，覆盖解码器就绪窗口。
std::thread g_replayThread;
std::atomic<bool> g_replayCancel{false};

inline int64_t MonoUs()
{
    return std::chrono::duration_cast<std::chrono::microseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

inline void PutU16LE(uint8_t *p, uint16_t v)
{
    p[0] = static_cast<uint8_t>(v & 0xff);
    p[1] = static_cast<uint8_t>((v >> 8) & 0xff);
}

inline void PutU32LE(uint8_t *p, uint32_t v)
{
    p[0] = static_cast<uint8_t>(v & 0xff);
    p[1] = static_cast<uint8_t>((v >> 8) & 0xff);
    p[2] = static_cast<uint8_t>((v >> 16) & 0xff);
    p[3] = static_cast<uint8_t>((v >> 24) & 0xff);
}

// 与 ArkTS hwpNow() 保持一致：epoch 毫秒的低 32 位（同源时钟才能算端到端延迟）
inline uint32_t NowMsLow32()
{
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    return static_cast<uint32_t>(static_cast<uint64_t>(ms) & 0xffffffffu);
}

// 一帧 H.264 按 1176B 切片，每片 = 16B HWP 头 + 8B 分片元数据 + 数据。
// 不加锁、不拷贝整帧，直接对编码器输出缓冲指针 memcpy 出包。
void SendFrameDirect(const uint8_t *data, int32_t size, bool keyframe, uint32_t tsOverride = 0,
                     bool paced = false)
{
    if (data == nullptr || size <= 0) {
        return;
    }
    const int32_t fd = g_streamSock;
    if (fd < 0 || !g_peerReady.load(std::memory_order_acquire)) {
        return;
    }
    // 串行化：编码器回调线程与 JS 线程（IDR 重放）可能同时进入
    std::lock_guard<std::mutex> sendGuard(g_sendLock);
    const int32_t count = (size + kHwpDataMax - 1) / kHwpDataMax;
    if (count <= 0 || count > 0xffff) {
        return;
    }
    const sockaddr_in peer = g_streamPeer;
    const uint32_t frameId = g_frameId.fetch_add(1, std::memory_order_relaxed) + 1;
    const uint32_t ts = tsOverride != 0 ? tsOverride : NowMsLow32();
    uint8_t buf[kHwpMtu];
    int64_t errors = 0;
    for (int32_t i = 0; i < count; ++i) {
        const int32_t from = i * kHwpDataMax;
        const int32_t len = (size - from) < kHwpDataMax ? (size - from) : kHwpDataMax;
        PutU16LE(buf + 0, kHwpMagic);
        buf[2] = kHwpVersion;
        buf[3] = kChStream;
        uint8_t flags = 0;
        if (keyframe && i == 0) {
            flags |= kFlagKeyframe;
        }
        if (i == count - 1) {
            flags |= kFlagLast;
        }
        buf[4] = flags;
        buf[5] = 0;
        PutU32LE(buf + 6, g_seq.fetch_add(1, std::memory_order_relaxed) + 1);
        PutU32LE(buf + 10, ts);
        PutU16LE(buf + 14, static_cast<uint16_t>(kStreamMetaSize + len));
        PutU32LE(buf + 16, frameId);
        PutU16LE(buf + 20, static_cast<uint16_t>(i));
        PutU16LE(buf + 22, static_cast<uint16_t>(count));
        std::memcpy(buf + kHwpHeaderSize + kStreamMetaSize, data + from, static_cast<size_t>(len));
        const size_t total = static_cast<size_t>(kHwpHeaderSize + kStreamMetaSize + len);
        if (sendto(fd, buf, total, 0, reinterpret_cast<const sockaddr *>(&peer), sizeof(peer)) < 0) {
            ++errors;
        }
        // 重放帧是一次性突发近 70 个包，PC 侧偶发丢几包就会让整帧凑不齐，
        // 实测恢复首帧因此拖到 ~800ms。每 8 包让出 200μs（整帧多花约 1.8ms，可忽略）。
        if (paced && (i & 7) == 7) {
            usleep(200);
        }
    }
    g_packets.fetch_add(count, std::memory_order_relaxed);
    if (errors > 0) {
        g_sendErrors.fetch_add(errors, std::memory_order_relaxed);
    }
}

// 恢复推流后的追加重放：250/500/750ms 各补发一次缓存 IDR。
// 起独立线程是为了不阻塞调用 setStreamActive 的 JS 线程。
// 重放缓存关键帧的唯一实现：参数集单独成帧先发，IDR 紧随。
// MF 的 H264 MFT 对「SPS+PPS+IDR 挤在同一 sample」不会立即出帧
// （实测首帧要等到下一个 GOP，4.7~13s）；拆开后 PC 一次就能解出。
// 主重放与追加重放必须共用本函数，否则只有第一次拆帧、后续仍整块发，等于白改。
void SendCachedIdrSplit(uint32_t ts, bool paced)
{
    std::vector<uint8_t> idr;
    std::vector<uint8_t> ps;
    {
        std::lock_guard<std::mutex> guard(g_idrLock);
        idr = g_lastIdr;
        ps = g_lastParamSets;
    }
    if (idr.empty() || !g_directSend.load(std::memory_order_acquire) ||
        !g_peerReady.load(std::memory_order_acquire)) {
        return;
    }
    int32_t offset = 0;
    if (!ps.empty() && idr.size() > ps.size() && std::equal(ps.begin(), ps.end(), idr.begin())) {
        SendFrameDirect(ps.data(), static_cast<int32_t>(ps.size()), true, ts, paced);
        offset = static_cast<int32_t>(ps.size());
    }
    SendFrameDirect(idr.data() + offset, static_cast<int32_t>(idr.size()) - offset, true, ts, paced);
    g_idrReplays.fetch_add(1, std::memory_order_relaxed);
}

void ReplayCachedIdr(uint32_t ts)
{
    SendCachedIdrSplit(ts, true);
}

void ScheduleTrailingReplay()
{
    if (g_replayThread.joinable()) {
        g_replayCancel.store(true, std::memory_order_release);
        g_replayThread.join();
    }
    g_replayCancel.store(false, std::memory_order_release);
    g_replayThread = std::thread([]() {
        // 时间表：120/600/1200/1900/2500ms。
        // 前三次覆盖解码器快速就绪，后两次覆盖 PC 首次创建 MF 解码器（实测约 2.4s）的窗口——
        // 窗口太短时重放帧会全部早于解码器就绪，只能干等下一个 GOP。
        static const int32_t kDelaysMs[] = {120, 600, 1200, 1900, 2500};
        const int32_t n = static_cast<int32_t>(sizeof(kDelaysMs) / sizeof(kDelaysMs[0]));
        int32_t prev = 0;
        for (int32_t i = 0; i < n; ++i) {
            std::this_thread::sleep_for(std::chrono::milliseconds(kDelaysMs[i] - prev));
            prev = kDelaysMs[i];
            if (g_replayCancel.load(std::memory_order_acquire) ||
                !g_streamActive.load(std::memory_order_acquire)) {
                return;
            }
            uint32_t ts = 0;
            {
                std::lock_guard<std::mutex> guard(g_idrLock);
                ts = g_lastIdrTs;
            }
            ReplayCachedIdr(ts);
        }
    });
}

// ---------- TSFN ----------
void CallJs(napi_env env, napi_value jsCb, void * /*context*/, void *data)
{
    FramePayload *p = static_cast<FramePayload *>(data);
    if (p == nullptr) {
        return;
    }
    if (env == nullptr || jsCb == nullptr) {
        delete p;
        return;
    }

    napi_value argv[3] = {nullptr, nullptr, nullptr};
    void *raw = nullptr;
    napi_value arrayBuffer = nullptr;
    napi_status st = napi_create_arraybuffer(env, p->data.size(), &raw, &arrayBuffer);
    if (st != napi_ok) {
        delete p;
        return;
    }
    if (!p->data.empty() && raw != nullptr) {
        std::memcpy(raw, p->data.data(), p->data.size());
    }
    napi_create_typedarray(env, napi_uint8_array, p->data.size(), arrayBuffer, 0, &argv[0]);
    napi_get_boolean(env, p->keyframe, &argv[1]);
    // 同上：int64 会成为 BigInt，与 ArkTS 的 number 声明冲突，用 double
    napi_create_double(env, static_cast<double>(p->ptsUs), &argv[2]);

    napi_value undefined = nullptr;
    napi_get_undefined(env, &undefined);
    napi_call_function(env, undefined, jsCb, 3, argv, nullptr);

    delete p;
}

void OnTsfnFinalize(napi_env /*env*/, void * /*finalizeData*/, void * /*hint*/)
{
    std::lock_guard<std::mutex> guard(g_lock);
    g_tsfn = nullptr;
}

// ---------- 编码器回调 ----------

void OnCodecError(OH_AVCodec * /*codec*/, int32_t code, void * /*userData*/)
{
    CAPI_LOGE("encoder error code=%{public}d", code);
}

void OnStreamChanged(OH_AVCodec * /*codec*/, OH_AVFormat *format, void * /*userData*/)
{
    int32_t w = 0;
    int32_t h = 0;
    if (format != nullptr) {
        OH_AVFormat_GetIntValue(format, OH_MD_KEY_WIDTH, &w);
        OH_AVFormat_GetIntValue(format, OH_MD_KEY_HEIGHT, &h);
    }
    CAPI_LOGI("encoder stream changed %{public}dx%{public}d", w, h);
}

void OnNeedInputBuffer(OH_AVCodec * /*codec*/, uint32_t /*index*/, OH_AVBuffer * /*buffer*/, void * /*userData*/)
{
    // Surface 输入模式下不需要向编码器喂 InputBuffer，帧由屏幕采集直接写入 Surface。
}

// 扫描 AnnexB 里是否已含 SPS(7)/PPS(8)。
// 实测 OH_VideoEncoder 的 IDR 有时自带参数集、有时纯 IDR，不能盲目前置（会重复）。
// 扫描是否含 slice NAL（1=non-IDR, 5=IDR）。用来区分「纯参数集帧」和「带 slice 的图像帧」。
static bool HasSliceNal(const uint8_t *p, int32_t size)
{
    for (int32_t i = 0; i + 4 < size; ++i) {
        if (p[i] != 0 || p[i + 1] != 0) {
            continue;
        }
        int32_t hdr = 0;
        if (p[i + 2] == 1) {
            hdr = i + 3;
        } else if (p[i + 2] == 0 && p[i + 3] == 1) {
            hdr = i + 4;
        } else {
            continue;
        }
        if (hdr >= size) {
            break;
        }
        const int32_t nalType = p[hdr] & 0x1f;
        if (nalType == 1 || nalType == 5) {
            return true;
        }
        i = hdr;
    }
    return false;
}

static bool HasParamSets(const uint8_t *p, int32_t size)
{
    for (int32_t i = 0; i + 4 < size; ++i) {
        if (p[i] != 0 || p[i + 1] != 0) {
            continue;
        }
        int32_t hdr = 0;
        if (p[i + 2] == 1) {
            hdr = i + 3;
        } else if (p[i + 2] == 0 && p[i + 3] == 1) {
            hdr = i + 4;
        } else {
            continue;
        }
        if (hdr >= size) {
            break;
        }
        const int32_t nalType = p[hdr] & 0x1f;
        if (nalType == 7 || nalType == 8) {
            return true;
        }
        i = hdr;
    }
    return false;
}

void OnNewOutputBuffer(OH_AVCodec *codec, uint32_t index, OH_AVBuffer *buffer, void * /*userData*/)
{
    if (buffer == nullptr) {
        return;
    }
    OH_AVCodecBufferAttr attr;
    std::memset(&attr, 0, sizeof(attr));
    if (OH_AVBuffer_GetBufferAttr(buffer, &attr) != AV_ERR_OK) {
        OH_VideoEncoder_FreeOutputBuffer(codec, index);
        return;
    }
    uint8_t *addr = OH_AVBuffer_GetAddr(buffer);
    const bool keyframe = (attr.flags & AVCODEC_BUFFER_FLAGS_SYNC_FRAME) != 0;
    const bool codecData = (attr.flags & AVCODEC_BUFFER_FLAGS_CODEC_DATA) != 0;
    if (addr != nullptr && attr.size > 0) {
        g_frames.fetch_add(1);
        g_bytes.fetch_add(attr.size);
        if (keyframe) {
            g_keyframes.fetch_add(1);
        }
        const bool hasPs = HasParamSets(addr, static_cast<int32_t>(attr.size));
        const bool hasSlice = HasSliceNal(addr, static_cast<int32_t>(attr.size));
        if (codecData || keyframe) {
            // 同一把锁里处理：g_idrLock 非递归，分两次 lock_guard 会自锁
            std::lock_guard<std::mutex> idrGuard(g_idrLock);
            if (hasPs && !hasSlice) {
                // 纯参数集帧（SPS+PPS）：留一份独立的，重放时作为单独一帧先发
                g_lastParamSets.assign(addr, addr + attr.size);
                CAPI_LOGI("param sets cached %{public}dKB", attr.size / 1024);
            } else if (keyframe) {
                g_lastIdr.clear();
                if (!hasPs) {
                    // 纯 IDR：前置参数集，PC 才能自举分辨率
                    std::copy(g_lastParamSets.begin(), g_lastParamSets.end(), std::back_inserter(g_lastIdr));
                }
                std::copy(reinterpret_cast<const char *>(addr),
                          reinterpret_cast<const char *>(addr) + attr.size,
                          std::back_inserter(g_lastIdr));
                g_lastIdrTs = NowMsLow32();
                CAPI_LOGI("idr cache: param=%{public}zuB self=%{public}d idr=%{public}dKB total=%{public}zuB",
                          g_lastParamSets.size(), hasPs ? 1 : 0, attr.size / 1024, g_lastIdr.size());
            }
        }
        // 每秒一条：编码器出帧率 + 平均帧大小 + 已发包数，直发与回落两条路都统计
        const int64_t nowUs = MonoUs();
        int64_t winStart = g_windowStartUs.load(std::memory_order_relaxed);
        if (winStart == 0) {
            g_windowStartUs.store(nowUs, std::memory_order_relaxed);
            winStart = nowUs;
        }
        const int32_t winFrames = g_windowFrames.fetch_add(1, std::memory_order_relaxed) + 1;
        if (nowUs - winStart >= 1000000) {
            const int64_t elapsedMs = (nowUs - winStart) / 1000;
            const int32_t encFps = static_cast<int32_t>(winFrames * 1000 / (elapsedMs > 0 ? elapsedMs : 1));
            CAPI_LOGI("enc %{public}d fps | lastSize=%{public}dKB totalFrames=%{public}d packets=%{public}d txErr=%{public}d state=%{public}d",
                      encFps, attr.size / 1024, static_cast<int32_t>(g_frames.load()),
                      static_cast<int32_t>(g_packets.load()),
                      static_cast<int32_t>(g_sendErrors.load()), g_captureState.load());
            g_windowFrames.store(0, std::memory_order_relaxed);
            g_windowStartUs.store(nowUs, std::memory_order_relaxed);
        }
        if (g_streamActive.load(std::memory_order_acquire) && g_directSend.load(std::memory_order_acquire)) {
            // 直发：编码器回调线程内分片 sendto，不占用 JS 线程
            SendFrameDirect(addr, attr.size, keyframe);
        } else if (!g_streamActive.load(std::memory_order_acquire)) {
            // 暂停中：只编码不发送，保持采集会话存活（省掉重新授权的 9.9s）
        } else {
            FramePayload *payload = new FramePayload();
            payload->data.assign(addr, addr + attr.size);
            payload->keyframe = keyframe;
            payload->ptsUs = attr.pts;
            napi_threadsafe_function tsfn = g_tsfn;
            if (tsfn != nullptr) {
                if (napi_call_threadsafe_function(tsfn, payload, napi_tsfn_nonblocking) != napi_ok) {
                    delete payload;
                }
            } else {
                delete payload;
            }
        }
    }
    OH_VideoEncoder_FreeOutputBuffer(codec, index);
}

// ---------- 录屏状态回调 ----------

void OnCaptureStateChange(OH_AVScreenCapture * /*capture*/, OH_AVScreenCaptureStateCode stateCode, void * /*userData*/)
{
    g_captureState.store(static_cast<int32_t>(stateCode));
    CAPI_LOGI("capture state=%{public}d", static_cast<int32_t>(stateCode));
}

void OnCaptureError(OH_AVScreenCapture * /*capture*/, int32_t errorCode, void * /*userData*/)
{
    CAPI_LOGE("capture error=%{public}d", errorCode);
}

// ---------- 资源释放 ----------

void ReleaseAll()
{
    // 先停追加重放线程，避免它在采集/编码器释放后仍持有指针
    g_replayCancel.store(true, std::memory_order_release);
    if (g_replayThread.joinable()) {
        g_replayThread.join();
    }
    if (g_capture != nullptr) {
        OH_AVScreenCapture_StopScreenCapture(g_capture);
        OH_AVScreenCapture_Release(g_capture);
        g_capture = nullptr;
    }
    if (g_codec != nullptr) {
        OH_VideoEncoder_Stop(g_codec);
        OH_VideoEncoder_Destroy(g_codec);
        g_codec = nullptr;
    }
    // 缓存的 IDR 属于本次采集会话，重新开始时必须清掉，否则会重放上一轮的旧画面
    {
        std::lock_guard<std::mutex> idrGuard(g_idrLock);
        g_lastIdr.clear();
        g_lastIdrTs = 0;
        g_lastParamSets.clear();
    }
}

// ---------- NAPI ----------

int32_t GetIntArg(napi_env env, napi_value value, int32_t fallback)
{
    napi_valuetype type = napi_undefined;
    napi_typeof(env, value, &type);
    if (type != napi_number) {
        return fallback;
    }
    int32_t out = fallback;
    napi_get_value_int32(env, value, &out);
    return out;
}

// start(width, height, fps, bitrateKbps, onFrame) -> int32
napi_value StartCapture(napi_env env, napi_callback_info info)
{
    size_t argc = 5;
    napi_value args[5] = {nullptr, nullptr, nullptr, nullptr, nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);

    const int32_t width = GetIntArg(env, argc > 0 ? args[0] : nullptr, 0);
    const int32_t height = GetIntArg(env, argc > 1 ? args[1] : nullptr, 0);
    const int32_t fps = GetIntArg(env, argc > 2 ? args[2] : nullptr, 30);
    const int32_t bitrateKbps = GetIntArg(env, argc > 3 ? args[3] : nullptr, 16000);

    napi_value result = nullptr;
    if (width <= 0 || height <= 0) {
        napi_create_int32(env, ERR_ENCODER_CONFIGURE, &result);
        return result;
    }

    std::lock_guard<std::mutex> guard(g_lock);
    if (g_codec != nullptr) {
        napi_create_int32(env, ERR_ALREADY_RUNNING, &result);
        return result;
    }

    // 1) 线程安全函数，用于把编码帧送回 JS 线程
    napi_value jsCb = argc > 4 ? args[4] : nullptr;
    napi_valuetype cbType = napi_undefined;
    if (jsCb != nullptr) {
        napi_typeof(env, jsCb, &cbType);
    }
    if (cbType != napi_function) {
        napi_create_int32(env, ERR_TSFN, &result);
        return result;
    }
    napi_value resourceName = nullptr;
    napi_create_string_utf8(env, "HarmOsCapFrame", NAPI_AUTO_LENGTH, &resourceName);
    napi_status st = napi_create_threadsafe_function(env, jsCb, nullptr, resourceName, 12, 1, nullptr,
                                                     OnTsfnFinalize, nullptr, CallJs, &g_tsfn);
    if (st != napi_ok || g_tsfn == nullptr) {
        napi_create_int32(env, ERR_TSFN, &result);
        return result;
    }

    // 2) 创建并配置 H.264 编码器（Surface 输入）
    g_codec = OH_VideoEncoder_CreateByMime(OH_AVCODEC_MIMETYPE_VIDEO_AVC);
    if (g_codec == nullptr) {
        napi_release_threadsafe_function(g_tsfn, napi_tsfn_release);
        g_tsfn = nullptr;
        napi_create_int32(env, ERR_ENCODER_CREATE, &result);
        return result;
    }

    OH_AVCodecCallback callback;
    callback.onError = OnCodecError;
    callback.onStreamChanged = OnStreamChanged;
    callback.onNeedInputBuffer = OnNeedInputBuffer;
    callback.onNewOutputBuffer = OnNewOutputBuffer;
    if (OH_VideoEncoder_RegisterCallback(g_codec, callback, nullptr) != AV_ERR_OK) {
        ReleaseAll();
        napi_create_int32(env, ERR_ENCODER_CALLBACK, &result);
        return result;
    }

    // 重复帧属性只在 configure 阶段生效，失败要能整体降级，所以格式由 lambda 统一构造
    auto buildFormat = [&](bool withRepeatFrame) -> OH_AVFormat * {
        OH_AVFormat *f = OH_AVFormat_Create();
        OH_AVFormat_SetIntValue(f, OH_MD_KEY_WIDTH, width);
        OH_AVFormat_SetIntValue(f, OH_MD_KEY_HEIGHT, height);
        OH_AVFormat_SetIntValue(f, OH_MD_KEY_PIXEL_FORMAT, AV_PIXEL_FORMAT_SURFACE_FORMAT);
        OH_AVFormat_SetIntValue(f, OH_MD_KEY_VIDEO_ENCODE_BITRATE_MODE, BITRATE_MODE_CBR);
        OH_AVFormat_SetLongValue(f, OH_MD_KEY_BITRATE, static_cast<int64_t>(bitrateKbps) * 1000);
        OH_AVFormat_SetIntValue(f, OH_MD_KEY_FRAME_RATE, fps);
        OH_AVFormat_SetIntValue(f, OH_MD_KEY_I_FRAME_INTERVAL, 1);
        OH_AVFormat_SetIntValue(f, OH_MD_KEY_PROFILE, AVC_PROFILE_MAIN);
        if (withRepeatFrame) {
            // 2026-11-08 关键修复：OH_AVScreenCapture 是 surface 模式，画面不变就不出帧。
            // 后果不只是「静止 0fps」—— PC 端 MF 的 H264 MFT 必须先累积到内容不同的连续帧才会吐第一帧，
            // 重连时设备只重放同一个缓存 IDR，等于重复帧，MFT 一帧都不输出（实测重复 8 次仍 0 帧，
            // 而真实连续帧 8 帧就出图）→ 首帧要等用户真的去动屏幕，实测 2.4~13s。
            // 让编码器在无新帧时按 33ms 周期重复编码上一帧（frame_num/POC 递增，算真帧），
            // 静止也有稳定帧流，重连首帧立刻可解。-1 = 不设次数上限。
            OH_AVFormat_SetIntValue(f, OH_MD_KEY_VIDEO_ENCODER_REPEAT_PREVIOUS_FRAME_AFTER, 33);
            OH_AVFormat_SetIntValue(f, OH_MD_KEY_VIDEO_ENCODER_REPEAT_PREVIOUS_MAX_COUNT, -1);
        }
        return f;
    };
    OH_AVFormat *format = buildFormat(true);
    OH_AVErrCode cfgRet = OH_VideoEncoder_Configure(g_codec, format);
    OH_AVFormat_Destroy(format);
    if (cfgRet != AV_ERR_OK) {
        // 老 ROM 可能不认「重复上一帧」的两个 key：去掉后重试一次，避免整机不可用
        CAPI_LOGW("configure with repeat-frame failed ret=%{public}d, retry without it",
                  static_cast<int32_t>(cfgRet));
        OH_AVFormat *fallback = buildFormat(false);
        cfgRet = OH_VideoEncoder_Configure(g_codec, fallback);
        OH_AVFormat_Destroy(fallback);
    }
    if (cfgRet != AV_ERR_OK) {
        CAPI_LOGE("configure failed ret=%{public}d", static_cast<int32_t>(cfgRet));
        ReleaseAll();
        napi_create_int32(env, ERR_ENCODER_CONFIGURE, &result);
        return result;
    }
    if (OH_VideoEncoder_Prepare(g_codec) != AV_ERR_OK) {
        CAPI_LOGE("prepare failed");
        ReleaseAll();
        napi_create_int32(env, ERR_ENCODER_PREPARE, &result);
        return result;
    }
    // Surface 必须在 Prepare 之后、Start 之前取，否则返回 AV_ERR_UNSUPPORT/INVALID_STATE
    OHNativeWindow *window = nullptr;
    OH_AVErrCode surfRet = OH_VideoEncoder_GetSurface(g_codec, &window);
    if (surfRet != AV_ERR_OK || window == nullptr) {
        CAPI_LOGE("get surface failed ret=%{public}d", static_cast<int32_t>(surfRet));
        ReleaseAll();
        napi_create_int32(env, ERR_ENCODER_SURFACE, &result);
        return result;
    }
    if (OH_VideoEncoder_Start(g_codec) != AV_ERR_OK) {
        CAPI_LOGE("encoder start failed");
        ReleaseAll();
        napi_create_int32(env, ERR_ENCODER_START, &result);
        return result;
    }

    // 3) 屏幕采集：HOME_SCREEN，原始流，直接写入编码器 Surface
    g_capture = OH_AVScreenCapture_Create();
    if (g_capture == nullptr) {
        ReleaseAll();
        napi_create_int32(env, ERR_CAPTURE_CREATE, &result);
        return result;
    }

    OH_AVScreenCaptureConfig config;
    std::memset(&config, 0, sizeof(config));
    config.captureMode = OH_CAPTURE_HOME_SCREEN;
    config.dataType = OH_ORIGINAL_STREAM;
    config.audioInfo.micCapInfo.audioSampleRate = 0;
    config.audioInfo.micCapInfo.audioChannels = 0;
    config.audioInfo.micCapInfo.audioSource = OH_MIC;
    config.audioInfo.innerCapInfo.audioSampleRate = 0;
    config.audioInfo.innerCapInfo.audioChannels = 0;
    config.audioInfo.innerCapInfo.audioSource = OH_ALL_PLAYBACK;
    config.audioInfo.audioEncInfo.audioBitrate = 0;
    config.audioInfo.audioEncInfo.audioCodecformat = OH_AAC_LC;
    config.videoInfo.videoCapInfo.displayId = 0;
    config.videoInfo.videoCapInfo.missionIDs = nullptr;
    config.videoInfo.videoCapInfo.missionIDsLen = 0;
    config.videoInfo.videoCapInfo.videoFrameWidth = width;
    config.videoInfo.videoCapInfo.videoFrameHeight = height;
    config.videoInfo.videoCapInfo.videoSource = OH_VIDEO_SOURCE_SURFACE_RGBA;
    config.videoInfo.videoEncInfo.videoCodec = OH_H264;
    config.videoInfo.videoEncInfo.videoBitrate = bitrateKbps;
    config.videoInfo.videoEncInfo.videoFrameRate = fps;

    if (OH_AVScreenCapture_Init(g_capture, config) != AV_SCREEN_CAPTURE_ERR_OK) {
        CAPI_LOGE("capture init failed");
        ReleaseAll();
        napi_create_int32(env, ERR_CAPTURE_INIT, &result);
        return result;
    }
    OH_AVScreenCapture_SetStateCallback(g_capture, OnCaptureStateChange, nullptr);
    OH_AVScreenCapture_SetErrorCallback(g_capture, OnCaptureError, nullptr);

    // 采集帧率封顶：Surface 模式下采集按屏幕刷新出帧（实测 60fps），不封顶会让编码器与网络都跑在 60fps，
    // 码率与丢包一起升高。会话目标帧率由 ArkTS 传入，这里强制对齐。
    OH_AVSCREEN_CAPTURE_ErrCode fpsRet = OH_AVScreenCapture_SetMaxVideoFrameRate(g_capture, fps);
    if (fpsRet != AV_SCREEN_CAPTURE_ERR_OK) {
        CAPI_LOGE("set max frame rate %{public}d failed ret=%{public}d", fps, static_cast<int32_t>(fpsRet));
    }

    g_width.store(width);
    g_height.store(height);
    g_frames.store(0);
    g_bytes.store(0);
    g_keyframes.store(0);
    g_seq.store(0);
    g_frameId.store(0);
    g_packets.store(0);
    g_sendErrors.store(0);
    g_idrReplays.store(0);
    g_streamActive.store(true, std::memory_order_release);

    OH_AVSCREEN_CAPTURE_ErrCode startRet = OH_AVScreenCapture_StartScreenCaptureWithSurface(g_capture, window);
    if (startRet != AV_SCREEN_CAPTURE_ERR_OK) {
        CAPI_LOGE("start with surface failed ret=%{public}d", static_cast<int32_t>(startRet));
        ReleaseAll();
        napi_create_int32(env, ERR_CAPTURE_START, &result);
        return result;
    }

    CAPI_LOGI("started %{public}dx%{public}d fps=%{public}d bitrate=%{public}dkbps", width, height, fps, bitrateKbps);
    napi_create_int32(env, OK, &result);
    return result;
}

napi_value StopCapture(napi_env env, napi_callback_info /*info*/)
{
    g_directSend.store(false);
    g_streamActive.store(false, std::memory_order_release);
    std::lock_guard<std::mutex> guard(g_lock);
    ReleaseAll();
    if (g_tsfn != nullptr) {
        napi_release_threadsafe_function(g_tsfn, napi_tsfn_release);
        g_tsfn = nullptr;
    }
    g_captureState.store(-1);
    napi_value result = nullptr;
    napi_create_int32(env, OK, &result);
    return result;
}

// setStreamPeer(host, port) -> 0 成功 / -1 参数无效 / -2 建 socket 失败 / -3 地址解析失败
// 直发通道单独用一个 socket（不 bind，源端口由系统分配）。PC 端受包只按配置的端点回控制包，
// 不跟随 remote 端口，因此视频源端口与 ArkTS 的 9020 不同不影响控制通道。
napi_value SetStreamPeer(napi_env env, napi_callback_info info)
{
    size_t argc = 2;
    napi_value args[2] = {nullptr, nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);

    char host[64] = {0};
    size_t hostLen = 0;
    if (argc > 0 && args[0] != nullptr) {
        napi_get_value_string_utf8(env, args[0], host, sizeof(host) - 1, &hostLen);
    }
    const int32_t port = GetIntArg(env, argc > 1 ? args[1] : nullptr, 0);

    napi_value result = nullptr;
    if (host[0] == '\0' || port <= 0 || port > 65535) {
        napi_create_int32(env, -1, &result);
        return result;
    }

    std::lock_guard<std::mutex> guard(g_sockLock);
    if (g_streamSock < 0) {
        g_streamSock = socket(AF_INET, SOCK_DGRAM, 0);
        if (g_streamSock < 0) {
            CAPI_LOGE("stream socket failed errno=%{public}d", errno);
            napi_create_int32(env, -2, &result);
            return result;
        }
        int32_t sndBuf = 4 * 1024 * 1024;
        setsockopt(g_streamSock, SOL_SOCKET, SO_SNDBUF, &sndBuf, sizeof(sndBuf));
        // 非阻塞：发送缓冲满时立即返回 EAGAIN 并计数，绝不把编码器回调线程卡住
        const int32_t flags = fcntl(g_streamSock, F_GETFL, 0);
        if (flags >= 0) {
            fcntl(g_streamSock, F_SETFL, flags | O_NONBLOCK);
        }
    }
    sockaddr_in addr;
    std::memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(static_cast<uint16_t>(port));
    if (inet_pton(AF_INET, host, &addr.sin_addr) != 1) {
        napi_create_int32(env, -3, &result);
        return result;
    }
    g_streamPeer = addr;
    g_seq.store(0);
    g_frameId.store(0);
    g_packets.store(0);
    g_sendErrors.store(0);
    g_peerReady.store(true, std::memory_order_release);
    CAPI_LOGI("stream peer set %{public}s:%{public}d fd=%{public}d", host, port, g_streamSock);
    napi_create_int32(env, 0, &result);
    return result;
}

// setDirectSend(enabled) -> 0 成功。关闭时回到 TSFN 回调模式（由 ArkTS 分片发送）。
napi_value SetDirectSend(napi_env env, napi_callback_info info)
{
    size_t argc = 1;
    napi_value args[1] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);
    bool enabled = false;
    napi_valuetype type = napi_undefined;
    if (argc > 0 && args[0] != nullptr) {
        napi_typeof(env, args[0], &type);
    }
    if (type == napi_boolean) {
        napi_get_value_bool(env, args[0], &enabled);
    }
    g_directSend.store(enabled, std::memory_order_release);
    CAPI_LOGI("direct send %{public}s", enabled ? "on" : "off");
    napi_value result = nullptr;
    napi_create_int32(env, OK, &result);
    return result;
}

// setStreamActive(enabled) -> 0
// false 只停止发送，采集/编码继续跑：避免「停止→再开始」重新弹录屏授权（白等约 9.9s）。
// true 时主动请求一个 IDR，PC 收到即可解出画面，不用等下一个 GOP。
napi_value SetStreamActive(napi_env env, napi_callback_info info)
{
    size_t argc = 1;
    napi_value args[1] = {nullptr};
    napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);
    bool enabled = false;
    napi_valuetype type = napi_undefined;
    if (argc > 0 && args[0] != nullptr) {
        napi_typeof(env, args[0], &type);
    }
    if (type == napi_boolean) {
        napi_get_value_bool(env, args[0], &enabled);
    }
    g_streamActive.store(enabled, std::memory_order_release);
    {
        size_t idrSize = 0;
        {
            std::lock_guard<std::mutex> idrGuard(g_idrLock);
            idrSize = g_lastIdr.size();
        }
        CAPI_LOGI("setStreamActive %{public}s direct=%{public}d peer=%{public}d idr=%{public}zuB",
                  enabled ? "true" : "false", g_directSend.load() ? 1 : 0,
                  g_peerReady.load() ? 1 : 0, idrSize);
    }
    if (enabled) {
        // ① 立刻重放缓存的 IDR：OH_AVScreenCapture 只在画面变化时出帧，
        //    静止画面下即使请求 IDR 也没有新帧可编，实测恢复要等 1~10s。
        //    重放用原始时间戳，避免把旧帧伪装成低延迟新帧。
        if (g_directSend.load(std::memory_order_acquire) &&
            g_peerReady.load(std::memory_order_acquire)) {
            size_t idrSize = 0;
            uint32_t replayTs = 0;
            {
                std::lock_guard<std::mutex> idrGuard(g_idrLock);
                idrSize = g_lastIdr.size();
                replayTs = g_lastIdrTs;
            }
            if (idrSize > 0) {
                SendCachedIdrSplit(replayTs, false);
                CAPI_LOGI("replay cached IDR %{public}dKB + trailing x5", static_cast<int32_t>(idrSize / 1024));
                ScheduleTrailingReplay();
            }
        }
        // ② 再请求一个新 IDR：画面一旦变化就立刻接上最新内容
        if (g_codec != nullptr) {
            OH_AVFormat *f = OH_AVFormat_Create();
            if (f != nullptr) {
                OH_AVFormat_SetIntValue(f, OH_MD_KEY_REQUEST_I_FRAME, 1);
                OH_VideoEncoder_SetParameter(g_codec, f);
                OH_AVFormat_Destroy(f);
            }
        }
    }
    CAPI_LOGI("stream active %{public}s", enabled ? "on" : "off");
    napi_value result = nullptr;
    napi_create_int32(env, OK, &result);
    return result;
}

// getStats() -> { frames, bytes, keyframes, width, height, state, packets, sendErrors }
napi_value GetStats(napi_env env, napi_callback_info /*info*/)
{
    napi_value obj = nullptr;
    napi_create_object(env, &obj);

    // 注意：napi_create_int64 在 ArkTS 中产生的是 BigInt，而 .d.ts 声明为 number，
    // 赋值会抛类型异常（被 stats() 的 catch 吞掉，UI 永远显示全 0 / state=-1）。
    // 这里一律用 double，ArkTS 的 number 就是 double，精度足够（2^53 以内）。
    napi_value v = nullptr;
    napi_create_double(env, static_cast<double>(g_frames.load()), &v);
    napi_set_named_property(env, obj, "frames", v);
    napi_create_double(env, static_cast<double>(g_bytes.load()), &v);
    napi_set_named_property(env, obj, "bytes", v);
    napi_create_double(env, static_cast<double>(g_keyframes.load()), &v);
    napi_set_named_property(env, obj, "keyframes", v);
    napi_create_int32(env, g_width.load(), &v);
    napi_set_named_property(env, obj, "width", v);
    napi_create_int32(env, g_height.load(), &v);
    napi_set_named_property(env, obj, "height", v);
    napi_create_int32(env, g_captureState.load(), &v);
    napi_set_named_property(env, obj, "state", v);
    napi_create_double(env, static_cast<double>(g_packets.load()), &v);
    napi_set_named_property(env, obj, "packets", v);
    napi_create_double(env, static_cast<double>(g_sendErrors.load()), &v);
    napi_set_named_property(env, obj, "sendErrors", v);
    napi_create_int32(env, g_directSend.load() ? 1 : 0, &v);
    napi_set_named_property(env, obj, "directSend", v);
    // 缓存 IDR 重放次数：恢复推流后 > 0 说明走了「立刻出画面」路径
    napi_create_double(env, static_cast<double>(g_idrReplays.load()), &v);
    napi_set_named_property(env, obj, "idrReplays", v);
    int32_t idrKb = 0;
    {
        std::lock_guard<std::mutex> idrGuard(g_idrLock);
        idrKb = static_cast<int32_t>(g_lastIdr.size() / 1024);
    }
    napi_create_int32(env, idrKb, &v);
    napi_set_named_property(env, obj, "idrCachedKb", v);
    return obj;
}

// ============================================================================
// uitest RPC 探针（方案 B 可行性验证，2026-11-08）
//
// 目的：验证「平板应用进程自己连 @uitest_socket」这条路是否可行。
// 与 PC 直连（方案 A，走 hdc fport）相比，方案 B 不依赖 hdc / 无线调试，
// 应用进程 <-> agent.so 是本地抽象 unix socket，理论上 <1ms。
//
// 已知风险（必须先证伪）：
//   1) agent.so 里有 "Got connection from domain socket, Peer UID: %d"，
//      若其对应用 UID 做白名单校验，connect 或首个请求会被拒；
//   2) 抽象 unix socket 无文件权限位，真正的门是 SELinux：
//      应用域（normal_hap）通常没有 unix_stream_socket connectto 到 shell 域的 socket。
//
// 返回一行 JSON，字段含义见 types/libharmoscap/index.d.ts 的 ProbeUitestResult。
// ============================================================================
constexpr const char *kUitestSocketName = "uitest_socket";
constexpr const char *kRpcHead = "_uitestkit_rpc_message_head_";
constexpr const char *kRpcTail = "_uitestkit_rpc_message_tail_";
constexpr const char *kRpcModule = "com.ohos.devicetest.hypiumApiHelper";

std::string JsonEscape(const std::string &in)
{
    std::string out;
    out.reserve(in.size() + 8);
    for (char c : in) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) {
                    char buf[8];
                    snprintf(buf, sizeof(buf), "\\u%04x", static_cast<unsigned char>(c));
                    out += buf;
                } else {
                    out += c;
                }
        }
    }
    return out;
}

// 大端：sid + bodyLen（小端会被服务端原样回显，实测）
std::string BuildRpcPacket(const std::string &body, uint32_t sid)
{
    std::string pkt(kRpcHead);
    uint32_t be[2];
    be[0] = htonl(sid);
    be[1] = htonl(static_cast<uint32_t>(body.size()));
    pkt.append(reinterpret_cast<const char *>(be), sizeof(be));
    pkt.append(body);
    pkt.append(kRpcTail);
    return pkt;
}

struct RpcProbeResult {
    bool connectOk = false;
    int connectErrno = 0;
    int peerPid = -1;
    int peerUid = -1;
    bool sent = false;
    size_t sentBytes = 0;
    size_t recvBytes = 0;
    bool gotTail = false;
    std::string resp;
    double connectMs = 0.0;
    double rpcMs = 0.0;
};

// 发一条 Gestures 请求并读回包；api 为空则只建链不发（用于单独验证 SELinux 门）
bool DoRpc(RpcProbeResult &r, const std::string &api, int x, int y)
{
    auto t0 = std::chrono::steady_clock::now();

    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) {
        r.connectErrno = errno;
        return false;
    }

    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    addr.sun_path[0] = '\0'; // 抽象命名空间：首字节 0，后接名字
    std::strncpy(addr.sun_path + 1, kUitestSocketName, sizeof(addr.sun_path) - 2);
    socklen_t addrLen = static_cast<socklen_t>(offsetof(sockaddr_un, sun_path) + 1 +
                                               std::strlen(kUitestSocketName));

    if (connect(fd, reinterpret_cast<sockaddr *>(&addr), addrLen) != 0) {
        r.connectErrno = errno;
        close(fd);
        return false;
    }
    r.connectOk = true;
    r.connectMs = std::chrono::duration<double, std::milli>(
                      std::chrono::steady_clock::now() - t0).count();

    // 对端凭据：确认我们连到的是 agent.so（也能反证 agent 拿到的 Peer UID 是谁）
    ucred cred{};
    socklen_t credLen = sizeof(cred);
    if (getsockopt(fd, SOL_SOCKET, SO_PEERCRED, &cred, &credLen) == 0) {
        r.peerPid = static_cast<int>(cred.pid);
        r.peerUid = static_cast<int>(cred.uid);
    }

    if (api.empty()) {
        close(fd);
        return true;
    }

    char body[256];
    snprintf(body, sizeof(body),
             "{\"module\":\"%s\",\"method\":\"Gestures\",\"params\":{\"api\":\"%s\","
             "\"args\":{\"x\":%d,\"y\":%d}}}",
             kRpcModule, api.c_str(), x, y);
    std::string pkt = BuildRpcPacket(body, 1);

    struct timeval tv{};
    tv.tv_sec = 2;
    tv.tv_usec = 0;
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));

    auto t1 = std::chrono::steady_clock::now();
    ssize_t n = send(fd, pkt.data(), pkt.size(), 0);
    if (n == static_cast<ssize_t>(pkt.size())) {
        r.sent = true;
        r.sentBytes = pkt.size();
    }

    std::string buf;
    char chunk[4096];
    while (buf.size() < 65536) {
        ssize_t got = recv(fd, chunk, sizeof(chunk), 0);
        if (got <= 0) {
            break;
        }
        buf.append(chunk, static_cast<size_t>(got));
        if (buf.find(kRpcTail) != std::string::npos) {
            r.gotTail = true;
            break;
        }
    }
    r.rpcMs = std::chrono::duration<double, std::milli>(
                  std::chrono::steady_clock::now() - t1).count();
    r.recvBytes = buf.size();
    r.resp = buf.size() > 400 ? buf.substr(0, 400) : buf;

    close(fd);
    return true;
}

// ── 扩展库自加载（2026-11-14 第二轮）────────────────────────────────────────
// 目标：让手机也能走「和平板一样」的参考采集（uitest + libscreen_casting.z.so），
// 而不必用 AVScreenCapture（那条每次都要用户点录屏授权）。
//
// 手机自带的 uitest 不触发扩展初始化（补记 73），所以改由本应用进程自己 dlopen 并调用
// UiTestExtension_OnInit。第一轮因为只补了一层依赖就判死是错的 —— 依赖可以逐层补齐，
// 现在 240 个 so 已打进 HAP（tablet/entry/libs/arm64-v8a）。
//
// 加载前必须把 LD_LIBRARY_PATH 指向 libs 目录，否则 dlopen 只在默认路径找，
// 会把我们打包的这些库全当不存在。
napi_value LoadExtLibrary(napi_env env, napi_callback_info info)
{
    size_t argc = 1;
    napi_value argv[1] = {nullptr};
    napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr);

    std::string path = "/data/local/tmp/extlibs/libscreen_casting.z.so";
    if (argc >= 1 && argv[0] != nullptr) {
        size_t len = 0;
        if (napi_get_value_string_utf8(env, argv[0], nullptr, 0, &len) == napi_ok && len > 0) {
            std::vector<char> tmp(len + 1);
            napi_get_value_string_utf8(env, argv[0], tmp.data(), tmp.size(), &len);
            path.assign(tmp.data(), len);
        }
    }

    // 关键：把打包目录加进搜索路径
    const char* libDir = "/data/local/tmp/extlibs";
    setenv("LD_LIBRARY_PATH", libDir, 1);

    void* handle = dlopen(path.c_str(), RTLD_NOW | RTLD_GLOBAL);
    const char* err = handle == nullptr ? dlerror() : nullptr;
    void* symInit = handle == nullptr ? nullptr : dlsym(handle, "UiTestExtension_OnInit");

    char out[1200];
    snprintf(out, sizeof(out),
             "{\"ok\":%s,\"path\":\"%s\",\"handle\":\"%p\",\"onInit\":\"%p\",\"err\":\"%s\"}",
             (handle != nullptr && symInit != nullptr) ? "true" : "false", JsonEscape(path).c_str(),
             handle, symInit, JsonEscape(err == nullptr ? "" : err).c_str());
    CAPI_LOGI("loadExtLibrary ldpath=%{public}s dlopen=%{public}s onInit=%{public}p err=%{public}s",
              libDir, handle != nullptr ? "ok" : "fail", symInit, err == nullptr ? "-" : err);

    napi_value ret = nullptr;
    napi_create_string_utf8(env, out, NAPI_AUTO_LENGTH, &ret);
    return ret;
}

// 真正调用 UiTestExtension_OnInit。会阻塞（内部起 grpc server）。
napi_value CallExtInit(napi_env env, napi_callback_info info)
{
    size_t argc = 1;
    napi_value argv[1] = {nullptr};
    napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr);

    std::string path = "/data/local/tmp/extlibs/libscreen_casting.z.so";
    if (argc >= 1 && argv[0] != nullptr) {
        size_t len = 0;
        if (napi_get_value_string_utf8(env, argv[0], nullptr, 0, &len) == napi_ok && len > 0) {
            std::vector<char> tmp(len + 1);
            napi_get_value_string_utf8(env, argv[0], tmp.data(), tmp.size(), &len);
            path.assign(tmp.data(), len);
        }
    }

    const char* libDir = "/data/local/tmp/extlibs";
    setenv("LD_LIBRARY_PATH", libDir, 1);

    void* handle = dlopen(path.c_str(), RTLD_NOW | RTLD_GLOBAL);
    if (handle == nullptr) {
        const char* err = dlerror();
        char out[600];
        snprintf(out, sizeof(out), "{\"ok\":false,\"stage\":\"dlopen\",\"err\":\"%s\"}",
                 JsonEscape(err == nullptr ? "" : err).c_str());
        CAPI_LOGE("callExtInit dlopen failed: %{public}s", err == nullptr ? "-" : err);
        napi_value ret = nullptr;
        napi_create_string_utf8(env, out, NAPI_AUTO_LENGTH, &ret);
        return ret;
    }

    using OnInitFn = void (*)(int, char**);
    auto onInit = reinterpret_cast<OnInitFn>(dlsym(handle, "UiTestExtension_OnInit"));
    if (onInit == nullptr) {
        napi_value ret = nullptr;
        napi_create_string_utf8(env, "{\"ok\":false,\"stage\":\"dlsym\"}", NAPI_AUTO_LENGTH, &ret);
        return ret;
    }

    // 参数与 PC 端启动 uitest daemon 时一致
    static std::vector<std::string> args = {
        "libscreen_casting.z.so", "-scale", "2", "-frameRefreshInterval", "20",
        "-bitRate", "20000000", "-p", "5000", "-iFrameInterval", "1000",
        "-repeatInterval", "16",
    };
    std::vector<char*> cargv;
    for (auto& a : args) {
        cargv.push_back(const_cast<char*>(a.c_str()));
    }
    CAPI_LOGI("callExtInit invoking OnInit argc=%{public}zu", cargv.size());
    onInit(static_cast<int>(cargv.size()), cargv.data());
    CAPI_LOGI("callExtInit OnInit returned");

    napi_value ret = nullptr;
    napi_create_string_utf8(env, "{\"ok\":true,\"stage\":\"called\"}", NAPI_AUTO_LENGTH, &ret);
    return ret;
}

// probeUitestSocket(api: string, x: number, y: number) -> string(JSON)
// api 传 "" 时只建链不发请求。
napi_value ProbeUitestSocket(napi_env env, napi_callback_info info)
{
    size_t argc = 3;
    napi_value argv[3] = {nullptr, nullptr, nullptr};
    napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr);

    std::string api;
    int32_t x = 800;
    int32_t y = 1900;
    if (argc >= 1 && argv[0] != nullptr) {
        size_t len = 0;
        if (napi_get_value_string_utf8(env, argv[0], nullptr, 0, &len) == napi_ok && len > 0) {
            std::vector<char> tmp(len + 1);
            napi_get_value_string_utf8(env, argv[0], tmp.data(), tmp.size(), &len);
            api.assign(tmp.data(), len);
        }
    }
    if (argc >= 2 && argv[1] != nullptr) {
        napi_get_value_int32(env, argv[1], &x);
    }
    if (argc >= 3 && argv[2] != nullptr) {
        napi_get_value_int32(env, argv[2], &y);
    }

    RpcProbeResult r;
    bool okCall = DoRpc(r, api, x, y);

    char head[512];
    snprintf(head, sizeof(head),
             "{\"connectOk\":%s,\"connectErrno\":%d,\"peerPid\":%d,\"peerUid\":%d,"
             "\"sent\":%s,\"sentBytes\":%zu,\"recvBytes\":%zu,\"gotTail\":%s,"
             "\"connectMs\":%.3f,\"rpcMs\":%.3f,\"api\":\"%s\",\"resp\":\"%s\"}",
             r.connectOk ? "true" : "false", r.connectErrno, r.peerPid, r.peerUid,
             r.sent ? "true" : "false", r.sentBytes, r.recvBytes,
             r.gotTail ? "true" : "false", r.connectMs, r.rpcMs, api.c_str(),
             JsonEscape(r.resp).c_str());
    CAPI_LOGI("probeUitestSocket api=%{public}s connect=%{public}s errno=%{public}d "
              "peerUid=%{public}d recv=%{public}zu tail=%{public}d ms=%.2f",
              api.c_str(), r.connectOk ? "ok" : "fail", r.connectErrno, r.peerUid,
              r.recvBytes, r.gotTail ? 1 : 0, r.rpcMs);
    (void)okCall;

    napi_value out = nullptr;
    napi_create_string_utf8(env, head, NAPI_AUTO_LENGTH, &out);
    return out;
}

napi_value Init(napi_env env, napi_value exports)
{
    napi_property_descriptor desc[] = {
        {"startCapture", nullptr, StartCapture, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"stopCapture", nullptr, StopCapture, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"getStats", nullptr, GetStats, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"setStreamPeer", nullptr, SetStreamPeer, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"setDirectSend", nullptr, SetDirectSend, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"setStreamActive", nullptr, SetStreamActive, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"probeUitestSocket", nullptr, ProbeUitestSocket, nullptr, nullptr, nullptr, napi_default,
         nullptr},
        {"loadExtLibrary", nullptr, LoadExtLibrary, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"callExtInit", nullptr, CallExtInit, nullptr, nullptr, nullptr, napi_default, nullptr},
    };
    napi_define_properties(env, exports, sizeof(desc) / sizeof(desc[0]), desc);
    CAPI_LOGI("harmoscap module loaded");
    return exports;
}

napi_module g_module = {
    .nm_version = 1,
    .nm_flags = 0,
    .nm_filename = nullptr,
    .nm_register_func = Init,
    .nm_modname = "harmoscap",
    .nm_priv = nullptr,
    .reserved = {0},
};

} // namespace

extern "C" __attribute__((constructor)) void RegisterHarmOsCapModule(void)
{
    napi_module_register(&g_module);
}
