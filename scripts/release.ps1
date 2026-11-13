# Builds a version, packs it with Velopack and publishes it as a release of the public repository,
# from which every installed overlay updates itself. GitHub sign-in is the GitHub CLI's own (gh auth login).
#   pwsh scripts/release.ps1 -Version 0.4.1 -Notes "무엇이 바뀌었는지"
# -TestFeed: a signed build on its own channel (signedtest) into publish/test-feed, nothing uploaded. Such a build
# updates only from the folder named in %APPDATA%\TokenOverlay\update-source.txt, for trying the laptop's App
# Control policy across two versions before any public release is signed.
#   pwsh scripts/release.ps1 -Version 0.7.6 -TestFeed
# Test builds are always signed; public ones only with -Sign until public signing is switched on.
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Notes = "",
    [switch]$TestFeed,
    [switch]$Sign
)
$ErrorActionPreference = 'Stop'
$repo = 'igayowai/TokenOverlay'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\TokenOverlay\TokenOverlay.csproj'
$publish = Join-Path $root 'publish\win-x64'
$releases = Join-Path $root ($TestFeed ? 'publish\test-feed' : 'releases')
$channel = $TestFeed ? 'signedtest' : 'win'
$signed = $TestFeed -or $Sign
if ($signed) { . (Join-Path $PSScriptRoot 'signing.ps1'); $signParams = Get-SignParams }

if (-not $TestFeed -and (gh release view "v$Version" --repo $repo 2>$null)) { throw "v$Version is already released" }
if ($TestFeed -and (Test-Path (Join-Path $releases "TokenOverlay-$Version-$channel-full.nupkg"))) { throw "test build $Version already exists" }
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
# Not a single file: then an update only carries the files that changed.
$testFeedProperty = $TestFeed ? @('-p:TokenOverlayTestFeed=true') : @()
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=$Version @testFeedProperty -o $publish --nologo -v q
if ($LASTEXITCODE) { throw 'publish failed' }

New-Item -ItemType Directory $releases -Force | Out-Null
# The previous release, so a small delta package can be made next to the full one (none yet on the first release).
# A test feed keeps its own earlier builds in its folder.
if (-not $TestFeed) { vpk download github --repoUrl "https://github.com/$repo" -o $releases }
$signArgs = $signed ? @('--signParams', $signParams) : @()
vpk pack --packId TokenOverlay --packVersion $Version --packDir $publish --mainExe TokenOverlay.exe `
    --runtime win-x64 --packTitle TokenOverlay --packAuthors igayowai --icon (Join-Path $root 'src\TokenOverlay\app.ico') `
    --shortcuts StartMenuRoot --channel $channel @signArgs -o $releases
if ($LASTEXITCODE) { throw 'pack failed' }
if ($signed) { Assert-ReleaseSigned $releases $Version $channel }
if ($TestFeed) { "test build $Version (signed, channel $channel) in $releases"; return }

$assets = @(
    Get-ChildItem $releases -File | Where-Object {
        $_.Name -like "TokenOverlay-$Version-*.nupkg" -or $_.Name -in @('releases.win.json', 'RELEASES', 'assets.win.json', 'TokenOverlay-win-Setup.exe', 'TokenOverlay-win-Portable.zip')
    } | ForEach-Object FullName
)
if (-not $Notes) { $Notes = "TokenOverlay $Version" }
gh release create "v$Version" @assets --repo $repo --title "TokenOverlay $Version" --notes $Notes
if ($LASTEXITCODE) { throw 'upload failed' }
"released v$Version with $($assets.Count) files"
# The public repository carries each release's source too (src/ and scripts/ as committed here).
& (Join-Path $PSScriptRoot 'publish-source.ps1') -Version $Version
