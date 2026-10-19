# Builds the two DLLs that go into the game (Ro3Emu.ClientHooks.dll, Ro3Emu.Loc.dll) and writes them, with the
# source.sha256 stamp of the hooks/loc sources they were built from, to -Out.
#
# They compile against the client's own managed DLLs. -ClientDir is a game folder (only ro3_Data/Managed is needed); its
# ro3emu-backup originals replace anything RO3Emu patched. Without -ClientDir the script downloads just ro3_Data/Managed
# (about 33 MB of files, from one 2 GB package) from the publisher with RO3Emu.exe install --only, every file checked
# against the publisher's signature. CI does that: .github/workflows/client-dlls.yml.
#
# usage: pwsh tools/release/client-dlls.ps1 [-ClientDir <game folder>] [-Out <folder>]
#        pwsh tools/release/client-dlls.ps1 -StampOnly      (prints the current sources' stamp)
param(
    [string]$ClientDir,
    [string]$Out,
    [switch]$StampOnly
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Out) { $Out = Join-Path $root 'dist/client-dlls' }

function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "failed ($LASTEXITCODE): $exe $($arguments -join ' ')" }
}

# The hooks and loc sources, as git hashes them (line endings normalized). pack.ps1 -UsePrebuiltClientDlls and
# RO3Emu's update script compare it with the current sources.
function SourceStamp {
    Push-Location $root
    try {
        $files = git ls-files src/Ro3Emu.ClientHooks src/Ro3Emu.Loc | Where-Object { $_ -notmatch '/(bin|obj)/' }
        $hashes = $files | git hash-object --stdin-paths
        $text = (0..($files.Count - 1) | ForEach-Object { "$($files[$_]) $($hashes[$_])" }) -join "`n"
        $sha = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))
        return [Convert]::ToHexStringLower($sha)
    } finally { Pop-Location }
}

if ($StampOnly) { return SourceStamp }

$work = Join-Path ([IO.Path]::GetTempPath()) ("ro3emu-client-dlls-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
try {
    if (-not $ClientDir) {
        $ClientDir = Join-Path $work 'client'
        Run dotnet @('run', '--project', (Join-Path $root 'src/Ro3Emu.Setup'), '-c', 'Release', '--',
            'install', $ClientDir, '--only', 'ro3_Data/Managed/*', '--yes', '--home', (Join-Path $work 'home'))
    }
    if (-not (Test-Path (Join-Path $ClientDir 'ro3_Data/Managed/Assembly-CSharp.dll'))) { throw "no client DLLs in $ClientDir/ro3_Data/Managed" }

    # The client's original DLLs: Managed, with the originals of anything RO3Emu patched, without our own DLLs.
    $managed = Join-Path $work 'managed'
    New-Item -ItemType Directory $managed | Out-Null
    Copy-Item (Join-Path $ClientDir 'ro3_Data/Managed/*.dll') $managed
    $backup = Join-Path $ClientDir 'ro3emu-backup'
    if (Test-Path $backup) { Copy-Item (Join-Path $backup '*.dll') $managed -Force }
    Get-ChildItem $managed -Filter 'Ro3Emu.*.dll' | Remove-Item

    Run dotnet @('build', (Join-Path $root 'src/Ro3Emu.ClientHooks'), '-c', 'Release', '--no-incremental', '-nologo', '-v', 'q', "-p:ClientManaged=$managed", '-o', (Join-Path $work 'hooks-build'))
    Run dotnet @('build', (Join-Path $root 'src/Ro3Emu.Loc'), '-c', 'Release', '--no-incremental', '-nologo', '-v', 'q', "-p:ClientManaged=$managed", '-p:Ro3EmuNoClientData=true', '-o', (Join-Path $work 'loc-build'))

    New-Item -ItemType Directory -Force $Out | Out-Null
    Copy-Item (Join-Path $work 'hooks-build/Ro3Emu.ClientHooks.dll') $Out -Force
    Copy-Item (Join-Path $work 'loc-build/Ro3Emu.Loc.dll') $Out -Force
    Set-Content (Join-Path $Out 'source.sha256') (SourceStamp) -NoNewline
    Get-ChildItem $Out | ForEach-Object { Write-Host ("{0}  {1}" -f $_.Name, (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant()) }
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
