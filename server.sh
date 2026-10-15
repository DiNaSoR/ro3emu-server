#!/usr/bin/env bash
# One-step RO3Emu server on Linux: checks the tools, exports the game data from a copy of the client (first run only),
# builds the server and runs it, in this terminal or as a systemd service. Players join from Windows with join.cmd.
#
# usage: ./server.sh [--client DIR] [--host ADDRESS] [--gm ACCOUNT] [--service] [--reexport]
#   --client DIR    the client folder copied from Windows (at least its ro3_Data/StreamingAssets); first run only
#   --host ADDRESS  the address players connect to (default: this machine's first address)
#   --gm ACCOUNT    an account that gets GM commands in game (repeatable)
#   --service       install and start a systemd service (needs sudo) instead of running in this terminal
#   --reexport      export the game data again (after a client update)
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
root=$(pwd)

client=""; host=""; service=0; reexport=0; gms=()
while [ $# -gt 0 ]; do
  case "$1" in
    --client) client="$2"; shift 2 ;;
    --host) host="$2"; shift 2 ;;
    --gm) gms+=("$2"); shift 2 ;;
    --service) service=1; shift ;;
    --reexport) reexport=1; shift ;;
    -h|--help) sed -n '2,11p' "$0"; exit 0 ;;
    *) echo "unknown option $1 (see ./server.sh --help)"; exit 1 ;;
  esac
done

step() { printf '\n== %s\n' "$1"; }
fail() { printf '\n%s\n' "$1" >&2; exit 1; }
ask() { local answer; read -r -p "$1 [Y/n] " answer; [[ ! "$answer" =~ ^[nN] ]]; }

# 1. Tools. .NET goes to ~/.dotnet with Microsoft's install script; Node.js and a JDK come from the package manager
#    and are needed only for the data export.
step 'Checking the tools'
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$PATH:$DOTNET_ROOT"
if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo '  .NET 10 SDK: missing'
  ask '  Install the .NET 10 SDK into ~/.dotnet now?' || fail 'Install the .NET 10 SDK (https://dotnet.microsoft.com/download/dotnet/10.0), then run this again.'
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_ROOT"
fi
echo '  .NET 10 SDK: ok'

need_package() { # <command> <apt/dnf package> <name>
  command -v "$1" >/dev/null && { echo "  $3: ok"; return; }
  echo "  $3: missing"
  if command -v apt-get >/dev/null; then
    ask "  Install $3 with apt-get (sudo)?" && sudo apt-get install -y "$2" && return
  elif command -v dnf >/dev/null; then
    ask "  Install $3 with dnf (sudo)?" && sudo dnf install -y "$2" && return
  fi
  fail "Install $3, then run this again."
}

# 2. Game data, exported once from the client.
if [ "$reexport" = 1 ] || [ ! -f data/config/MapResource.json ] || [ ! -f data/msgtable.tsv ]; then
  step 'Exporting the game data from the client (first run only, takes a few minutes)'
  if [ -z "$client" ]; then
    echo '  Copy the game folder from Windows to this machine first (at least its ro3_Data/StreamingAssets folder).'
    read -r -p '  Path of the copied game folder: ' client
  fi
  [ -d "$client/ro3_Data/StreamingAssets/Recovery/LuaPayload" ] || fail "No ro3_Data/StreamingAssets/Recovery/LuaPayload in '$client'. Copy the whole game folder and pass it with --client."
  client=$(readlink -f "$client")
  need_package node nodejs 'Node.js'
  need_package javac default-jdk-headless 'Java JDK'
  if [ ! -f unluac.jar ]; then
    echo '  Downloading the unluac decompiler (SourceForge)'
    curl -sSL -A Wget -o unluac.jar 'https://sourceforge.net/projects/unluac/files/Unstable/unluac_2025_12_23.jar/download'
    echo '98be0fa84ac73ca66dce2842a2e4512226f4c611b6500dc96415571fc5538fcc  unluac.jar' | sha256sum -c --quiet \
      || { rm -f unluac.jar; fail 'The unluac download does not match. Get unluac_2025_12_23.jar from https://sourceforge.net/projects/unluac/files/Unstable/ and save it here as unluac.jar.'; }
  fi
  export RO3_CLIENT_DIR="$client"
  dotnet run --project tools/LuaDecode -c Release -- "$client/ro3_Data/StreamingAssets/Recovery/LuaPayload" re/clientB/luac
  javac -cp unluac.jar -d tools/unluac-batch tools/unluac-batch/BatchUnluac.java
  java -cp "unluac.jar:tools/unluac-batch" BatchUnluac re/clientB/luac re/clientB/lua-src
  dotnet run --project tools/ConfigExport -c Release -- re/clientB/luac data/config
  dotnet run --project tools/ConfigExport -c Release -- re/clientB/luac data/config Localization_en.luac
  dotnet run --project tools/ConfigExport -c Release -- re/clientB/luac data/sceneeditor 'Config/SceneEditor'
  node tools/WorldData/navexport.js data/nav
  node tools/msgtable.js re/clientB/lua-src data/msgtable.tsv
  [ -f data/config/MapResource.json ] || fail 'The export did not produce data/config. Check that the folder is a complete Ragnarok 3 PC client.'
