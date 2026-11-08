$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$root = (Get-Location).Path
$setup = Join-Path $root 'artifacts/KidLauncher-Setup-win7-x86.exe'
$destination = Join-Path $root 'artifacts/installer-test/KidLauncher'
$evidence = Join-Path $root 'artifacts/installer-evidence'
New-Item $evidence -ItemType Directory -Force | Out-Null
if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }

function Install-Quiet {
    $process = Start-Process $setup -ArgumentList "--quiet --install-dir `"$destination`"" -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Installer timed out.' }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Installer failed ($($process.ExitCode))." }
}

Install-Quiet
if ((Get-Content "$destination/games.json" -Raw | ConvertFrom-Json).Language -ne 'nl') { throw 'Fresh installation must default to Dutch.' }
$preserved = @('games.json', 'games.json.bak', 'Data/display-session.json', 'Data/Logs/game.log', 'Logs/old.log', 'Assets/cover.png', 'Disc Images/game.iso', 'Backups/saved.json', 'personal.txt')
$hashes = @{}
foreach ($relative in $preserved) {
    $path = Join-Path $destination $relative
    New-Item (Split-Path $path -Parent) -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText($path, "User data must survive: $relative")
    $hashes[$relative] = (Get-FileHash $path -Algorithm SHA256).Hash
}
[IO.File]::WriteAllText("$destination/KidLauncher.Core.dll", 'Old application binary to replace')
Install-Quiet
foreach ($relative in $preserved) {
    if ((Get-FileHash (Join-Path $destination $relative) -Algorithm SHA256).Hash -ne $hashes[$relative]) { throw "Setup overwrote $relative" }
}
if ((Get-FileHash "$destination/KidLauncher.Core.dll").Hash -ne (Get-FileHash 'artifacts/KidLauncher/KidLauncher.Core.dll').Hash) { throw 'Setup did not update the application binary.' }

$runner = Join-Path $root 'artifacts/bin/KidLauncher.E2E/Release/KidLauncher.E2E.exe'
$guiDestination = Join-Path $root 'artifacts/installer-test/GUI'
& $runner --installer $setup $guiDestination $evidence
if ($LASTEXITCODE -ne 0) { throw 'Installer GUI journey failed.' }
& ffmpeg -hide_banner -loglevel error -y -framerate 5 -i "$evidence/frames/frame-%06d.png" -vf 'pad=ceil(iw/2)*2:ceil(ih/2)*2' -c:v libx264 -preset ultrafast -pix_fmt yuv420p -movflags +faststart "$evidence/installer.mp4"
if ($LASTEXITCODE -ne 0) { throw 'Installer recording encoding failed; frames retained.' }
Remove-Item "$evidence/frames" -Recurse -Force
'PASS: fresh Dutch installation; update replaces binaries and preserves configuration, recovery, logs, artwork, disc images, backups and unrelated files; GUI install succeeds.' | Tee-Object "$evidence/summary.txt"
