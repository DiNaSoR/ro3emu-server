#!/usr/bin/env bash
# Puts the newest client DLLs (Ro3Emu.ClientHooks.dll, Ro3Emu.Loc.dll) where RO3Emu.exe and the game load them.
#
#   ./update-client-dlls.sh            download CI's latest main build (the client-dlls release on GitHub)
#   ./update-client-dlls.sh <folder>   copy from a folder instead (e.g. a test build a thread gave you, or the
#                                      unzipped client-dlls artifact of a pull request's CI run)
#
# The current DLLs are saved to RO3Emu/client/backups/<time>/ first, so a bad build is easy to roll back.
# The DLLs are no longer in git: .github/workflows/client-dlls.yml builds them on every change to main.
set -euo pipefail

LAUNCHER="${RO3EMU_LAUNCHER:-/run/media/SSD/RO3Emu}"
GAME="${RO3_CLIENT_DIR:-/run/media/SSD/ro3/ROO_PC}"
RELEASE="${RO3EMU_DLL_RELEASE:-https://github.com/akreao/ro3emu-server/releases/download/client-dlls}"
DLLS=(Ro3Emu.ClientHooks.dll Ro3Emu.Loc.dll)

if [[ -n "${1:-}" && "$1" != "--pull" ]]; then
    src="$1"
else
    src="$(mktemp -d)"
    trap 'rm -rf "$src"' EXIT
    for f in "${DLLS[@]}" commit.txt; do
        curl -fsSL --retry 3 -o "$src/$f" "$RELEASE/$f"
    done
    echo "CI build of main at $(cut -c1-12 "$src/commit.txt")"
fi

[[ -f "$src/Ro3Emu.ClientHooks.dll" ]] || { echo "no Ro3Emu.ClientHooks.dll in $src" >&2; exit 1; }

backup="$LAUNCHER/client/backups/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$backup"
for dll in "${DLLS[@]}"; do
    [[ -f "$LAUNCHER/client/$dll" ]] && cp -p "$LAUNCHER/client/$dll" "$backup/"
    if [[ -f "$src/$dll" ]]; then
        # RO3Emu.exe copies client/ into the game when it patches; the game copy is updated too so it applies right away.
        cp "$src/$dll" "$LAUNCHER/client/$dll"
        cp "$src/$dll" "$GAME/ro3_Data/Managed/$dll"
        echo "updated $dll  $(sha256sum "$src/$dll" | cut -c1-12)"
    fi
done

# A copy in the game's root folder is loaded before ro3_Data/Managed and would hide the new build.
for dll in "${DLLS[@]}"; do
    [[ -f "$GAME/$dll" ]] && mv "$GAME/$dll" "$backup/root-$dll" && echo "moved stale $GAME/$dll to $backup"
done
echo "backup of the previous DLLs: $backup"
