#!/usr/bin/env python3
"""GATE-L0 一键测量入口。

包装已编译的 pc/HarmOsWin.Probe（.NET 8），避免记忆 dotnet 路径与参数顺序。
平板端需先打开 HarmLink →「进入 GATE-L0 网络探针」→「启动 L0 服务」。

用法：
    python scripts/l0_probe.py
    python scripts/l0_probe.py --host 192.168.1.100 --idle 12 --active 16
    python scripts/l0_probe.py --build          # 先编译 PC 端再测
"""

import argparse
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SLN = ROOT / "pc" / "HarmOsWin.sln"
DLL = ROOT / "pc" / "HarmOsWin.Probe" / "bin" / "Release" / "net8.0" / "HarmOsWin.Probe.dll"
TMP = ROOT / "tmp"


def dotnet_exe() -> str:
    user_sdk = Path.home() / ".dotnet" / "dotnet.exe"
    if user_sdk.exists():
        return str(user_sdk)
    return "dotnet"


def env() -> dict:
    e = os.environ.copy()
    user_sdk = Path.home() / ".dotnet"
    if user_sdk.exists():
        e["DOTNET_ROOT"] = str(user_sdk)
    e["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    return e


def build() -> None:
    print("[build] dotnet build HarmOsWin.sln -c Release")
    subprocess.run([dotnet_exe(), "build", str(SLN), "-c", "Release", "--nologo"],
                   check=True, env=env())


def main() -> int:
    ap = argparse.ArgumentParser(description="GATE-L0 UDP 延迟测量")
    ap.add_argument("--host", default="192.168.1.100", help="平板无线 IP")
    ap.add_argument("--idle", type=int, default=12, help="IDLE 阶段 probe 数（每 2s 一个）")
    ap.add_argument("--active", type=int, default=16, help="LIGHT/FULL 阶段 probe 数（每 1s 一个）")
    ap.add_argument("--out", default=str(TMP / "l0_result.json"), help="结果 JSON 路径")
    ap.add_argument("--build", action="store_true", help="测量前先编译 PC 端")
    args = ap.parse_args()

    if args.build or not DLL.exists():
        build()
    if not DLL.exists():
        print(f"[ERR] 未找到 {DLL}", file=sys.stderr)
        return 1

    TMP.mkdir(exist_ok=True)
    print(f"[probe] {args.host} | IDLE {args.idle}x2s | LIGHT/FULL {args.active}x1s")
    rc = subprocess.run(
        [dotnet_exe(), str(DLL), args.host, str(args.idle), str(args.active), args.out],
        cwd=str(TMP), env=env()).returncode
    if rc != 0:
        return rc
    print(f"[done] 结果：{args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
