#!/bin/bash
# Baut das Release-Paket (macOS: App-Bundle und DMG) mit Prüfsummen.
# Hinweis: Erstellt unsignierte Artefakte (REQ-REL-023 offen).
# Protokoll: target/check/release.log, Status: target/check/release-state.txt
cd "$(dirname "$0")/.." || exit 1
export PATH="$HOME/.cargo/bin:/opt/homebrew/bin:/usr/local/bin:$PATH"
mkdir -p target/check
state=target/check/release-state.txt
echo "running $(date -u +%FT%TZ)" > "$state"
{
  echo "Commit: $(git rev-parse --short HEAD) $(git status --porcelain | grep -q . && echo '(mit lokalen Änderungen)')"
  echo "rustc: $(rustc --version)"
  echo "cargo: $(cargo --version)"
  echo "node: $(node --version)"
  echo "System: $(sw_vers -productName 2>/dev/null) $(sw_vers -productVersion 2>/dev/null) $(uname -m)"
} > target/check/release-env.txt
npx tauri build --bundles app > target/check/release.log 2>&1
code=$?
# DMG direkt mit hdiutil: Das Tauri-DMG-Skript steuert den Finder per AppleScript
# und scheitert ohne Automations-Freigabe des aufrufenden Terminals.
version=$(node -p "require('./src-tauri/tauri.conf.json').version")
dmg_dir=target/release/bundle/dmg
dmg="$dmg_dir/Urlaubstool_${version}_$(uname -m).dmg"
# Vollständige Ad-hoc-Signatur (versiegelt auch die Ressourcen). Ohne sie meldet
# macOS eine heruntergeladene App unter Umständen als „beschädigt“. Ersetzt keine
# Signatur mit Entwicklerzertifikat (REQ-REL-023).
app=target/release/bundle/macos/Urlaubstool.app
if [ "$code" -eq 0 ]; then
  codesign --force --deep --sign - "$app" >> target/check/release.log 2>&1 \
    && codesign --verify --deep --strict "$app" >> target/check/release.log 2>&1
  code=$?
  echo "codesign verify exit=$code" >> target/check/release.log
fi
if [ "$code" -eq 0 ]; then
  staging=$(mktemp -d)
  cp -R target/release/bundle/macos/Urlaubstool.app "$staging/" && ln -s /Applications "$staging/Programme"
  mkdir -p "$dmg_dir"
  if [ -e "$dmg" ]; then mv "$dmg" "$dmg.alt-$(date +%s)"; fi
  hdiutil create -volname "Urlaubstool $version" -srcfolder "$staging" -fs HFS+ -format UDZO "$dmg" >> target/check/release.log 2>&1
  code=$?
  rm -rf "$staging"
fi
if [ "$code" -eq 0 ]; then
  (cd target/release/bundle && { find macos -maxdepth 1 -name '*.app'; find dmg -maxdepth 1 -name '*.dmg'; } | sort | while read -r item; do
    if [ -d "$item" ]; then
      echo "$(find "$item" -type f -print0 | sort -z | xargs -0 shasum -a 256 | shasum -a 256 | cut -d' ' -f1)  $item (Inhalt)"
    else
      shasum -a 256 "$item"
    fi
  done) > target/check/release-SHA256SUMS.txt
  code=$?
fi
echo "done $(date -u +%FT%TZ) exit=$code" > "$state"
exit $code
