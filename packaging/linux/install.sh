#!/bin/sh
# BiosFlash Studio - Linux installer (Debian/Ubuntu, Fedora and most other distros)
#
#   sudo sh install.sh            install to /opt/biosflash-studio (+ dependencies)
#   sudo sh install.sh --no-deps  skip installing libusb / X11 runtime packages
#
# Run it from the folder that contains the BiosFlashStudio binary.
set -e

PREFIX=/opt/biosflash-studio
SRC=$(cd "$(dirname "$0")" && pwd)
INSTALL_DEPS=1
[ "$1" = "--no-deps" ] && INSTALL_DEPS=0

if [ "$(id -u)" -ne 0 ]; then
    echo "Root rights are needed - re-running with sudo..."
    exec sudo sh "$0" "$@"
fi

if [ ! -f "$SRC/BiosFlashStudio" ]; then
    echo "error: BiosFlashStudio binary not found next to install.sh ($SRC)" >&2
    exit 1
fi

# ---------------------------------------------------------------- dependencies
if [ "$INSTALL_DEPS" = 1 ]; then
    if command -v apt-get >/dev/null 2>&1; then
        echo "==> Installing runtime packages (apt)"
        apt-get install -y libusb-1.0-0 libfontconfig1 libx11-6 libice6 libsm6 || \
            echo "warning: apt-get failed - install libusb-1.0-0 manually"
    elif command -v dnf >/dev/null 2>&1; then
        echo "==> Installing runtime packages (dnf)"
        dnf install -y libusb1 fontconfig libX11 libICE libSM || \
            echo "warning: dnf failed - install libusb1 manually"
    elif command -v zypper >/dev/null 2>&1; then
        zypper --non-interactive install libusb-1_0-0 fontconfig libX11-6 libICE6 libSM6 || true
    elif command -v pacman >/dev/null 2>&1; then
        pacman -S --needed --noconfirm libusb fontconfig libx11 libice libsm || true
    else
        echo "note: unknown package manager - make sure libusb-1.0 is installed"
    fi
fi

# ---------------------------------------------------------------- files
echo "==> Installing to $PREFIX"
install -d "$PREFIX"
install -m 0755 "$SRC/BiosFlashStudio" "$PREFIX/BiosFlashStudio"
for f in uninstall.sh biosflash-studio.desktop biosflash-studio.svg 60-biosflash-ch341a.rules README-LINUX.md; do
    [ -f "$SRC/$f" ] && install -m 0644 "$SRC/$f" "$PREFIX/$f"
done
chmod 0755 "$PREFIX/uninstall.sh" 2>/dev/null || true
ln -sf "$PREFIX/BiosFlashStudio" /usr/local/bin/biosflash-studio

install -Dm 0644 "$SRC/biosflash-studio.desktop" /usr/share/applications/biosflash-studio.desktop
install -Dm 0644 "$SRC/biosflash-studio.svg" /usr/share/icons/hicolor/scalable/apps/biosflash-studio.svg

# ---------------------------------------------------------------- udev
echo "==> Installing udev rule for the CH341A (1a86:5512)"
install -Dm 0644 "$SRC/60-biosflash-ch341a.rules" /etc/udev/rules.d/60-biosflash-ch341a.rules
if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules || true
    udevadm trigger --subsystem-match=usb --attr-match=idVendor=1a86 || true
fi

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q /usr/share/icons/hicolor || true

echo
echo "Done. Start 'BiosFlash Studio' from the application menu or run: biosflash-studio"
echo "Re-plug the CH341A programmer so the new udev permissions apply."
