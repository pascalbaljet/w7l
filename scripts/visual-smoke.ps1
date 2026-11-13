$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$output = Join-Path (Get-Location) 'artifacts/visual'
New-Item $output -ItemType Directory -Force | Out-Null
function Invoke-Smoke([string]$Executable, [string]$Arguments) {
    $process = Start-Process $Executable -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit(45000)) {
        $process.Kill()
        throw "Visual smoke test timed out: $Executable"
    }
    if ($process.ExitCode -ne 0) { throw "Visual smoke test failed: $Executable ($($process.ExitCode))" }
}
Invoke-Smoke 'artifacts/bin/KidLauncher/Release/KidLauncher.exe' "--smoke-test --screenshots `"$output`""
Invoke-Smoke 'artifacts/bin/KidLauncher.Configurator/Release/KidLauncher.Configurator.exe' "--smoke-test --screenshot `"$output/configurator.png`""
foreach ($image in @('launcher-default.png','launcher-page-2.png','launcher-loading.png','launcher-returning.png','launcher-error.png','launcher-empty.png',
    'launcher-artwork.png','launcher-no-titles.png','launcher-wallpaper.png',
    'launcher-english-default.png','launcher-english-page-2.png','launcher-english-loading.png','launcher-english-returning.png','launcher-english-error.png','launcher-english-empty.png','configurator.png')) {
    if (-not (Test-Path "$output/$image")) { throw "Visual capture missing: $image" }
}
Get-Content "$output/launcher-checks.txt"
Write-Host 'WPF visual smoke checks and captures completed.'
