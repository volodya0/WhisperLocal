#!/usr/bin/env bash
# Builds WhisperLocal.app for macOS:
#   1. whisper.cpp's whisper-server with Metal (once; cached in WhisperLocal.Mac/Runtime/whisper, not in git)
#   2. the .NET app, self-contained, for this Mac's architecture
#   3. the .app bundle, signed ad hoc (or with $CODESIGN_IDENTITY)
#
# Requirements: Xcode Command Line Tools (xcode-select --install), CMake (brew install cmake), .NET 8 SDK.
#
#   scripts/build-mac.sh                  # → artifacts/WhisperLocal.app
#   scripts/build-mac.sh --install        # also copies it to ~/Applications
#   scripts/build-mac.sh --dmg            # also packs it into artifacts/WhisperLocal-<arch>.dmg (drag to Applications)
#   WHISPER_CPP_TAG=v1.9.5 scripts/build-mac.sh --rebuild-whisper
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
WHISPER_CPP_TAG="${WHISPER_CPP_TAG:-v1.9.5}"
RUNTIME="$REPO/WhisperLocal.Mac/Runtime/whisper"
WORK="$REPO/artifacts/build"
APP="$REPO/artifacts/WhisperLocal.app"

INSTALL=0
DMG=0
REBUILD_WHISPER=0
for arg in "$@"; do
    case "$arg" in
        --install) INSTALL=1 ;;
        --dmg) DMG=1 ;;
        --rebuild-whisper) REBUILD_WHISPER=1 ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

case "$(uname -m)" in
    arm64) RID=osx-arm64 ;;
    x86_64) RID=osx-x64 ;;
    *) echo "Unsupported architecture $(uname -m)" >&2; exit 1 ;;
esac

need() {
    command -v "$1" >/dev/null 2>&1 || { echo "Missing '$1'. $2" >&2; exit 1; }
}
need dotnet "Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0 (or: brew install --cask dotnet-sdk@8)"

# 1. whisper-server (Metal is on by default on macOS; static so the binary is self-contained)
if [[ $REBUILD_WHISPER == 1 || ! -x "$RUNTIME/whisper-server" ]]; then
    need git "Install the Xcode Command Line Tools: xcode-select --install"
    need cmake "Install CMake: brew install cmake"
    echo "==> Building whisper.cpp $WHISPER_CPP_TAG (whisper-server, Metal)"
    rm -rf "$WORK/whisper.cpp"
    mkdir -p "$WORK"
    git clone --depth 1 --branch "$WHISPER_CPP_TAG" https://github.com/ggml-org/whisper.cpp "$WORK/whisper.cpp"
    cmake -S "$WORK/whisper.cpp" -B "$WORK/whisper.cpp/build" \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=OFF \
        -DGGML_METAL=ON \
        -DGGML_METAL_EMBED_LIBRARY=ON \
        -DWHISPER_BUILD_TESTS=OFF \
        -DWHISPER_BUILD_SERVER=ON
    cmake --build "$WORK/whisper.cpp/build" --config Release --target whisper-server -j "$(sysctl -n hw.ncpu)"
    mkdir -p "$RUNTIME"
    cp "$WORK/whisper.cpp/build/bin/whisper-server" "$RUNTIME/"
    echo "$WHISPER_CPP_TAG" > "$RUNTIME/VERSION"
else
    echo "==> Using cached whisper-server ($(cat "$RUNTIME/VERSION" 2>/dev/null || echo unknown version)); --rebuild-whisper to rebuild"
fi

# 2. .NET app
echo "==> Publishing WhisperLocal.Mac ($RID)"
rm -rf "$WORK/publish"
dotnet publish "$REPO/WhisperLocal.Mac/WhisperLocal.Mac.csproj" -c Release -r "$RID" --self-contained true \
    -p:DebugType=none -o "$WORK/publish"

# 3. .app bundle
echo "==> Assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$REPO/WhisperLocal.Mac/Info.plist" "$APP/Contents/Info.plist"
cp "$REPO/WhisperLocal.Mac/Resources/AppIcon.icns" "$APP/Contents/Resources/"
cp -R "$WORK/publish/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/WhisperLocal" "$APP/Contents/MacOS/whisper/whisper-server"

# Ad-hoc signatures change with every build, and macOS then forgets the Accessibility permission.
# Signing with a stable identity (e.g. a self-signed "Code Signing" certificate from Keychain Access) avoids that.
IDENTITY="${CODESIGN_IDENTITY:--}"
echo "==> Signing (identity: $IDENTITY)"
# codesign treats everything in Contents/MacOS as code, including .NET's .dll/.json files (their signatures go
# into extended attributes), so every file is signed first, the main executable last, then the bundle
find "$APP/Contents/MacOS" -type f ! -path "$APP/Contents/MacOS/WhisperLocal" -print0 \
    | xargs -0 codesign --force --sign "$IDENTITY"
codesign --force --sign "$IDENTITY" "$APP/Contents/MacOS/WhisperLocal"
codesign --force --sign "$IDENTITY" "$APP"
codesign --verify "$APP"

if [[ $INSTALL == 1 ]]; then
    mkdir -p "$HOME/Applications"
    rm -rf "$HOME/Applications/WhisperLocal.app"
    cp -R "$APP" "$HOME/Applications/"
    echo "==> Installed to ~/Applications/WhisperLocal.app"
fi

if [[ $DMG == 1 ]]; then
    VERSION="$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$APP/Contents/Info.plist")"
    DMG_PATH="$REPO/artifacts/WhisperLocal-$VERSION-${RID#osx-}.dmg"
    STAGING="$WORK/dmg"
    echo "==> Creating $DMG_PATH"
    rm -rf "$STAGING" "$DMG_PATH"
    mkdir -p "$STAGING"
    cp -R "$APP" "$STAGING/"
    ln -s /Applications "$STAGING/Applications" # drag-to-install target
    hdiutil create -volname "WhisperLocal" -srcfolder "$STAGING" -ov -format UDZO "$DMG_PATH"
    echo "==> DMG: $DMG_PATH"
    echo "    Not notarized: on another Mac, open it via System Settings → Privacy & Security → Open Anyway."
fi

echo
echo "Done: $APP"
echo "Model: scripts/get-model.sh (downloads into ~/Library/Application Support/WhisperLocal/models)"
