<#
.SYNOPSIS
    Reassembles the Smeagle 4B Q8_0 GGUF from its committed part files and verifies it
    against the pinned SHA-256 in the model manifest.

.DESCRIPTION
    The Q8_0 GGUF (~4.3 GiB) cannot be committed to GitHub as a single file (100 MB
    per-file push limit; Git LFS caps at 2 GiB on Free/Pro and 4 GiB on Team). It is
    instead stored as ~47 zero-padded parts named <model>.gguf.partNN under
    assets/models/Hob-forge_smeagle-4b/. This script concatenates them back into the
    .gguf the runtime loads, then verifies size and SHA-256 against the manifest so a
    corrupt or truncated assembly is never used.

    Idempotent: if the .gguf already exists and matches the manifest, it does nothing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\restore-smeagle.ps1
#>
$ErrorActionPreference = "Stop"

$manifestDir = "assets\models\Hob-forge_smeagle-4b"
$manifestPath = Join-Path $manifestDir "manifest.json"
if (-not (Test-Path $manifestPath)) { throw "Manifest not found: $manifestPath" }

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$targetName = $manifest.fileName
if (-not $targetName) { throw "Manifest has no 'fileName'." }
$targetPath = Join-Path $manifestDir $targetName

$expectedSha256 = ""
if ($manifest.sha256) { $expectedSha256 = ([string]$manifest.sha256).ToLowerInvariant() }
$expectedSize = [long]0
if ($manifest.sizeBytes) { $expectedSize = [long]$manifest.sizeBytes }

function Get-Sha256([string]$path) {
    return (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Already present and valid?
if (Test-Path $targetPath) {
    $fileSize = (Get-Item $targetPath).Length
    if ($expectedSize -gt 0 -and $fileSize -ne $expectedSize) {
        throw "Existing $targetPath is $fileSize bytes but the manifest expects $expectedSize. Delete it and re-run."
    }
    if ($expectedSha256 -and (Get-Sha256 $targetPath) -ne $expectedSha256) {
        throw "Existing $targetPath fails the SHA-256 check. Delete it and re-run."
    }
    Write-Host "OK - $targetName already present and matches the manifest (SHA-256 verified)."
    exit 0
}

# Collect parts, ordered by their numeric suffix (part00, part01, ... part46).
$parts = Get-ChildItem -Path $manifestDir -File |
    Where-Object { $_.Name -like "$targetName.part*" } |
    Sort-Object { [int]([regex]::Match($_.Name, '\.part(\d+)$').Groups[1].Value) }

if ($parts.Count -eq 0) {
    throw "No part files found for $targetName. The clone may be incomplete - run git pull again."
}

$totalBytes = ($parts | Measure-Object -Property Length -Sum).Sum
if ($expectedSize -gt 0 -and $totalBytes -ne $expectedSize) {
    throw "Parts sum to $totalBytes bytes but the manifest expects $expectedSize - parts are corrupt or incomplete."
}

Write-Host "Reassembling $targetName from $($parts.Count) parts ($totalBytes bytes)..."
$out = [System.IO.File]::Open($targetPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
try {
    foreach ($part in $parts) {
        $in = [System.IO.File]::OpenRead($part.FullName)
        try { $in.CopyTo($out) } finally { $in.Dispose() }
    }
}
finally { $out.Dispose() }

if ($expectedSha256) {
    $actual = Get-Sha256 $targetPath
    if ($actual -ne $expectedSha256) {
        Remove-Item $targetPath -Force
        throw "SHA-256 mismatch after assembly (expected $expectedSha256, got $actual). Deleted the partial file - parts may be corrupt."
    }
}

Write-Host "OK - $targetName restored and verified (SHA-256: $expectedSha256)."
