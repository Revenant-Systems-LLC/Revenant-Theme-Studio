# Build-Release.ps1
# Builds the RTS distribution zip.
# Includes: exe, MunWriter, ruby-theme + gunmetal-theme (Pro-gated in UI).
# Output: dist\Revenant-Theme-Studio-<version>-win-x64.zip
#
# Usage: pwsh -File .\scripts\Build-Release.ps1
# Run from the project root or from the scripts folder — both work.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path $PSScriptRoot -Parent
$version     = "1.0.0"
$zipName     = "Revenant-Theme-Studio-$version-win-x64.zip"
$outputZip   = Join-Path $projectRoot "dist\$zipName"
$stagingDir  = Join-Path $env:TEMP "rts-staging"

Write-Output "=== RTS Release Builder ==="
Write-Output "Project root : $projectRoot"
Write-Output "Output zip   : $outputZip"
Write-Output ""

# 1. Publish
Write-Output "[1/4] Publishing..."
dotnet publish "$projectRoot\Revenant-Theme-Studio.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -o "$projectRoot\dist\publish"
Write-Output "      Done."

# 2. Stage
Write-Output "[2/4] Staging..."
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item $stagingDir -ItemType Directory | Out-Null

Copy-Item "$projectRoot\dist\publish\Revenant-Theme-Studio.exe" $stagingDir
Copy-Item "$projectRoot\dist\publish\RTS.MunWriter.exe"         $stagingDir

$iconSrc = Join-Path $projectRoot "IconEngine"
$iconDst = Join-Path $stagingDir  "IconEngine"
New-Item $iconDst -ItemType Directory | Out-Null

$includedPacks = @("ruby-theme", "gunmetal-theme")

foreach ($pack in $includedPacks) {
    $src = Join-Path $iconSrc $pack
    if (-not (Test-Path $src)) { Write-Warning "Pack not found: $pack"; continue }
    $dst = Join-Path $iconDst $pack
    Copy-Item $src $dst -Recurse
    $count = (Get-ChildItem $dst -File -Recurse).Count
    Write-Output "      Copied $pack ($count files)"
}
Write-Output "      Staged."

# 3. Zip
Write-Output "[3/4] Zipping..."
if (Test-Path $outputZip) { Remove-Item $outputZip -Force }
Compress-Archive -Path "$stagingDir\*" -DestinationPath $outputZip -CompressionLevel Optimal
$sizeMB = [math]::Round((Get-Item $outputZip).Length / 1MB, 1)
Write-Output "      $zipName — $sizeMB MB"

# 4. Cleanup
Write-Output "[4/4] Cleaning staging..."
Remove-Item $stagingDir -Recurse -Force
Write-Output "      Done."

Write-Output ""
Write-Output "=== BUILD COMPLETE ==="
Write-Output "Zip ready at: $outputZip"
Write-Output "Upload this to public_html/downloads/ on the server."
