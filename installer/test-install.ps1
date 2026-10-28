# Dev check: compiles a non-elevated variant of BagaGame.iss, installs it
# silently into %LocalAppData%, verifies the payload, starts the app and
# uninstalls again. The real installer stays per-machine.
#
# Run:  powershell -File installer\test-install.ps1

$ErrorActionPreference = 'Stop'

$project = Split-Path $PSScriptRoot -Parent
$local = Join-Path $env:LOCALAPPDATA 'BagaGame'
$jsonPath = Join-Path $local 'library.json'
$backup = Join-Path $project '.artifacts\state-backup\preinstall-library.json'
$testDir = Join-Path $project '.artifacts\installer-test'

$entry = Get-ItemProperty @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*') -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like 'Inno Setup*' } | Select-Object -First 1
$iscc = Join-Path $entry.InstallLocation 'ISCC.exe'
if (-not (Test-Path $iscc)) { throw 'ISCC.exe not found' }

# ------------------------------------------------------- non-elevated variant

New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$iss = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'BagaGame.iss'), [Text.Encoding]::UTF8)
$iss = $iss -replace [regex]::Escape('#define PublishDir "..\.artifacts\publish"'), ('#define PublishDir "' + (Join-Path $project '.artifacts\publish') + '"')
$iss = $iss -replace [regex]::Escape('#define ReadmeFile "..\README.md"'), ('#define ReadmeFile "' + (Join-Path $project 'README.md') + '"')
$iss = $iss -replace [regex]::Escape('SetupIconFile=..\Assets\bagagame.ico'), ('SetupIconFile=' + (Join-Path $project 'Assets\bagagame.ico'))
$iss = $iss -replace 'WizardImageFile=wizard-large\.bmp', ('WizardImageFile=' + (Join-Path $PSScriptRoot 'wizard-large.bmp'))
$iss = $iss -replace 'WizardSmallImageFile=wizard-small\.bmp', ('WizardSmallImageFile=' + (Join-Path $PSScriptRoot 'wizard-small.bmp'))
$iss = $iss -replace 'PrivilegesRequired=admin', 'PrivilegesRequired=lowest'
$iss = $iss -replace 'PrivilegesRequiredOverridesAllowed=dialog', ''
$iss = $iss -replace [regex]::Escape('DefaultDirName={autopf}\{#AppName}'), 'DefaultDirName={localappdata}\BagaInstallTest'
$iss = $iss -replace [regex]::Escape('OutputBaseFilename={#AppName}-{#AppVersion}-setup'), 'OutputBaseFilename=BagaGame-test-setup'
$iss = $iss -replace [regex]::Escape('OutputDir=..\dist'), ('OutputDir=' + $testDir)

$testIss = Join-Path $testDir 'BagaGame.test.iss'
[IO.File]::WriteAllText($testIss, $iss, (New-Object Text.UTF8Encoding $true))

& $iscc $testIss 2>&1 | Select-Object -Last 3
$setup = Join-Path $testDir 'BagaGame-test-setup.exe'
if (-not (Test-Path $setup)) { throw 'test setup was not produced' }
Write-Output ("setup: {0} bytes" -f (Get-Item $setup).Length)

# ---------------------------------------------------------------- install

New-Item -ItemType Directory -Force -Path (Split-Path $backup -Parent) | Out-Null
if (Test-Path $jsonPath) { Copy-Item $jsonPath $backup -Force }

$installDir = Join-Path $env:LOCALAPPDATA 'BagaInstallTest'
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }

$log = Join-Path $testDir 'install.log'
$proc = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG=' + $log) -PassThru -Wait
Write-Output ("installer exit code: " + $proc.ExitCode)

try {
    if ($proc.ExitCode -ne 0) { throw "installer failed: $($proc.ExitCode)" }

    $exe = Join-Path $installDir 'BagaGame.exe'
    $installed = Get-ChildItem $installDir -File -ErrorAction SilentlyContinue
    Write-Output ("installed exe present: " + (Test-Path $exe))
    Write-Output ("installed files: " + $installed.Count)
    Write-Output ("readme installed: " + (Test-Path (Join-Path $installDir 'README.md')))
    Write-Output ("pdb excluded: " + (-not (Test-Path (Join-Path $installDir 'BagaGame.pdb'))))
    Write-Output ("uninstaller: " + (Test-Path (Join-Path $installDir 'unins000.exe')))
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\BagaGame\BagaGame.lnk'
    Write-Output ("start menu shortcut: " + (Test-Path $startMenu))

    $reg = Get-ChildItem 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
        Where-Object { $_.DisplayName -like 'BagaGame*' } | Select-Object -First 1
    Write-Output ("uninstall entry: " + $(if ($reg) { $reg.DisplayName + ' ' + $reg.DisplayVersion } else { 'MISSING' }))

    # The installed app must actually start.
    $app = Start-Process -FilePath $exe -PassThru
    $started = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $app.Refresh()
        if ($app.HasExited) { break }
        if ($app.MainWindowHandle -ne 0) { $started = $true; break }
    }
    Write-Output ("installed app starts: " + $started)
    if (-not $app.HasExited) { $app | Stop-Process -Force }
    Start-Sleep -Milliseconds 700
}
finally {
    $uninstaller = Join-Path $installDir 'unins000.exe'
    if (Test-Path $uninstaller) {
        $un = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
        Write-Output ("uninstaller exit code: " + $un.ExitCode)
    }
    Start-Sleep -Milliseconds 800
    Write-Output ("install dir removed: " + (-not (Test-Path $installDir)))
    if (Test-Path $backup) { Copy-Item $backup $jsonPath -Force }
    Write-Output 'user data restored'
}
