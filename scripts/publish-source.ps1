# Puts the committed source (src/ and scripts/) into the public repository igayowai/TokenOverlay, next to its README
# and releases, as one commit under the GitHub noreply address. The public repository has no history of its own
# development: each run adds one commit with the tree as it is at this repository's HEAD. Uncommitted changes and
# everything outside src/ and scripts/ stay here. release.ps1 runs it after each release (user decision 2026-11-07:
# the whole source is public under the MIT license).
#   pwsh scripts/publish-source.ps1 -Version 0.9.9
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$repo = 'igayowai/TokenOverlay'
$root = Split-Path $PSScriptRoot -Parent
$clone = Join-Path $root 'publish\public-repo'
$name = 'igayowai'
$mail = '204812137+igayowai@users.noreply.github.com'

if (Test-Path -LiteralPath (Join-Path $clone '.git')) {
    git -C $clone fetch -q origin
    git -C $clone checkout -q main
    git -C $clone reset -q --hard origin/main
}
else {
    New-Item -ItemType Directory (Split-Path $clone -Parent) -Force | Out-Null
    gh repo clone $repo $clone -- -q
    if ($LASTEXITCODE) { throw 'clone failed' }
}

# Replace src/ and scripts/ there with the committed ones here.
foreach ($part in 'src', 'scripts') { Remove-Item -LiteralPath (Join-Path $clone $part) -Recurse -Force -ErrorAction SilentlyContinue }
$archive = Join-Path ([IO.Path]::GetTempPath()) "tokenoverlay-source-$Version.tar"
git -C $root archive --format=tar -o $archive HEAD src scripts
if ($LASTEXITCODE) { throw 'archive failed' }
# Windows' own tar by its full path: run from Git Bash, Git's GNU tar comes first on PATH and reads "C:" as a remote
# host, so nothing was extracted and 0.10.4's first public commit removed every file. An empty result stops here.
& (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $archive -C $clone
if ($LASTEXITCODE) { throw 'extract failed' }
Remove-Item -LiteralPath $archive -Force
if (-not (Test-Path -LiteralPath (Join-Path $clone 'src\TokenOverlay\TokenOverlay.csproj'))) { throw 'extracted source is missing the project' }
$ignore = Join-Path $clone '.gitignore'
if (-not (Test-Path -LiteralPath $ignore)) {
    Set-Content -LiteralPath $ignore -Encoding utf8 -Value @('bin/', 'obj/', 'publish/', 'releases/', '*.user', '.vs/')
}

git -C $clone add -A -- src scripts .gitignore
if (-not (git -C $clone status --porcelain -- src scripts .gitignore)) { "public source already at $Version"; return }
$message = "Source of TokenOverlay $Version`n`nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git -C $clone -c "user.name=$name" -c "user.email=$mail" commit -q -m $message
if ($LASTEXITCODE) { throw 'commit failed' }
git -C $clone push -q origin main
if ($LASTEXITCODE) { throw 'push failed' }
"public source ${Version}: " +(git -C $clone rev-parse --short HEAD)
