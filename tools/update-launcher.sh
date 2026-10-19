#!/usr/bin/env bash
# Installs this fork's RO3Emu.exe (and the client DLLs built with it) where you launch the game from.
#
#   ./update-launcher.sh            download CI's latest main build (the "launcher" release on GitHub)
#   ./update-launcher.sh <zip>      use a RO3Emu-win-x64.zip you already have (e.g. a pull request's CI artifact)
#
# The current RO3Emu.exe is saved to RO3Emu/backups/<time>/ first; the client DLLs go in through update-client-dlls.sh,
# which keeps its own backups. The next `wine RO3Emu.exe join ...` patches the game with the new exe.
set -euo pipefail

LAUNCHER="${RO3EMU_LAUNCHER:-/run/media/SSD/RO3Emu}"
RELEASE="${RO3EMU_LAUNCHER_RELEASE:-https://github.com/akreao/ro3emu-server/releases/download/launcher}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
if [[ -n "${1:-}" ]]; then
    zip="$1"
else
    zip="$work/RO3Emu-win-x64.zip"
    curl -fsSL --retry 3 -o "$zip" "$RELEASE/RO3Emu-win-x64.zip"
    curl -fsSL --retry 3 -o "$work/commit.txt" "$RELEASE/commit.txt" && echo "CI build of main at $(cut -c1-12 "$work/commit.txt")"
fi
python3 -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$zip" "$work/x"
new="$work/x/RO3Emu"
[[ -f "$new/RO3Emu.exe" ]] || { echo "no RO3Emu/RO3Emu.exe in $zip" >&2; exit 1; }

mkdir -p "$LAUNCHER"
backup="$LAUNCHER/backups/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$backup"
[[ -f "$LAUNCHER/RO3Emu.exe" ]] && cp -p "$LAUNCHER/RO3Emu.exe" "$backup/"
[[ -d "$LAUNCHER/server" ]] && cp -rp "$LAUNCHER/server" "$backup/"
cp "$new/RO3Emu.exe" "$LAUNCHER/RO3Emu.exe"
echo "updated RO3Emu.exe  $(sha256sum "$new/RO3Emu.exe" | cut -c1-12)"
# The bundled server (used by `RO3Emu.exe` / `host`; `join` does not need it). appsettings you changed are kept.
mkdir -p "$LAUNCHER/server"
for f in "$new/server/"*; do
    name="$(basename "$f")"
    [[ "$name" == appsettings*.json && -f "$LAUNCHER/server/$name" ]] && continue
    cp "$f" "$LAUNCHER/server/$name"
done
cp "$new/README.txt" "$LAUNCHER/README.txt"
echo "backup of the previous RO3Emu.exe and server: $backup"

RO3EMU_LAUNCHER="$LAUNCHER" bash "$here/update-client-dlls.sh" "$new/client"
