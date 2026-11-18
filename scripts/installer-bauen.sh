#!/bin/bash
# Baut beide Installer und legt sie mit Prüfsummen in Installer/ ab (git-ignoriert):
#  - macOS: DMG (scripts/bundle-release.sh)
#  - Windows x64: NSIS-Setup per Cross-Compile (scripts/bundle-windows.sh), auf Windows ungeprüft
# Status: target/check/installer-state.txt
cd "$(dirname "$0")/.." || exit 1
mkdir -p target/check Installer
state=target/check/installer-state.txt
echo "running $(date -u +%FT%TZ)" > "$state"

bash scripts/bundle-release.sh; mac=$?
bash scripts/bundle-windows.sh; win=$?

version=$(node -p "require('./src-tauri/tauri.conf.json').version" 2>/dev/null)
if [ "$mac" -eq 0 ]; then
  cp -f "target/release/bundle/dmg/Urlaubstool_${version}_$(uname -m).dmg" Installer/ || mac=1
fi
if [ "$win" -eq 0 ]; then
  cp -f "target/x86_64-pc-windows-msvc/release/bundle/nsis/Urlaubstool_${version}_x64-setup.exe" Installer/ || win=1
fi
(cd Installer && shasum -a 256 "./Urlaubstool_${version}_$(uname -m).dmg" "./Urlaubstool_${version}_x64-setup.exe" 2>/dev/null) > Installer/SHA256SUMS.txt
echo "done $(date -u +%FT%TZ) macos=$mac windows=$win" > "$state"
[ "$mac" -eq 0 ] && [ "$win" -eq 0 ]
