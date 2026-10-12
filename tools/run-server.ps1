# Runs the Ro3Emu server and restarts it when it exits (crash or kill), with output in data/logs/server-<date>.log.
# usage: tools/run-server.ps1 [-Configuration Release]
param([string]$Configuration = 'Release')
$root = Split-Path $PSScriptRoot -Parent
$logs = Join-Path $root 'data\logs'
New-Item -ItemType Directory -Force $logs | Out-Null
dotnet build (Join-Path $root 'src\Ro3Emu.Server') -c $Configuration | Out-Null
$exe = Join-Path $root "src\Ro3Emu.Server\bin\$Configuration\net10.0\Ro3Emu.Server.exe"
while ($true) {
  $log = Join-Path $logs ("server-{0:yyyyMMdd}.log" -f (Get-Date))
  "=== start $(Get-Date -Format s)" | Add-Content $log
  # Local play keeps the traffic log on (the RE aid); production and load tests leave it off.
  & $exe --Ro3Emu:PreloadGameData=true --Ro3Emu:TrafficLog=on *>> $log
  "=== exit $LASTEXITCODE $(Get-Date -Format s), restarting in 5 s" | Add-Content $log
  Start-Sleep 5
}
