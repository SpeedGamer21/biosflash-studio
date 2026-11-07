#!/usr/bin/env bash
# Creates dist/installers/biosflash-studio-<ver>-linux-<arch>.tar.gz with proper Unix permissions.
# Works on Linux (and WSL on a Windows drive) and in Git Bash on Windows (used by build.bat).
set -euo pipefail
ARCH="$1"; VERSION="${2:-2.0.0}"
cd "$(dirname "$0")/../../dist"
mkdir -p installers
NAME="biosflash-studio-$VERSION"
OUTF="installers/$NAME-linux-$ARCH.tar.gz"

if [ "$(uname -s)" = "Linux" ]; then
    # copy to a native filesystem and set modes explicitly (files on /mnt/<drive> all look like 0777)
    S="$(mktemp -d)"
    trap 'rm -rf "$S"' EXIT
    mkdir -p "$S/$NAME"
    cp -r "linux-$ARCH/." "$S/$NAME/"
    find "$S/$NAME" -type d -exec chmod 0755 {} +
    find "$S/$NAME" -type f -exec chmod 0644 {} +
    chmod 0755 "$S/$NAME/BiosFlashStudio" "$S/$NAME/install.sh" "$S/$NAME/uninstall.sh"
    tar --owner=0 --group=0 -C "$S" -czf "$OUTF" "$NAME"
else
    T="$(mktemp)"
    tar --owner=0 --group=0 --mode='u=rwX,go=rX' --transform "s,^linux-$ARCH,$NAME," \
        --exclude="linux-$ARCH/BiosFlashStudio" --exclude="linux-$ARCH/install.sh" --exclude="linux-$ARCH/uninstall.sh" \
        -cf "$T" "linux-$ARCH"
    tar --owner=0 --group=0 --mode=0755 --transform "s,^linux-$ARCH,$NAME," \
        -rf "$T" "linux-$ARCH/BiosFlashStudio" "linux-$ARCH/install.sh" "linux-$ARCH/uninstall.sh"
    gzip -9 -c "$T" > "$OUTF"
    rm -f "$T"
fi
echo "created dist/$OUTF"
