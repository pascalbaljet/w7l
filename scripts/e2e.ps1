$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$root = (Get-Location).Path
$evidence = Join-Path $root 'artifacts/e2e'
$work = Join-Path $root 'artifacts/e2e-work'
$runner = Join-Path $root 'artifacts/bin/KidLauncher.E2E/Release/KidLauncher.E2E.exe'
if (Test-Path $evidence) { Remove-Item $evidence -Recurse -Force }
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item $evidence, $work -ItemType Directory -Force | Out-Null
$hash = (Get-FileHash 'artifacts/KidLauncher-win7-x86.zip' -Algorithm SHA256).Hash
"Commit: $env:GITHUB_SHA`nPortable ZIP SHA256: $hash" | Set-Content "$evidence/build.txt"

function Invoke-Journey([string]$Name, [bool]$Negative) {
    $destination = Join-Path $work $Name
    $output = Join-Path $evidence $Name
    New-Item $output -ItemType Directory -Force | Out-Null
    Expand-Archive 'artifacts/KidLauncher-win7-x86.zip' $destination
    $app = Join-Path $destination 'KidLauncher'
    $arguments = "`"$app`" `"$output`""
    if ($Negative) { $arguments += ' --negative-control' }
    $process = Start-Process $runner -ArgumentList $arguments -PassThru -RedirectStandardOutput "$output/stdout.txt" -RedirectStandardError "$output/stderr.txt"
    try {
        if (-not $process.WaitForExit(180000)) {
            $process.Kill()
            throw "E2E $Name exceeded three minutes."
        }
        $process.WaitForExit()
        Get-Content "$output/stdout.txt"
        Get-Content "$output/stderr.txt"
        if ($Negative) {
            $result = Get-Content "$output/results.txt" -Raw
            if ($process.ExitCode -eq 0 -or $result -notmatch 'FAIL: System.TimeoutException: E2E cooperative game exits after a single F6') {
                throw 'Negative control must fail specifically because F6 cannot close the game when the GUI configures F7.'
            }
            Write-Host 'PASS: negative control detected the deliberately incorrect exit key.'
        } elseif ($process.ExitCode -ne 0) {
            throw "Packaged-app E2E journey failed ($($process.ExitCode))."
        }
    } finally {
        # A driver timeout cannot execute its managed finally. Clean up only
        # executables extracted/created in this disposable journey directory.
        Get-CimInstance Win32_Process | Where-Object {
            $_.ExecutablePath -and $_.ExecutablePath.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)
        } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        $logs = Join-Path $app 'Data/Logs'
        if (Test-Path $logs) { Copy-Item $logs "$output/application-logs" -Recurse -Force }
        $journal = Join-Path $app 'Data/display-session.json'
        if (Test-Path $journal) { Copy-Item $journal "$output/pending-session.json" }
        $frames = Join-Path $output 'frames'
        if (Test-Path "$frames/frame-000000.png") {
            & ffmpeg -hide_banner -loglevel error -y -framerate 5 -i "$frames/frame-%06d.png" -vf 'pad=ceil(iw/2)*2:ceil(ih/2)*2' -c:v libx264 -preset ultrafast -pix_fmt yuv420p -movflags +faststart "$output/journey.mp4"
            if ($LASTEXITCODE -ne 0) { throw "Could not encode $Name desktop recording; raw frames retained." }
            Remove-Item $frames -Recurse -Force
        }
    }
}

Invoke-Journey 'positive' $false
Invoke-Journey 'negative-control' $true
'PASS: packaged-app E2E and deliberate F7 negative control.' | Tee-Object "$evidence/summary.txt"
