#!/usr/bin/env bash
# Installs the Log Dug icon and desktop entry for the current user, so it appears in the app launcher,
# the task switcher and the file manager's "Open With" menu. Usage: scripts/install-linux.sh [path/to/logdug]
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BINARY="${1:-$ROOT_DIR/artifacts/publish/logdug/logdug}"
[[ -x "$BINARY" ]] || { echo "No executable at $BINARY. Publish first or pass its path." >&2; exit 1; }

DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
BIN_DIR="$HOME/.local/bin"
mkdir -p "$BIN_DIR" "$DATA/applications"
ln -sf "$(cd "$(dirname "$BINARY")" && pwd)/$(basename "$BINARY")" "$BIN_DIR/logdug"

for size in 16 24 32 48 64 128 256 512; do
  install -Dm644 "$ROOT_DIR/assets/icon/logdug-$size.png" "$DATA/icons/hicolor/${size}x${size}/apps/logdug.png"
done
install -Dm644 "$ROOT_DIR/assets/icon/logdug.svg" "$DATA/icons/hicolor/scalable/apps/logdug.svg"
sed "s|^Exec=logdug|Exec=$BIN_DIR/logdug|" "$ROOT_DIR/packaging/linux/logdug.desktop" > "$DATA/applications/logdug.desktop"

command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -qtf "$DATA/icons/hicolor" || true
command -v update-desktop-database >/dev/null && update-desktop-database "$DATA/applications" || true
echo "Installed. Right-click a file or folder > Open With > Log Dug."