fi

# 3. Build.
step 'Building the server'
dotnet publish src/Ro3Emu.Server -c Release -o server-bin -v q -nologo -clp:ErrorsOnly

# 4. Settings: the address players reach, GM accounts, open content and the battle pass.
if [ -z "$host" ]; then
  host=$(hostname -I 2>/dev/null | awk '{print $1}')
  [ -n "$host" ] || host=127.0.0.1
fi
case "$host" in
  127.*|10.*|192.168.*|172.1[6-9].*|172.2[0-9].*|172.3[01].*)
    echo "  Note: $host is a local network address. Players outside your network need your public address: --host <public IP>." ;;
esac
settings=(
  "ASPNETCORE_ENVIRONMENT=Production"
  "Ro3Emu__PublicHost=$host"
  "Ro3Emu__OpenContent__Enabled=true"
  "Ro3Emu__Pass__Play=true"
  "Ro3Emu__Pass__FreePremium=true"
)
for i in "${!gms[@]}"; do settings+=("Ro3Emu__GmAccounts__$i=${gms[$i]}"); done
[ ${#gms[@]} -gt 0 ] || echo '  No --gm account: nobody has GM commands in game.'

ports_note() {
  echo "Open TCP ports 5380, 20000 and 33100 for players (firewall, and port forwarding behind a router)."
  if command -v ufw >/dev/null && sudo -n ufw status 2>/dev/null | grep -q 'Status: active'; then
    echo "  ufw: sudo ufw allow 5380/tcp && sudo ufw allow 20000/tcp && sudo ufw allow 33100/tcp"
  fi
  echo "Players: run join.cmd on Windows and enter $host"
}

# 5. Run: as a service, or in this terminal (Ctrl+C stops it).
if [ "$service" = 1 ]; then
  step 'Installing the systemd service ro3emu'
  unit=/etc/systemd/system/ro3emu.service
  {
    echo '[Unit]'
    echo 'Description=RO3Emu server'
    echo 'After=network.target'
    echo
    echo '[Service]'
    echo "User=$(id -un)"
    echo "WorkingDirectory=$root"
    echo "ExecStart=$root/server-bin/Ro3Emu.Server --Ro3Emu:PreloadGameData=true"
    for s in "${settings[@]}"; do echo "Environment=$s"; done
    echo "Environment=DOTNET_ROOT=$DOTNET_ROOT"
    echo 'Restart=always'
    echo
    echo '[Install]'
    echo 'WantedBy=multi-user.target'
  } | sudo tee "$unit" >/dev/null
  sudo systemctl daemon-reload
  sudo systemctl enable --now ro3emu
  sudo systemctl restart ro3emu
  echo "Running. Logs: journalctl -u ro3emu -f   Stop: sudo systemctl stop ro3emu"
  ports_note
else
  step "Starting the server on $host (Ctrl+C stops it)"
  ports_note
  exec env "${settings[@]}" ./server-bin/Ro3Emu.Server --Ro3Emu:PreloadGameData=true
fi
