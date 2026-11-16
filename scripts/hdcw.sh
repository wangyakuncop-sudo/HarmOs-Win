#!/usr/bin/env bash
# hdcw —— hdc 统一封装（HarmOs-Win 专用）
#
# 解决四个实测坑（2026-11-07 逐条验证）：
#   1. TCP 会话跨 shell 进程就掉      → 每次调用前自动 tconn（仅无线模式）
#   2. 并发调用 hdc 互相踢掉会话       → mkdir 锁强制串行
#   3. Git Bash 转换 /data/... 路径    → 导出 MSYS2_ARG_CONV_EXCL
#   4. hdc file 拼绝对路径（含中文失败）→ 统一 cd + 相对文件名
#
# 设备（端口动态，重启或开关无线调试后会变）：
#   tablet  192.168.1.100  默认 38803
#   phone   192.168.1.102  默认 40663   （用户 2026-11-16 18:18 最终裁定）
#
# 模式：
#   wireless（默认，INV-01）  自动 tconn 到 IP:port
#   usb（仅 P2P 验证期临时破例，用户 2026-11-08 批准）
#     → 跳过 tconn，直连 USB target；验证完必须 mode wireless 并补测无线 hdc
#
# 用法：
#   ./hdcw.sh status
#   ./hdcw.sh dev phone|tablet
#   ./hdcw.sh mode wireless|usb|show          # 切换调试通道
#   ./hdcw.sh shell "param get const.product.model"
#   ./hdcw.sh shell "snapshot_display -f /data/local/tmp/s.jpeg"
#   ./hdcw.sh recv /data/local/tmp/s.jpeg s.jpeg     # 落在 tmp/
#   ./hdcw.sh send probe.txt /data/local/tmp/p.txt
#   ./hdcw.sh install app.hap -r
#   ./hdcw.sh port 40001                    # 改当前设备端口
#   ./hdcw.sh keepon [毫秒]                 # 覆盖息屏时间，默认 1800000(30min)
#   ./hdcw.sh keepoff                      # 恢复系统息屏时间（退出前必做）
#   ./hdcw.sh screenoff                    # 查当前电源态与 ScreenOffTime
#   ./hdcw.sh dim [次数]                    # 系统亮度压到最低（视觉息屏 S1），默认 6
#   ./hdcw.sh undim                        # 按记录次数恢复亮度
#   ./hdcw.sh brightness                   # 查当前亮度与自动亮度状态
#   ./hdcw.sh raw shell echo hi             # 逃生口
#
# dim 的实测依据（2026-11-08，GATE-S1）：
#   uitest uiInput keyEvent 41 (KEYCODE_BRIGHTNESS_DOWN) 可改系统亮度，与应用是否前台无关；
#   连按 6 次 49→1(Min)，15s 不回弹；按 40 (BRIGHTNESS_UP) 同次数可恢复 48。
#   亮度=1 时 snapshot_display 截图内容完全正常（物理背光不影响合成像素），
#   因此 AVScreenCapture 也能拿到非黑画面。禁止改用合成层黑遮罩（会被一起采走）。
#   KEYCODE_BRIGHTNESS_MIN(2724) / ZERO(2660) 实测无效，只有 41/40 有效。
#
# keepon 的三个实测约束（2026-11-07）：
#   - hdc 断连/常亮 3 分钟不衰减，但 SLEEP 后被压到 10000ms、解锁后清除 → 需心跳重发
#   - 只防自动超时息屏，挡不住手动电源键
#   - 若异常退出时覆盖停在 10000ms，设备 10 秒即息屏 → 退出前务必 keepoff

set -uo pipefail
export MSYS2_ARG_CONV_EXCL='*'

HDC="E:/DevEco Studio/sdk/default/openharmony/toolchains/hdc.exe"
ROOT="/c/Users/W/Desktop/HarmOS/HarmOs-Win"
LOCAL_DIR="$ROOT/tmp"
STATE_DIR="$ROOT/.hdcw"
LOCK_DIR="$STATE_DIR/.lock"

