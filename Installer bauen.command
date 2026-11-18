#!/bin/bash
# Doppelklick im Finder: baut die Installer für macOS und Windows nach Installer/.
cd "$(dirname "$0")" || exit 1
export PATH="$HOME/.cargo/bin:/opt/homebrew/bin:/usr/local/bin:$PATH"
npm run installer
echo
echo "Fertig. Status: $(cat target/check/installer-state.txt 2>/dev/null)"
echo "Dieses Fenster kann geschlossen werden."
