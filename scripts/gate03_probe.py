#!/usr/bin/env python3
"""GATE-03：真机对比 uitest / uinput 两条输入注入通道的端到端耗时。

严格串行调用 scripts/hdcw.sh（INV-02），每次测量的是「发起 shell 命令 → 返回」的墙钟耗时，
包含 hdc tconn 握手开销，因此两条通道在同一基准下可比，但绝对值不等于设备侧注入耗时。

用法: python scripts/gate03_probe.py [--count 8] [--out tmp/gate03_result.json]
"""
import argparse
import json
import os
import shutil
import statistics
import subprocess
import time
from datetime import datetime

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASH = shutil.which("bash") or r"C:\Program Files\Git\bin\bash.exe"

COMMANDS = {
    "uitest": "uitest uiInput click 800 2000",
    "uinput": "uinput -T -c 800 2000 -d 1 -u 1",
}

# hdc 进程退出码恒为 0，必须靠输出文本判断设备侧是否真的执行成功。
FAIL_MARKERS = (
    "parameter error",
    "unable to run",
    "Not match target",
    "ExecuteCommand need connect-key",
    "Not support stdio",
    "[Fail]",
)


def invoke(shell_cmd: str):
    # 不用 -l：login shell 会加载 MSYS2 profile 并报 /etc/msystem.d/MSYS 缺失，
    # 且路径转换由 hdcw.sh 内部 export 处理，外层无需再设。
    wrapper = './scripts/hdcw.sh shell "%s"' % shell_cmd
    t0 = time.perf_counter()
    try:
        p = subprocess.run(
            [BASH, "-c", wrapper],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=30,
        )
        elapsed = (time.perf_counter() - t0) * 1000
        out = (p.stdout + p.stderr).strip()
        ok = p.returncode == 0 and not any(m in out for m in FAIL_MARKERS)
        return elapsed, out, ok
    except subprocess.TimeoutExpired:
        return 30000.0, "timeout", False


def summarize(samples):
    ok = [s["ms"] for s in samples if s["ok"]]
    if not ok:
        return {"n": len(samples), "success": 0, "p50": None, "max": None}
    return {
        "n": len(samples),
        "success": len(ok),
        "p50": round(statistics.median(ok), 1),
        "p95": round(sorted(ok)[min(len(ok) - 1, int(len(ok) * 0.95))], 1),
        "min": round(min(ok), 1),
        "max": round(max(ok), 1),
        "mean": round(statistics.mean(ok), 1),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--count", type=int, default=8)
    ap.add_argument("--out", default="tmp/gate03_result.json")
    args = ap.parse_args()

    result = {
        "gate": "GATE-03",
        "timestamp": datetime.now().isoformat(timespec="seconds"),
        "method": "hdcw.sh shell 串行调用，墙钟耗时（含 tconn 握手）",
        "channels": {},
    }
    for name, cmd in COMMANDS.items():
        samples = []
        print(f"[{name}] {cmd}")
        for i in range(args.count):
            ms, out, ok = invoke(cmd)
            samples.append({"i": i + 1, "ms": round(ms, 1), "ok": ok,
                            "output": out[-120:]})
            print(f"  #{i + 1}: {ms:7.1f}ms ok={ok} {out[-60:]}")
        result["channels"][name] = {
            "command": cmd,
            "stats": summarize(samples),
            "samples": samples,
        }

    u = result["channels"]["uitest"]["stats"]
    v = result["channels"]["uinput"]["stats"]
    result["hdc_reconnect_note"] = (
        "hdc TCP 会话跨进程即掉线，每条命令都要重新 tconn；"
        "已连接态单次 hdc 命令约 170ms，但不可复用。常驻 hdc shell 实测返回 "
        "'Not support stdio TTY mode' / 'Not match target founded'，不可行。"
        "因此下面的毫秒数包含重连握手，是 DEC-02 通道的真实端到端代价。"
    )
    if u["success"] == 0 and v["success"] == 0:
        result["verdict"] = "INCOMPLETE"
        result["reason"] = "两条通道均无成功样本"
    elif v["success"] == 0:
        result["verdict"] = "uitest"
        result["reason"] = f"uinput 命令参数不被设备接受（0/{v['n']} 成功）；uitest {u['success']}/{u['n']} 成功，p50={u['p50']}ms"
    elif u["success"] == 0:
        result["verdict"] = "uinput"
        result["reason"] = f"uitest 全部失败；uinput {v['success']}/{v['n']} 成功，p50={v['p50']}ms"
    else:
        winner = "uitest" if u["p50"] <= v["p50"] else "uinput"
        result["verdict"] = winner
        result["reason"] = f"p50 {u['p50']}ms vs {v['p50']}ms（均含 hdc 重连）"
    result["gate_verdict"] = (
        "FAIL — DEC-02 的「单点注入 <30ms」不成立："
        "hdc 通道每条命令需 2~3s 重连，且无法保持长会话"
    )

    out_path = os.path.join(ROOT, args.out)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=2)
    print(f"\n判定: {result['verdict']} — {result['reason']}")
    print(f"报告: {out_path}")


if __name__ == "__main__":
    main()
