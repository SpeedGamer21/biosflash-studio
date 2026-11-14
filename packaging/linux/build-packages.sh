#!/usr/bin/env bash
# Builds BiosFlash Studio for Linux and packages it.
#
#   ./packaging/linux/build-packages.sh [x64|arm64] [--prebuilt]
#
# --prebuilt packages an existing dist/linux-<arch>/BiosFlashStudio (e.g. cross-built on Windows with
# "build.bat linux") instead of running dotnet publish, so the .NET SDK is not needed.
#
# Needs the .NET 10 SDK (https://dot.net). Produces in dist/:
#   biosflash-studio-<ver>-linux-<arch>.tar.gz   (always; install with: sudo sh install.sh)
#   biosflash-studio_<ver>_<amd64|arm64>.deb     (if dpkg-deb is available  - Debian/Ubuntu)
#   biosflash-studio-<ver>-1.<x86_64|aarch64>.rpm (if rpmbuild is available - Fedora/RHEL: sudo dnf install rpm-build)
set -euo pipefail

PREBUILT=0
ARGS=()
for a in "$@"; do case "$a" in --prebuilt) PREBUILT=1 ;; *) ARGS+=("$a") ;; esac; done
ARCH="${ARGS[0]:-x64}"
case "$ARCH" in
    x64)   DEBARCH=amd64; RPMARCH=x86_64 ;;
    arm64) DEBARCH=arm64; RPMARCH=aarch64 ;;
    *) echo "usage: $0 [x64|arm64]" >&2; exit 1 ;;
esac

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/BiosFlashStudio.csproj" | head -1)"
VERSION="${VERSION:-2.1.0}"
OUT="$ROOT/dist/linux-$ARCH"
DIST="$ROOT/dist/installers"
mkdir -p "$DIST"

if [ "$PREBUILT" = 1 ]; then
    [ -f "$OUT/BiosFlashStudio" ] || { echo "error: $OUT/BiosFlashStudio not found (run 'build.bat linux' first)" >&2; exit 1; }
    echo "==> Packaging prebuilt $OUT/BiosFlashStudio ($VERSION, linux-$ARCH)"
else
    echo "==> Publishing BiosFlash Studio $VERSION for linux-$ARCH"
    rm -rf "$OUT"
    dotnet publish "$ROOT/src/BiosFlashStudio.csproj" -c Release -r "linux-$ARCH" --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
        -p:DebugType=none -o "$OUT"
fi
chmod 0755 "$OUT/BiosFlashStudio"
for f in install.sh uninstall.sh biosflash-studio.desktop biosflash-studio.svg 60-biosflash-ch341a.rules README-LINUX.md; do
    cp "$HERE/$f" "$OUT/"
done
chmod 0755 "$OUT/install.sh" "$OUT/uninstall.sh"

echo "==> tar.gz"
"$HERE/tarball.sh" "$ARCH" "$VERSION"

# ---------------------------------------------------------------- staging tree shared by deb + rpm
STAGE="$(mktemp -d)"
chmod 0755 "$STAGE"
trap 'rm -rf "$STAGE"' EXIT
install -Dm0755 "$OUT/BiosFlashStudio" "$STAGE/opt/biosflash-studio/BiosFlashStudio"
install -Dm0644 "$HERE/README-LINUX.md" "$STAGE/opt/biosflash-studio/README-LINUX.md"
install -d "$STAGE/usr/bin"
ln -s /opt/biosflash-studio/BiosFlashStudio "$STAGE/usr/bin/biosflash-studio"
install -Dm0644 "$HERE/biosflash-studio.desktop" "$STAGE/usr/share/applications/biosflash-studio.desktop"
install -Dm0644 "$HERE/biosflash-studio.svg" "$STAGE/usr/share/icons/hicolor/scalable/apps/biosflash-studio.svg"
install -Dm0644 "$HERE/60-biosflash-ch341a.rules" "$STAGE/usr/lib/udev/rules.d/60-biosflash-ch341a.rules"

