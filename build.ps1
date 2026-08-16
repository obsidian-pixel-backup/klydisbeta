# Builds the Windows x64 self-contained publish output for Klydis and prunes the
# non-Windows native binaries that leak in from the LLamaSharp.Backend.* packages (their
# asset layout defeats RID pruning). Keeping the artifact small matters because Cloudflare
# only edge-caches objects up to 512 MB on Free/Pro/Business.
#
# Deliberately NOT PublishSingleFile: the app loads native DLLs (llama.dll / ggml*.dll) at
# runtime and single-file extraction breaks the native engine lookup.
#
# Usage:  powershell -ExecutionPolicy Bypass -File .\build.ps1
# Output: src\Klydis.App\bin\Release\net10.0-windows\win-x64\publish\

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$appProject = Join-Path $repoRoot "src\Klydis.App\Klydis.App.csproj"

Write-Host "==> Publishing Klydis (Release, win-x64, self-contained)..."
dotnet publish $appProject -c Release -r win-x64 --self-contained true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$publishDir = Join-Path $repoRoot "src\Klydis.App\bin\Release\net10.0-windows\win-x64\publish"
if (-not (Test-Path $publishDir)) { throw "Publish output not found at $publishDir" }

function Get-DirSizeMb([string]$path) {
    $bytes = (Get-ChildItem $path -Recurse -File | Measure-Object -Property Length -Sum).Sum
    return [math]::Round($bytes / 1MB, 1)
}

$before = Get-DirSizeMb $publishDir
Write-Host ("==> Publish output before pruning: {0} MB" -f $before)

# Non-target RIDs shipped by the LLamaSharp backend packages — safe to delete from a
# win-x64 publish (zero functional loss on Windows x64; win-arm64 is not the target either).
$ridsToPrune = @("linux-x64", "linux-musl-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-arm64")
$removedBytes = 0L
foreach ($rid in $ridsToPrune) {
    $ridDir = Join-Path $publishDir "runtimes\$rid"
    if (Test-Path $ridDir) {
        $size = (Get-ChildItem $ridDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
        $removedBytes += $size
        Remove-Item $ridDir -Recurse -Force
        Write-Host ("    pruned runtimes/{0}  ({1} MB)" -f $rid, [math]::Round($size / 1MB, 1))
    }
}

$after = Get-DirSizeMb $publishDir
Write-Host ("==> Publish output after pruning:  {0} MB" -f $after)
Write-Host ("==> Removed {0} MB of non-Windows natives" -f [math]::Round($removedBytes / 1MB, 1))
Write-Host ""

Write-Host "Largest files (useful for sizing R2 parts / the installer payload):"
Get-ChildItem $publishDir -Recurse -File |
    Sort-Object Length -Descending |
    Select-Object -First 8 |
    ForEach-Object {
        Write-Host ("    {0,8:N1} MB  {1}" -f ($_.Length / 1MB), $_.FullName.Substring($publishDir.Length + 1))
    }
Write-Host ""
Write-Host ("Done. Publish output: {0}  ({1} MB total)" -f $publishDir, $after)
