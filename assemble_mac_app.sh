#!/bin/bash
APP_NAME="Urlaubstool"
DEST_DIR="$HOME/Desktop/$APP_NAME.app"
TEMP_DIR="$HOME/Desktop/Urlaubstool_Mac_Temp"
SOURCE_PLIST="Urlaubstool_Release_Final/Mac_App/Urlaubstool.app/Contents/Info.plist"
ICON_FILE="Urlaubstool.App/appicon.icns"

# Clean up previous build
rm -rf "$DEST_DIR"

# Create directories
mkdir -p "$DEST_DIR/Contents/MacOS"
mkdir -p "$DEST_DIR/Contents/Resources"

# Copy binaries
cp -a "$TEMP_DIR/"* "$DEST_DIR/Contents/MacOS/"

# Copy resources
cp "$ICON_FILE" "$DEST_DIR/Contents/Resources/"
cp "$SOURCE_PLIST" "$DEST_DIR/Contents/Info.plist"

# Copy external assets to MacOS folder (working dir)
# Assuming basis folder contains all necessary templates/csvs
if [ -d "basis" ]; then
    cp -r basis "$DEST_DIR/Contents/MacOS/"
    # Also copy to Windows folder
    if [ -d "$HOME/Desktop/Urlaubstool_Windows" ]; then
        cp -r basis "$HOME/Desktop/Urlaubstool_Windows/"
    fi
else
    echo "Warning: 'basis' folder not found in current directory."
fi

# Set executable permission
chmod +x "$DEST_DIR/Contents/MacOS/$APP_NAME.App"

# Cleanup
rm -rf "$TEMP_DIR"

echo "Mac App assembled at $DEST_DIR"
