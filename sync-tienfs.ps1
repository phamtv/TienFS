# =====================================================================================
# SYNC-TIENFS.PS1
# -------------------------------------------------------------------------------------
# Copies an extracted TienFS.zip (or any updated file set) over your existing git
# repo, WITHOUT touching .git, build output (bin/obj), or local SQLite/db leftovers.
# Safe to re-run any time you get a fresh zip.
#
# USAGE:
#   .\sync-tienfs.ps1 -Source "C:\path\to\extracted\TienFS" -Destination "C:\path\to\your\TienFS"
# =====================================================================================

param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Destination
)

if (-not (Test-Path $Source)) {
    Write-Error "Source folder not found: $Source"
    exit 1
}

if (-not (Test-Path $Destination)) {
    Write-Error "Destination folder not found: $Destination"
    exit 1
}

$gitFolder = Join-Path $Destination ".git"
if (-not (Test-Path $gitFolder)) {
    Write-Error "No .git folder found in destination -- is this really your repo? Aborting to be safe."
    exit 1
}

Write-Host "Syncing:" -ForegroundColor Cyan
Write-Host "  From: $Source"
Write-Host "  To:   $Destination"
Write-Host ""

$excludeDirs = @(".git", "bin", "obj", ".vs")
$excludeFiles = @("*.db", "*.db-shm", "*.db-wal")

robocopy $Source $Destination /E /XD $excludeDirs /XF $excludeFiles /NP

if ($LASTEXITCODE -ge 8) {
    Write-Host ""
    Write-Host "robocopy reported an error (exit code $LASTEXITCODE). Check the output above." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Done. Now review the changes:" -ForegroundColor Green
Write-Host "  cd $Destination"
Write-Host "  git status"
