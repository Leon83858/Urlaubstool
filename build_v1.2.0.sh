#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT_PATH="$SCRIPT_DIR/../v1.2.0"
mkdir -p "$OUTPUT_PATH"

echo "🔨 Kompiliere Urlaubstool v1.2.0..."

# Windows Build
echo "📦 Windows Portable (x64) wird kompiliert..."
WINDOWS_OUTPUT="$OUTPUT_PATH/Windows"
mkdir -p "$WINDOWS_OUTPUT"
dotnet publish Urlaubstool.App/Urlaubstool.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$WINDOWS_OUTPUT/temp"

# Copy Icon zu Windows output
cp appicon.ico "$WINDOWS_OUTPUT/" 2>/dev/null || echo "Icon konnte nicht kopiert werden"

# Copy basis folder if exists
if [ -d "basis" ]; then
    cp -r basis "$WINDOWS_OUTPUT/"
fi

# Create final Windows executable location
mv "$WINDOWS_OUTPUT/temp/Urlaubstool.App.exe" "$WINDOWS_OUTPUT/Urlaubstool.exe" 2>/dev/null || true
rm -rf "$WINDOWS_OUTPUT/temp"

echo "✅ Windows Portable erstellt: $WINDOWS_OUTPUT/Urlaubstool.exe"

# macOS Build
echo "📦 macOS App Bundle wird kompiliert..."
MAC_OUTPUT="$OUTPUT_PATH/macOS"
APP_DIR="$MAC_OUTPUT/Urlaubstool.app"
TEMP_MAC="$MAC_OUTPUT/temp"

mkdir -p "$MAC_OUTPUT"

# Publish for macOS (Apple Silicon)
dotnet publish Urlaubstool.App/Urlaubstool.App.csproj -c Release -r osx-arm64 --self-contained -o "$TEMP_MAC"

# Initialize App Bundle
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS"
mkdir -p "$APP_DIR/Contents/Resources"

# Copy Binaries
cp -a "$TEMP_MAC/"* "$APP_DIR/Contents/MacOS/"

# Copy Icon
if [ -f "appicon.icns" ]; then
    cp appicon.icns "$APP_DIR/Contents/Resources/"
    echo "✅ Icon eingebettet"
else
    echo "⚠️  Icon appicon.icns nicht gefunden"
fi

# Create Info.plist
printf '<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Urlaubstool</string>
    <key>CFBundleDisplayName</key>
    <string>Urlaubstool</string>
    <key>CFBundleIdentifier</key>
    <string>com.urlaubstool.app</string>
    <key>CFBundleVersion</key>
    <string>1.2.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.2.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>CFBundleExecutable</key>
    <string>Urlaubstool.App</string>
    <key>CFBundleIconFile</key>
    <string>appicon.icns</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
</dict>
</plist>\n' > "$APP_DIR/Contents/Info.plist"

# Copy basis folder if exists
if [ -d "basis" ]; then
    cp -r basis "$APP_DIR/Contents/MacOS/"
fi

# Make executable
chmod +x "$APP_DIR/Contents/MacOS/Urlaubstool.App"

# Cleanup
rm -rf "$TEMP_MAC"

echo "✅ macOS App Bundle erstellt: $APP_DIR"

# Summary
echo ""
echo "=========================================="
echo "✅ Kompilierung erfolgreich abgeschlossen!"
echo "=========================================="
echo ""
echo "📍 Output-Verzeichnis: $OUTPUT_PATH"
echo ""
echo "Windows:"
echo "  📁 $WINDOWS_OUTPUT/"
echo "  💾 Urlaubstool.exe (Portable)"
echo ""
echo "macOS:"
echo "  📁 $APP_DIR"
echo "  💾 Urlaubstool.app"
echo ""

ls -lh "$WINDOWS_OUTPUT/Urlaubstool.exe" 2>/dev/null && echo "  Größe: $(du -h "$WINDOWS_OUTPUT/Urlaubstool.exe" | cut -f1)" || true
echo ""
