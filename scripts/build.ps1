param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet build KidLauncher.sln --configuration $Configuration --property:Platform=x86
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& "artifacts/bin/KidLauncher.Tests/$Configuration/KidLauncher.Tests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
$package = 'artifacts/KidLauncher'
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item $package -ItemType Directory -Force | Out-Null
foreach ($project in @('KidLauncher', 'KidLauncher.Configurator')) {
    Copy-Item "artifacts/bin/$project/$Configuration/*" $package -Recurse -Force
}
foreach ($directory in @('Assets','Disc Images','Data','Logs','Backups')) {
    New-Item "$package/$directory" -ItemType Directory -Force | Out-Null
}
Copy-Item examples/games.json "$package/games.json"
Copy-Item README.md, LICENSE $package
Copy-Item docs "$package/docs" -Recurse
Compress-Archive -Path $package -DestinationPath artifacts/KidLauncher-win7-x86.zip -Force
Write-Host 'Portable build: artifacts/KidLauncher-win7-x86.zip'
