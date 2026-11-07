#!/bin/sh
# Starts BiosFlash Studio with the built-in simulator and a synthetic demo firmware.
cd "$(dirname "$0")"
if [ -x /opt/biosflash-studio/BiosFlashStudio ]; then exec /opt/biosflash-studio/BiosFlashStudio --demo; fi
for d in dist/linux-x64 dist/linux-arm64; do
  [ -f "$d/BiosFlashStudio" ] && { chmod +x "$d/BiosFlashStudio"; exec "$d/BiosFlashStudio" --demo; }
done
exec dotnet run --project src/BiosFlashStudio.csproj -c Release -- --demo
