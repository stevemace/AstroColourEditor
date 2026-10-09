#!/bin/sh
# Builds "Astro Colour Editor.app" and a zip of it in publish/macos-<arch>. Run on a Mac with the .NET 10 SDK.
#
#   tools/package-macos.sh [osx-arm64|osx-x64]       (default: osx-arm64, Apple Silicon)
#
# Without signing settings the app is ad-hoc signed, so it runs on the Mac that built it; on other
# Macs Gatekeeper blocks it until it is opened with right-click > Open (or the quarantine flag is cleared).
# To distribute it, sign and notarize with an Apple Developer ID:
#
#   SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" \
#   NOTARY_PROFILE=<profile saved with: xcrun notarytool store-credentials> \
#   tools/package-macos.sh
set -eu

RID=${1:-osx-arm64}
VERSION=${VERSION:-1.0.0}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
PROJECT="$ROOT/src/AstroColour.Desktop"
OUT="$ROOT/publish/macos-${RID#osx-}"
APP="$OUT/Astro Colour Editor.app"

rm -rf "$OUT"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained -p:DebugType=none -o "$OUT/bin"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/bin/." "$APP/Contents/MacOS/"
rm -rf "$OUT/bin"
sed "s/\$VERSION/$VERSION/g" "$PROJECT/macOS/Info.plist" > "$APP/Contents/Info.plist"
cp "$ROOT/src/AstroColour.App/Assets/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"

if [ -n "${SIGN_IDENTITY:-}" ]; then
    # Sign the native libraries first, then the bundle (whose main executable gets the entitlements).
    find "$APP/Contents/MacOS" -type f \( -name "*.dylib" -o -perm -u+x \) ! -name AstroColourEditor -exec \
        codesign --force --timestamp --options runtime --sign "$SIGN_IDENTITY" {} \;
    codesign --force --timestamp --options runtime --entitlements "$PROJECT/macOS/AstroColourEditor.entitlements" \
        --sign "$SIGN_IDENTITY" "$APP"
else
    codesign --force --deep --sign - "$APP"
fi

ZIP="$OUT/AstroColourEditor-$VERSION-${RID#osx-}.zip"
ditto -c -k --keepParent "$APP" "$ZIP"

if [ -n "${SIGN_IDENTITY:-}" ] && [ -n "${NOTARY_PROFILE:-}" ]; then
    xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$APP"
    rm "$ZIP"
    ditto -c -k --keepParent "$APP" "$ZIP"
fi

echo "Built $APP"
echo "      $ZIP"
