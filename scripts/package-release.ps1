param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

# Copy the tested CI binaries without rebuilding or changing their contents.
$destination = "artifacts/releases/$Version"
New-Item $destination -ItemType Directory -Force | Out-Null
$assets = @(
    @{ Source = 'artifacts/KidLauncher-Setup-win7-x86.exe'; Name = "KidLauncher-Setup-$Version-win7-x86.exe" },
    @{ Source = 'artifacts/KidLauncher-win7-x86.zip'; Name = "KidLauncher-$Version-win7-x86.zip" }
)
$checksums = foreach ($asset in $assets) {
    $path = Join-Path $destination $asset.Name
    Copy-Item $asset.Source $path -Force
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($asset.Name)"
}
$checksums | Set-Content (Join-Path $destination "SHA256SUMS-$Version.txt") -Encoding Ascii
Write-Host "Release assets: $destination"