mkdir -p "$LOCAL_DIR" "$STATE_DIR"

TABLET_IP="192.168.1.100"
PHONE_IP="192.168.1.102"
USB_TARGET="${HDCW_USB_TARGET:-6MFBB25C03202906}"

# 当前设备（默认 tablet）
CUR_FILE="$STATE_DIR/current"
if [[ -f "$CUR_FILE" ]]; then
  DEV_NAME="$(tr -d ' \r\n' < "$CUR_FILE")"
else
  DEV_NAME="tablet"
  echo "tablet" > "$CUR_FILE"
fi

case "$DEV_NAME" in
  tablet) DEV_IP="$TABLET_IP"; DEF_PORT=38803 ;;
  phone)  DEV_IP="$PHONE_IP";  DEF_PORT=40663 ;;
  *)      DEV_IP="$TABLET_IP"; DEF_PORT=38803; DEV_NAME="tablet" ;;
esac

PORT_FILE="$STATE_DIR/port.$DEV_NAME"
if [[ -n "${HDCW_PORT:-}" ]]; then
  DEV_PORT="$HDCW_PORT"
elif [[ -f "$PORT_FILE" ]]; then
  DEV_PORT="$(tr -d ' \r\n' < "$PORT_FILE")"
else
  DEV_PORT="$DEF_PORT"
fi
DEV="$DEV_IP:$DEV_PORT"

# 当前调试模式（默认无线）
MODE_FILE="$STATE_DIR/mode"
if [[ -f "$MODE_FILE" ]]; then
  HDCW_MODE="$(tr -d ' \r\n' < "$MODE_FILE")"
else
  HDCW_MODE="wireless"
  echo "wireless" > "$MODE_FILE"
fi
[[ "$HDCW_MODE" == "usb" || "$HDCW_MODE" == "wireless" ]] || HDCW_MODE="wireless"

KEEP_FILE="$STATE_DIR/keepon.$DEV_NAME"
DIM_FILE="$STATE_DIR/dim.$DEV_NAME"

acquire_lock() {
  local n=0
  until mkdir "$LOCK_DIR" 2>/dev/null; do
    n=$((n+1))
    if (( n > 200 )); then
      echo "[hdcw] 等锁超时，强制清理" >&2
      rmdir "$LOCK_DIR" 2>/dev/null
      mkdir "$LOCK_DIR" 2>/dev/null
      break
    fi
    sleep 0.1
  done
}
release_lock() { rmdir "$LOCK_DIR" 2>/dev/null; }
trap release_lock EXIT

# -t 参数：USB 模式用序列号，无线模式用 IP:port
target_arg() {
  if [[ "$HDCW_MODE" == "usb" ]]; then
    echo "$USB_TARGET"
  else
    echo "$DEV"
  fi
}

ensure_conn() {
  local out list
  if [[ "$HDCW_MODE" == "usb" ]]; then
    list=$("$HDC" list targets 2>&1 | tr -d '\r')
    if [[ "$list" == *"$USB_TARGET"* ]]; then
      return 0
    fi
    echo "[hdcw] USB 模式但未发现 $USB_TARGET" >&2
    echo "[hdcw] list targets: $list" >&2
    echo "[hdcw] 检查 USB 线/授权，或设 HDCW_USB_TARGET=<序列号>" >&2
    return 1
  fi
  out=$("$HDC" tconn "$DEV" 2>&1 | tr -d '\r')
  case "$out" in
    # 2026-11-08 实测：已连接时 hdc 返回 [Info]Target is connected, repeat operation，
    # 不是 Connect OK。只认后者会让每次调用都白跑 kill→start→sleep 1（+3.4s），
    # 直接毁掉输入注入延迟。三种「已连接」表述都必须视为成功。
    *"Connect OK"*|*"Target is connected"*|*"already connected"*) return 0 ;;
  esac
  "$HDC" kill >/dev/null 2>&1
  "$HDC" start >/dev/null 2>&1
  sleep 1
  out=$("$HDC" tconn "$DEV" 2>&1 | tr -d '\r')
  case "$out" in
    *"Connect OK"*|*"Target is connected"*|*"already connected"*) return 0 ;;
  esac
  echo "[hdcw] 连接失败（$DEV_NAME @ $DEV）：$out" >&2
  echo "[hdcw] 检查：设备无线调试是否开启？端口变了吗？  hdcw.sh port <新端口>" >&2
  return 1
}

