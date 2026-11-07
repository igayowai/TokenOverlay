# Tries a build on this PC before it is released: replaces the installed overlay's files with a fresh build,
# keeping the collected model-request times (the running overlay is asked to exit normally and saves them).
#   pwsh scripts/deploy-local.ps1 [-BackupName before-x.zip]
param([string]$BackupName = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\TokenOverlay\TokenOverlay.csproj'
$build = Join-Path $root 'publish\local'
$installed = Join-Path $env:LOCALAPPDATA 'TokenOverlay\current'
$exe = Join-Path $installed 'TokenOverlay.exe'
if (-not (Test-Path $exe)) { throw "TokenOverlay is not installed on this PC ($installed)" }

Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $build --nologo -v q
if ($LASTEXITCODE) { throw 'publish failed' }
if ($BackupName) {
    $backups = Join-Path $root 'publish\backups'
    New-Item -ItemType Directory $backups -Force | Out-Null
    Compress-Archive -Path (Join-Path $installed '*') -DestinationPath (Join-Path $backups $BackupName) -Force
}

function Running { Get-CimInstance Win32_Process -Filter "Name='TokenOverlay.exe'" | Where-Object { $_.ExecutablePath -eq $exe } }
$cache = Join-Path $env:APPDATA 'TokenOverlay\model-rate-cache.json'
$before = (Get-Item $cache -ErrorAction SilentlyContinue).LastWriteTime
$clock = [Diagnostics.Stopwatch]::StartNew()
foreach ($p in (Running | Where-Object { $_.CommandLine -match '--watch-codex' })) { Stop-Process -Id $p.ProcessId -Force -Confirm:$false }
$quit = Start-Process (Join-Path $build 'TokenOverlay.exe') -ArgumentList '--quit' -PassThru -WindowStyle Hidden
[void]$quit.WaitForExit(8000)
$graceful = -not (Running)
foreach ($p in Running) { Stop-Process -Id $p.ProcessId -Force -Confirm:$false }
for ($i = 0; $i -lt 40 -and (Running); $i++) { [Threading.Thread]::Sleep(50) }
# A release the overlay downloaded is applied by Velopack's Update.exe once the overlay exits, over the installed
# folder, and it restarts the overlay. Let it finish and close that overlay, or it would overwrite this build.
$updater = Join-Path (Split-Path $installed -Parent) 'Update.exe'
function Updating { Get-CimInstance Win32_Process -Filter "Name='Update.exe'" | Where-Object { $_.ExecutablePath -eq $updater } }
[Threading.Thread]::Sleep(1000)
if (Updating) {
    for ($i = 0; $i -lt 600 -and (Updating); $i++) { [Threading.Thread]::Sleep(100) }
    [Threading.Thread]::Sleep(2000)
    $quit = Start-Process (Join-Path $build 'TokenOverlay.exe') -ArgumentList '--quit' -PassThru -WindowStyle Hidden
    [void]$quit.WaitForExit(8000)
    foreach ($p in Running) { Stop-Process -Id $p.ProcessId -Force -Confirm:$false }
    for ($i = 0; $i -lt 40 -and (Running); $i++) { [Threading.Thread]::Sleep(50) }
    "velopack applied a downloaded release first; replaced it with this build"
}
# Copies over the installed files; Velopack's own files there (sq.version) stay.
robocopy $build $installed /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy failed ($LASTEXITCODE)" }
Start-Process $exe -WindowStyle Hidden
$down = $clock.ElapsedMilliseconds
schtasks /Run /TN TokenOverlay | Out-Null
[Threading.Thread]::Sleep(3000)
$sha = (Get-FileHash (Join-Path $installed 'TokenOverlay.dll') -Algorithm SHA256).Hash.ToLower()
if ($sha -ne (Get-FileHash (Join-Path $build 'TokenOverlay.dll') -Algorithm SHA256).Hash.ToLower()) { throw "installed dll is not this build ($sha)" }
"dll_sha=$sha graceful=$graceful restart_ms=$down cache_saved_on_exit=$((Get-Item $cache -ErrorAction SilentlyContinue).LastWriteTime -gt $before)"
Running | ForEach-Object { "$($_.ProcessId) $($_.CommandLine)" }
"port 43189 listening=$([bool](Get-NetTCPConnection -LocalPort 43189 -State Listen -ErrorAction SilentlyContinue))"
