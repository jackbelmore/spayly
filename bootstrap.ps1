# For someone with no clone yet: checks git, git-lfs and Node, clones Spayly, runs start.cmd. Installs nothing.
#   irm https://raw.githubusercontent.com/jackbelmore/spayly/main/bootstrap.ps1 | iex
# $env:SPAYLY_DIR sets the clone folder (default .\spayly).
$ErrorActionPreference = 'Stop'

$repo = 'https://github.com/jackbelmore/spayly'
$dir = if ($env:SPAYLY_DIR) { $env:SPAYLY_DIR } else { 'spayly' }

$missing = @()
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { $missing += 'git' }
elseif (-not (git lfs version 2>$null)) { $missing += 'git-lfs' }
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node -or [int]((node -p "process.versions.node.split('.')[0]")) -lt 22) { $missing += 'node>=22' }

if ($missing.Count -gt 0) {
  Write-Host "bootstrap: missing: $($missing -join ', ')"
  Write-Host '  winget install Git.Git GitHub.GitLFS OpenJS.NodeJS.LTS'
  Write-Host 'Then open a NEW terminal (PATH does not refresh) and run this again. (start.cmd will list the rest, such as the .NET SDK.)'
  return
}

git lfs install --skip-repo *> $null # before the clone, so the textures arrive as images and not pointer files

if ((Test-Path (Join-Path $dir 'start.cmd')) -and (Test-Path (Join-Path $dir '.git'))) {
  Write-Host "==> $dir is already a spayly clone; not cloning"
} else {
  Write-Host "==> Cloning $repo into $dir"
  git clone $repo $dir
  if ($LASTEXITCODE -ne 0) { Write-Host 'bootstrap: git clone failed'; return }
}

Set-Location $dir
& .\start.cmd
