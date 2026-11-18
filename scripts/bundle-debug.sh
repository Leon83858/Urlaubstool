#!/bin/bash
# Baut ein Debug-App-Bundle (macOS) und startet es. Protokoll: target/check/bundle.log
cd "$(dirname "$0")/.." || exit 1
export PATH="$HOME/.cargo/bin:/opt/homebrew/bin:/usr/local/bin:$PATH"
mkdir -p target/check
echo "running $(date -u +%FT%TZ)" > target/check/bundle-state.txt
npx tauri build --debug --bundles app > target/check/bundle.log 2>&1
code=$?
if [ "$code" -eq 0 ]; then
  open "target/debug/bundle/macos/Urlaubstool.app" >> target/check/bundle.log 2>&1
  code=$?
fi
echo "done $(date -u +%FT%TZ) exit=$code" > target/check/bundle-state.txt
exit $code
