# One-step play. start.cmd: checks the tools, exports the game data from your client (first run only), builds and starts
# a local server in its own window, then patches and starts the client against it. join.cmd: patches and starts the
# client against someone else's server (no data export, no local server).
# usage: start.cmd | join.cmd  (or: powershell -ExecutionPolicy Bypass -File start.ps1 [-ClientDir <folder with ro3.exe>]
#        [-Reexport] [-Join [-Server <address>]])
param(
    [string]$ClientDir,
    [switch]$Reexport,
    [switch]$Join,
    [string]$Server
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root
$data = Join-Path $root 'data'
New-Item -ItemType Directory -Force $data | Out-Null

function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host ''; Write-Host $text -ForegroundColor Red; Read-Host 'Press Enter to close'; exit 1 }
function Run($exe, [string[]]$arguments) {
    # Native tools write progress to stderr; only the exit code decides.
    $ErrorActionPreference = 'Continue'
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { Fail "Failed: $exe $($arguments -join ' ')" }
}
function RefreshPath {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

# 1. Tools. A missing one is installed with winget when the user agrees (Node.js and Java only for the data export).
$tools = @{
    dotnet = @{ Name = '.NET 10 SDK'; Ok = { try { (& dotnet --list-sdks 2>$null) -match '^10\.' } catch { $false } }; Winget = 'Microsoft.DotNet.SDK.10'; Url = 'https://dotnet.microsoft.com/download/dotnet/10.0' }
    node = @{ Name = 'Node.js'; Ok = { Get-Command node -ErrorAction SilentlyContinue }; Winget = 'OpenJS.NodeJS.LTS'; Url = 'https://nodejs.org/' }
    java = @{ Name = 'Java JDK'; Ok = { Get-Command javac -ErrorAction SilentlyContinue }; Winget = 'EclipseAdoptium.Temurin.21.JDK'; Url = 'https://adoptium.net/' }
}
function Need($key) {
    $tool = $tools[$key]
    if (& $tool.Ok) { Write-Host "  $($tool.Name): ok"; return }
    Write-Host "  $($tool.Name): missing" -ForegroundColor Yellow
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Fail "Install $($tool.Name) from $($tool.Url), then run this again." }
    $answer = Read-Host "  Install $($tool.Name) now with winget? [Y/n]"
    if ($answer -match '^[nN]') { Fail "Install $($tool.Name) from $($tool.Url), then run this again." }
    winget install --id $tool.Winget -e --accept-package-agreements --accept-source-agreements
    RefreshPath
    if (-not (& $tool.Ok)) { Fail "$($tool.Name) is still not found. Close this window, open a new one and run this again." }
}

Step 'Checking the tools'
Need dotnet

# 2. The client folder: argument, environment, last run, or ask.
Step 'Finding the game client'
$saved = Join-Path $data 'client-dir.txt'
if (-not $ClientDir) { $ClientDir = $env:RO3_CLIENT_DIR }
if (-not $ClientDir -and (Test-Path $saved)) { $ClientDir = (Get-Content $saved -Raw -Encoding UTF8).Trim() }
if (-not $ClientDir -or -not (Test-Path (Join-Path $ClientDir 'ro3.exe'))) {
    Write-Host '  Pick the game folder: the one that contains ro3.exe.'
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = 'Ragnarok 3 client folder (the one with ro3.exe)'
        if ($dialog.ShowDialog() -eq 'OK') { $ClientDir = $dialog.SelectedPath }
    } catch {
        $ClientDir = Read-Host '  Game folder'
    }
}
if (-not $ClientDir -or -not (Test-Path (Join-Path $ClientDir 'ro3.exe'))) { Fail "No ro3.exe in '$ClientDir'. Run this again and pick the folder that contains ro3.exe." }
$ClientDir = (Resolve-Path $ClientDir).Path
Set-Content $saved $ClientDir -Encoding UTF8
$env:RO3_CLIENT_DIR = $ClientDir
Write-Host "  $ClientDir"

# A server to join: "host", "host:port" or a full URL; the HTTP port defaults to 5380.
if ($Join -or $Server) {
    if (-not $Server) { $Server = Read-Host 'Server address (ask the server owner, for example 203.0.113.10)' }
    $Server = $Server.Trim().TrimEnd('/')
    if (-not $Server) { Fail 'No server address given.' }
    if ($Server -notmatch '^https?://') { $Server = 'http://' + $Server }
    if ($Server -notmatch '^https?://[^/]+:\d+$' -and $Server -notmatch '^https?://\[') { $Server = $Server + ':5380' }
}

