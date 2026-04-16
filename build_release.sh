#!/bin/bash
set -e

echo "Building for Windows..."
# Windows (x64)
mkdir -p "$HOME/Desktop/Urlaubstool_Windows"
dotnet publish Urlaubstool.App/Urlaubstool.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$HOME/Desktop/Urlaubstool_Windows"

# Copy external assets (basis folder)
if [ -d "basis" ]; then
    cp -r basis "$HOME/Desktop/Urlaubstool_Windows/"
fi

echo "Windows build complete."

echo "Building for MacOS..."
TEMP_MAC="$HOME/Desktop/Urlaubstool_Mac_Temp"
APP_DIR="$HOME/Desktop/Urlaubstool.app"

# MacOS (Apple Silicon) - self contained
dotnet publish Urlaubstool.App/Urlaubstool.App.csproj -c Release -r osx-arm64 --self-contained -o "$TEMP_MAC"

# Initialize App Bundle
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS"
mkdir -p "$APP_DIR/Contents/Resources"

# Copy Binaries
cp -a "$TEMP_MAC/"* "$APP_DIR/Contents/MacOS/"

# Copy Icon
if [ -f "Urlaubstool.App/appicon.icns" ]; then
    cp Urlaubstool.App/appicon.icns "$APP_DIR/Contents/Resources/"
elif [ -f "appicon.icns" ]; then
    cp appicon.icns "$APP_DIR/Contents/Resources/"
else 
    echo "Warning: Icon appicon.icns not found!"
fi

# Create Info.plist using printf
printf '<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Urlaubstool</string>
    <key>CFBundleDisplayName</key>
    <string>Urlaubstool</string>
    <key>CFBundleIdentifier</key>
    <string>com.leonpilger.urlaubstool</string>
    <key>CFBundleVersion</key>
    <string>1.0.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
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
</dict>
</plist>\n' > "$APP_DIR/Contents/Info.plist"

# Copy Local Assets (basis)
if [ -d "basis" ]; then
    cp -r basis "$APP_DIR/Contents/MacOS/"
fi

# Cleanup
rm -rf "$TEMP_MAC"

echo "MacOS build complete: $APP_DIR"