cmd="${1:-}"; shift || true

case "$cmd" in
  dev)
    t="${1:-}"
    if [[ "$t" != "tablet" && "$t" != "phone" ]]; then
      echo "用法: hdcw.sh dev tablet|phone"; exit 1
    fi
    echo "$t" > "$CUR_FILE"
    echo "[hdcw] 当前设备切换为 $t"
    exit 0
    ;;
  port)
    p="${1:-}"
    if [[ -z "$p" ]]; then echo "用法: hdcw.sh port <端口号>"; exit 1; fi
    echo "$p" > "$PORT_FILE"
    echo "[hdcw] $DEV_NAME 端口已改为 $DEV_IP:$p"
    exit 0
    ;;
  mode)
    m="${1:-show}"
    case "$m" in
      wireless|usb)
        echo "$m" > "$MODE_FILE"
        echo "[hdcw] 调试模式切换为 $m"
        if [[ "$m" == "wireless" ]]; then
          echo "[hdcw] INV-01：P2P 验证结束后恢复无线，请复测 hdc 通道与输入注入"
        else
          echo "[hdcw] INV-01 临时破例：仅 P2P 验证期；USB 在位会踢掉 TCP 会话"
          echo "[hdcw] 验证完务必 mode wireless；此模式下 hdc 可用不能证明无线形态可用"
        fi
        ;;
      show)
        echo "调试模式: $HDCW_MODE  设备: $DEV_NAME  目标: $(target_arg)"
        ;;
      *)
        echo "用法: hdcw.sh mode wireless|usb|show"; exit 1
        ;;
    esac
    exit 0
    ;;
  dim)
    n="${1:-6}"
    acquire_lock; ensure_conn || exit 1
    for ((i = 0; i < n; i++)); do
      "$HDC" -t "$(target_arg)" shell "uitest uiInput keyEvent 41" >/dev/null 2>&1
    done
    echo "$n" > "$DIM_FILE"
    "$HDC" -t "$(target_arg)" shell "hidumper -s DisplayPowerManagerService -a -h" 2>&1 \
      | grep -E "Display Id=|Brightness=|Auto Adjust" | tr -d '\r'
    echo "[hdcw] 已压低系统亮度；退出前执行 undim 恢复"
    ;;
  undim)
    if [[ -f "$DIM_FILE" ]]; then
      n="$(tr -d ' \r\n' < "$DIM_FILE")"
    else
      n="${1:-6}"
    fi
    acquire_lock; ensure_conn || exit 1
    for ((i = 0; i < n; i++)); do
      "$HDC" -t "$(target_arg)" shell "uitest uiInput keyEvent 40" >/dev/null 2>&1
    done
    rm -f "$DIM_FILE"
    "$HDC" -t "$(target_arg)" shell "hidumper -s DisplayPowerManagerService -a -h" 2>&1 \
      | grep -E "Display Id=|Brightness=|Auto Adjust" | tr -d '\r'
    echo "[hdcw] 已恢复亮度"
    ;;
  brightness)
    acquire_lock; ensure_conn || exit 1
    "$HDC" -t "$(target_arg)" shell "hidumper -s DisplayPowerManagerService -a -h" 2>&1 \
      | grep -E "Display Id=|Brightness=|Auto Adjust|Limits" | tr -d '\r'
    ;;
  keepon)
    ms="${1:-1800000}"
    acquire_lock; ensure_conn || exit 1
    out=$("$HDC" -t "$(target_arg)" shell "power-shell timeout -o $ms" 2>&1 | tr -d '\r')
    echo "$out"
    if [[ "$out" == *"Override screen off time"* ]]; then
      date +%s > "$KEEP_FILE"
      echo "[hdcw] 已覆盖息屏时间为 ${ms}ms"
      echo "[hdcw] 注意：SLEEP/解锁会清除覆盖，需 ≤10s 心跳重发；退出前执行 keepoff"
    else
      echo "[hdcw] 覆盖失败" >&2; exit 1
    fi
    ;;
  keepoff)
    acquire_lock; ensure_conn || exit 1
    "$HDC" -t "$(target_arg)" shell "power-shell timeout -r" 2>&1 | tr -d '\r'
    rm -f "$KEEP_FILE"
    echo "[hdcw] 已恢复系统息屏时间"
    ;;
  screenoff)
    acquire_lock; ensure_conn || exit 1
    "$HDC" -t "$(target_arg)" shell "hidumper -s PowerManagerService -a -s" 2>&1 \
      | grep -E "Current State|ScreenOffTime" | tr -d '\r'
    ;;
  raw)
    acquire_lock; ensure_conn || exit 1
    "$HDC" -t "$(target_arg)" "$@"
    exit $?
    ;;
  shell)
    acquire_lock; ensure_conn || exit 1
    "$HDC" -t "$(target_arg)" shell "$@" | tr -d '\r'
    exit ${PIPESTATUS[0]}
    ;;
  send)
    src="${1:?需要本地文件名}"; dst="${2:?需要设备路径}"
    acquire_lock; ensure_conn || exit 1
    ( cd "$LOCAL_DIR" && "$HDC" -t "$(target_arg)" file send "$src" "$dst" )
    ;;
  recv)
    src="${1:?需要设备路径}"; dst="${2:?需要本地文件名}"
    acquire_lock; ensure_conn || exit 1
    ( cd "$LOCAL_DIR" && "$HDC" -t "$(target_arg)" file recv "$src" "$dst" )
    ;;
  install)
    hap="${1:?需要 hap 文件名（放 tmp/ 下）}"; shift
    acquire_lock; ensure_conn || exit 1
    ( cd "$LOCAL_DIR" && "$HDC" -t "$(target_arg)" install "$hap" "$@" )
    ;;
  status)
    acquire_lock
    echo "调试模式: $HDCW_MODE  当前设备: $DEV_NAME  目标: $(target_arg)"
    if [[ "$HDCW_MODE" == "wireless" ]]; then
      handshake=$("$HDC" tconn "$DEV" 2>&1 | tr -d '\r')
      echo "握手: $handshake"
      # 与 ensure_conn 保持一致：已连接态 hdc 返回 "Target is connected, repeat operation"
      case "$handshake" in
        *"Connect OK"*|*"Target is connected"*|*"already connected"*) ;;
        *)
          echo "[hdcw] 未连上，检查设备无线调试状态或端口" >&2
          exit 1
          ;;
      esac
    else
      handshake=$("$HDC" list targets 2>&1 | tr -d '\r')
      if [[ "$handshake" != *"$USB_TARGET"* ]]; then
        echo "[hdcw] USB 目标 $USB_TARGET 未出现" >&2
        exit 1
      fi
      echo "USB 目标: 已连接"
    fi
    echo "目标列表:"
    "$HDC" list targets -v 2>&1 | grep -v "^COM" | head -5 || true
    if [[ -f "$KEEP_FILE" ]]; then
      echo "[hdcw] 注意：keepon 状态文件存在，退出前请执行 keepoff"
    fi
    ;;
  *)
    sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
    ;;
esac