# 3. Game data, exported once from the client (about 5-10 minutes). Joining needs none.
$exported = (Test-Path (Join-Path $data 'config\MapResource.json')) -and (Test-Path (Join-Path $data 'msgtable.tsv'))
if (-not $Server -and ($Reexport -or -not $exported)) {
    Step 'Exporting the game data from your client (first run only, takes a few minutes)'
    Need node
    Need java
    $jar = Join-Path $root 'unluac.jar'
    if (-not (Test-Path $jar)) {
        Write-Host '  Downloading the unluac decompiler (SourceForge)'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest 'https://sourceforge.net/projects/unluac/files/Unstable/unluac_2025_12_23.jar/download' -OutFile $jar -UserAgent 'Wget' -UseBasicParsing
        $hash = (Get-FileHash $jar -Algorithm SHA256).Hash
        if ($hash -ne '98BE0FA84AC73CA66DCE2842A2E4512226F4C611B6500DC96415571FC5538FCC') {
            Remove-Item $jar
            Fail 'The unluac download does not match the expected file. Download unluac_2025_12_23.jar from https://sourceforge.net/projects/unluac/files/Unstable/ and save it as unluac.jar in this folder.'
        }
    }
    Run dotnet @('run', '--project', 'tools/LuaDecode', '-c', 'Release', '--', (Join-Path $ClientDir 'ro3_Data/StreamingAssets/Recovery/LuaPayload'), 're/clientB/luac')
    Run javac @('-cp', 'unluac.jar', '-d', 'tools/unluac-batch', 'tools/unluac-batch/BatchUnluac.java')
    Run java @('-cp', 'unluac.jar;tools/unluac-batch', 'BatchUnluac', 're/clientB/luac', 're/clientB/lua-src')
    Run dotnet @('run', '--project', 'tools/ConfigExport', '-c', 'Release', '--', 're/clientB/luac', 'data/config')
    Run dotnet @('run', '--project', 'tools/ConfigExport', '-c', 'Release', '--', 're/clientB/luac', 'data/config', 'Localization_en.luac')
    Run dotnet @('run', '--project', 'tools/ConfigExport', '-c', 'Release', '--', 're/clientB/luac', 'data/sceneeditor', 'Config\SceneEditor')
    Run node @('tools/WorldData/navexport.js', 'data/nav')
    Run node @('tools/msgtable.js', 're/clientB/lua-src', 'data/msgtable.tsv')
    if (-not (Test-Path (Join-Path $data 'config\MapResource.json'))) { Fail 'The export did not produce data/config. Check that the folder is a complete Ragnarok 3 PC client.' }
}

# 4. Build.
Step 'Building'
if (-not $Server) { Run dotnet @('build', 'src/Ro3Emu.Server', '-c', 'Release', '-v', 'q', '-nologo', '-clp:ErrorsOnly') }
Run dotnet @('build', 'src/Ro3Emu.ClientHooks', '-c', 'Release', '-v', 'q', '-nologo', '-clp:ErrorsOnly')
Run dotnet @('build', 'src/Ro3Emu.ClientTools', '-c', 'Release', '--no-incremental', '-v', 'q', '-nologo', '-clp:ErrorsOnly')

# 5. Local server, in its own window (close that window to stop it).
function ServerUp {
    try { Invoke-WebRequest 'http://127.0.0.1:5380/ro3emu/online' -UseBasicParsing -TimeoutSec 2 | Out-Null; return $true } catch { return $false }
}
if ($Server) {
    Step "Joining $Server"
} elseif (ServerUp) {
    Step 'The server is already running'
} else {
    Step 'Starting the server (its own window; close it to stop the server)'
    $exe = Get-ChildItem (Join-Path $root 'src\Ro3Emu.Server\bin\Release') -Recurse -Filter 'Ro3Emu.Server.exe' | Select-Object -First 1
    if (-not $exe) { Fail 'The server build is missing (src\Ro3Emu.Server\bin\Release).' }
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    # cmd /k keeps the window open with the error if the server stops.
    Start-Process -FilePath 'cmd.exe' -ArgumentList "/k title RO3Emu server & `"$($exe.FullName)`" --Ro3Emu:PreloadGameData=true" -WorkingDirectory $exe.DirectoryName
    $deadline = (Get-Date).AddMinutes(3)
    while (-not (ServerUp)) {
        if ((Get-Date) -gt $deadline) { Fail 'The server did not start within 3 minutes. Look at its window for the error.' }
        Start-Sleep 2
    }
}

# 6. Client: patch against the server and start it.
Step 'Patching and starting the game'
$client = Get-ChildItem (Join-Path $root 'src\Ro3Emu.ClientTools\bin\Release') -Recurse -Filter 'Ro3Emu.ClientTools.exe' | Select-Object -First 1
if (-not $client) { Fail 'The client tools build is missing (src\Ro3Emu.ClientTools\bin\Release).' }
$target = if ($Server) { $Server } else { 'http://127.0.0.1:5380' }
Run $client.FullName @('play', $ClientDir, $target)

Write-Host ''
Write-Host 'The game is starting. Log in with any account name and password: a new account is created on first login.' -ForegroundColor Green
if ($Server) { Write-Host 'To play again later, run join.cmd again.' }
else { Write-Host 'To play again later, run start.cmd again. Close the server window to stop the server.' }
Start-Sleep 8
