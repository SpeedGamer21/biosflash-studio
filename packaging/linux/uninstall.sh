#!/bin/sh
# Removes a BiosFlash Studio installation made with install.sh (settings in ~/.config/BiosFlashStudio are kept).
set -e
if [ "$(id -u)" -ne 0 ]; then exec sudo sh "$0" "$@"; fi
rm -f /usr/local/bin/biosflash-studio
rm -f /usr/share/applications/biosflash-studio.desktop
rm -f /usr/share/icons/hicolor/scalable/apps/biosflash-studio.svg
rm -f /etc/udev/rules.d/60-biosflash-ch341a.rules
command -v udevadm >/dev/null 2>&1 && udevadm control --reload-rules || true
rm -rf /opt/biosflash-studio
echo "BiosFlash Studio removed."
