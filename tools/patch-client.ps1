# Rebuilds ClientHooks + Loc, then the patcher that embeds both (a non-incremental build, because MSBuild does not see
# a changed embedded DLL), stops the game client and patches the client in -Client (default: $env:RO3_CLIENT_DIR). The patcher's output is bin\Debug\net10.0\win-x64.
param([string]$Client = $env:RO3_CLIENT_DIR, [string]$Server = 'http://127.0.0.1:5380')
$root = Split-Path $PSScriptRoot -Parent
foreach ($p in 'Ro3Emu.ClientHooks', 'Ro3Emu.Loc') {
  dotnet build (Join-Path $root "src\$p") -v q | Out-Null
  if ($LASTEXITCODE) { throw "build $p failed" }
}
dotnet build (Join-Path $root 'src\Ro3Emu.ClientTools') --no-incremental -v q | Out-Null
if ($LASTEXITCODE) { throw 'build ClientTools failed' }
Get-Process ro3 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
& (Join-Path $root 'src\Ro3Emu.ClientTools\bin\Debug\net10.0\win-x64\Ro3Emu.ClientTools.exe') patch $Client $Server (Join-Path $root 'data\gate_rsa_public.pem')
