#!/bin/bash
# Baut den Windows-Installer (NSIS, x64) per Cross-Compile auf dem Mac.
# Werkzeuge (einmalig, werden bei Bedarf installiert): Homebrew nsis, llvm, lld;
# Rust-Ziel x86_64-pc-windows-msvc; cargo-xwin (lädt Microsoft CRT/Windows SDK
# in ~/.cache/cargo-xwin).
# Ergebnis ist NICHT auf Windows geprüft (REQ-PLAT-002 bleibt offen, bis Build und
# Bedienung dort gelaufen sind). Unsigniert (REQ-REL-023 offen).
# Protokoll: target/check/windows.log, Status: target/check/windows-state.txt
cd "$(dirname "$0")/.." || exit 1
export PATH="$HOME/.cargo/bin:/opt/homebrew/bin:/usr/local/bin:$PATH"
mkdir -p target/check
state=target/check/windows-state.txt
log=target/check/windows.log
echo "running $(date -u +%FT%TZ)" > "$state"
: > "$log"
step() { echo "== $* ($(date -u +%T))" >> "$log"; }

code=0
for formula in nsis llvm lld; do
  if ! brew list --versions "$formula" >/dev/null 2>&1; then
    step "brew install $formula"
    brew install "$formula" >> "$log" 2>&1 || code=$?
  fi
done
llvm_prefix=$(brew --prefix llvm 2>/dev/null)
lld_prefix=$(brew --prefix lld 2>/dev/null)
export PATH="$llvm_prefix/bin:$lld_prefix/bin:$PATH"

if [ "$code" -eq 0 ] && ! rustup target list --installed | grep -qx x86_64-pc-windows-msvc; then
  step "rustup target add x86_64-pc-windows-msvc"
  rustup target add x86_64-pc-windows-msvc >> "$log" 2>&1 || code=$?
fi
if [ "$code" -eq 0 ] && ! command -v cargo-xwin >/dev/null 2>&1; then
  step "cargo install cargo-xwin"
  cargo install --locked cargo-xwin >> "$log" 2>&1 || code=$?
fi

if [ "$code" -eq 0 ]; then
  step "tauri build (nsis, x86_64-pc-windows-msvc)"
  {
    echo "Commit: $(git rev-parse --short HEAD)"
    echo "makensis: $(makensis -VERSION 2>&1)"
    echo "clang-cl: $(clang-cl --version 2>&1 | head -1)"
    echo "cargo-xwin: $(cargo xwin --version 2>&1 | head -1)"
  } >> "$log"
  npx tauri build --runner cargo-xwin --target x86_64-pc-windows-msvc --bundles nsis >> "$log" 2>&1
  code=$?
fi

nsis_dir=target/x86_64-pc-windows-msvc/release/bundle/nsis
if [ "$code" -eq 0 ]; then
  (cd "$nsis_dir" && shasum -a 256 ./*.exe) > target/check/windows-SHA256SUMS.txt
  code=$?
fi
echo "done $(date -u +%FT%TZ) exit=$code" > "$state"
exit $code
