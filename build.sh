#!/usr/bin/env bash
# Linux/macOS build helper.
#   ./build.sh            -> build .tar.gz (+ .deb / .rpm when dpkg-deb / rpmbuild exist) for this machine's arch
#   ./build.sh arm64      -> cross-build for ARM64
#   ./build.sh run        -> run from source (needs the .NET 10 SDK)
#   ./build.sh demo       -> run from source with the simulator + demo firmware
set -euo pipefail
cd "$(dirname "$0")"
case "${1:-}" in
  run)  exec dotnet run --project src/BiosFlashStudio.csproj -c Release ;;
  demo) exec dotnet run --project src/BiosFlashStudio.csproj -c Release -- --demo ;;
  arm64|x64) exec ./packaging/linux/build-packages.sh "$1" ;;
  "") case "$(uname -m)" in aarch64|arm64) A=arm64 ;; *) A=x64 ;; esac
      exec ./packaging/linux/build-packages.sh "$A" ;;
  *) echo "usage: $0 [x64|arm64|run|demo]"; exit 1 ;;
esac