POSTINST='#!/bin/sh
if command -v udevadm >/dev/null 2>&1; then
  udevadm control --reload-rules || true
  udevadm trigger --subsystem-match=usb --attr-match=idVendor=1a86 || true
fi
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q /usr/share/icons/hicolor || true
exit 0'

DESC="CH341A SPI/I2C BIOS programmer with firmware analysis
 Reads, erases, programs and verifies 25xx SPI flash and 24xx I2C EEPROM chips
 with CH341A USB programmers. Includes an Intel/AMD firmware analyzer
 (descriptor, ME, DMI, OEM key, NVRAM), a hex editor and an ASUS .CAP tool."

# ---------------------------------------------------------------- .deb
if command -v dpkg-deb >/dev/null 2>&1; then
    echo "==> .deb"
    DEB="$(mktemp -d)"
    chmod 0755 "$DEB"
    cp -a "$STAGE/." "$DEB/"
    install -d "$DEB/DEBIAN"
    SIZE_KB="$(du -sk "$DEB" | cut -f1)"
    cat > "$DEB/DEBIAN/control" <<EOF
Package: biosflash-studio
Version: $VERSION
Section: electronics
Priority: optional
Architecture: $DEBARCH
Installed-Size: $SIZE_KB
Depends: libusb-1.0-0, libfontconfig1, libx11-6, libice6, libsm6
Recommends: libgtk-3-0, xdg-desktop-portal
Maintainer: BiosFlash Studio <noreply@localhost>
Description: $DESC
EOF
    printf '%s\n' "$POSTINST" > "$DEB/DEBIAN/postinst"
    printf '%s\n' "$POSTINST" > "$DEB/DEBIAN/postrm"
    chmod 0755 "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"
    dpkg-deb --root-owner-group --build "$DEB" "$DIST/biosflash-studio_${VERSION}_${DEBARCH}.deb"
    rm -rf "$DEB"
else
    echo "(dpkg-deb not found - skipping .deb)"
fi

# ---------------------------------------------------------------- .rpm
if command -v rpmbuild >/dev/null 2>&1; then
    echo "==> .rpm"
    TOP="$(mktemp -d)"
    mkdir -p "$TOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
    SPEC="$TOP/SPECS/biosflash-studio.spec"
    cat > "$SPEC" <<EOF
Name:           biosflash-studio
Version:        $VERSION
Release:        1
Summary:        CH341A SPI/I2C BIOS programmer with firmware analysis
License:        GPL-3.0-or-later
URL:            https://github.com/SpeedGamer21/biosflash-studio
AutoReqProv:    no
Requires:       libusb1, fontconfig, libX11, libICE, libSM
%global debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none

%description
$(printf '%s\n' "$DESC" | sed 's/^ //')

%install
mkdir -p %{buildroot}
cp -a $STAGE/. %{buildroot}/

%post
$(printf '%s\n' "$POSTINST" | sed '1d;$d')

%postun
command -v udevadm >/dev/null 2>&1 && udevadm control --reload-rules || true

%files
/opt/biosflash-studio
/usr/bin/biosflash-studio
/usr/share/applications/biosflash-studio.desktop
/usr/share/icons/hicolor/scalable/apps/biosflash-studio.svg
/usr/lib/udev/rules.d/60-biosflash-ch341a.rules
EOF
    rpmbuild --quiet -bb --target "$RPMARCH" --define "_topdir $TOP" "$SPEC"
    cp "$TOP"/RPMS/*/*.rpm "$DIST/"
    rm -rf "$TOP"
else
    echo "(rpmbuild not found - skipping .rpm; Fedora: sudo dnf install rpm-build)"
fi

echo
echo "Done. Packages in $DIST:"
ls -1 "$DIST" | grep -E 'biosflash-studio.*\.(deb|rpm|tar\.gz)$' || true
