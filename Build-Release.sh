#!/usr/bin/env bash
# Linux release build: publish linux-x64, then produce tar.gz and AppImage
# artifacts in dist/ (the .deb is built separately by CI — linux-deb.yml).
# Needs the .NET 8 SDK (DOTNET env var to override) and, for the AppImage,
# appimagetool (downloaded automatically to dist/tools when missing).
set -euo pipefail
cd "$(dirname "$0")"

DOTNET=${DOTNET:-dotnet}
RID=linux-x64
REPO=$PWD
VER=$(grep -oE '"v[0-9]+\.[0-9]+\.[0-9]+"' LoopDPI.Core/UpdateService.cs | head -1 | tr -d '"v')
echo "==> PKG Sender $VER linux-x64"

# 1) Self-contained single-file publish (same flags as Build-Release.bat).
"$DOTNET" publish library/PkgSender.csproj -c Release -r $RID --self-contained true \
    /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o dist
cp payload/pkg-receiver.elf dist/
cp library/pkg_header.py dist/
chmod +x dist/PkgSender dist/pkg-receiver.elf

ART="PkgSender-$VER-linux-x64"
mkdir -p dist

# 2) Portable tar.gz (flat layout: the in-app updater extracts PkgSender
#    from the archive root and swaps it in place).
tar -czf "dist/$ART.tar.gz" -C dist PkgSender pkg-receiver.elf pkg_header.py \
    -C "$REPO/packaging" README-linux.txt

# 3) AppImage.
DEB=$(mktemp -d)
trap 'rm -rf "$DEB"' EXIT
APPDIR="$DEB/AppDir"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/icons/hicolor/1024x1024/apps"
cp dist/PkgSender dist/pkg-receiver.elf dist/pkg_header.py "$APPDIR/usr/bin/"
ln -s usr/bin/PkgSender "$APPDIR/AppRun"
cp packaging/pkg-sender.desktop "$APPDIR/"
cp library/Assets/logo.png "$APPDIR/usr/share/icons/hicolor/1024x1024/apps/pkg-sender.png"
cp library/Assets/logo.png "$APPDIR/pkg-sender.png"
TOOL=dist/tools/appimagetool-x86_64.AppImage
if [ ! -x "$TOOL" ] && ! command -v appimagetool >/dev/null 2>&1; then
    echo "==> downloading appimagetool"
    mkdir -p dist/tools
    curl -sSL -o "$TOOL" \
        https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$TOOL"
fi
if command -v appimagetool >/dev/null 2>&1; then
    appimagetool "$APPDIR" "$REPO/dist/$ART.AppImage"
else
    "$TOOL" --appimage-extract-and-run "$APPDIR" "$REPO/dist/$ART.AppImage"
fi

echo "==> done:"
ls -lh dist/$ART.*
