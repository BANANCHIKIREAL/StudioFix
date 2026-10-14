param([Parameter(Mandatory = $true)][string]$CompilerPath, [string]$TestRoot = (Join-Path $env:TEMP ('studiofix-installer-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$studioProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$studioRoot = [IO.Path]::GetFullPath($TestRoot)
$studioId = [guid]::NewGuid().ToString()
$studioShortcutName = 'StudioFix-Test-' + $studioId
$studioInstall = Join-Path $studioRoot 'installed'
$studioOutput = Join-Path $studioRoot 'setup'
$studioDesktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) ($studioShortcutName + '.lnk')
$studioProgramsLink = Join-Path ([Environment]::GetFolderPath('Programs')) ($studioShortcutName + '.lnk')
$studioPublicShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'TikTok LIVE Studio.lnk'
$studioBefore = if (Test-Path -LiteralPath $studioPublicShortcut) { (Get-FileHash -LiteralPath $studioPublicShortcut).Hash } else { $null }
if (Test-Path -LiteralPath $studioInstall) { throw 'Use a new test directory.' }
New-Item -ItemType Directory -Path $studioOutput -Force | Out-Null
& $CompilerPath '/Q' '/DTestInstall=yes' ('/DInstallId={{' + $studioId + '}') ('/DShortcutName=' + $studioShortcutName) ('/DBuildDir=' + (Join-Path $studioProject 'build')) ('/DReleaseDir=' + $studioOutput) (Join-Path $studioProject 'installer\StudioFix.iss')
if ($LASTEXITCODE -ne 0) { throw 'Test installer compilation failed.' }
$studioSetup = Join-Path $studioOutput 'StudioFix-Setup.exe'
$studioArgs = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LANG=russian /TASKS=desktopicon /DIR="' + $studioInstall + '"'
$studioInstalled = $false
try {
    $studioProcess = Start-Process -FilePath $studioSetup -ArgumentList $studioArgs -WindowStyle Hidden -PassThru -Wait
    if ($studioProcess.ExitCode -ne 0) { throw ('Install failed: ' + $studioProcess.ExitCode) }
    $studioInstalled = $true
    $studioExe = Join-Path $studioInstall 'StudioFix.exe'
    $studioExpectedHash = (Get-FileHash -LiteralPath (Join-Path $studioProject 'build\StudioFix.exe')).Hash
    if ((Get-FileHash -LiteralPath $studioExe).Hash -ne $studioExpectedHash) { throw 'Installer payload differs.' }
    $studioShell = New-Object -ComObject WScript.Shell
    foreach ($studioLink in @($studioDesktopLink,$studioProgramsLink)) {
        if (-not (Test-Path -LiteralPath $studioLink) -or $studioShell.CreateShortcut($studioLink).TargetPath -ne $studioExe) { throw 'Own shortcut missing or incorrect.' }
    }
    $studioState = Join-Path $studioInstall 'test-data'
    New-Item -ItemType Directory -Path $studioState | Out-Null
    [IO.File]::WriteAllText((Join-Path $studioState 'settings.json'),'{"Repository":"BANANCHIKIREAL/StudioFix","AutoCheckUpdates":false}')
    $studioSavedHash = (Get-FileHash -LiteralPath (Join-Path $studioState 'settings.json')).Hash
    $studioLaunchArgs = '--preview supported --output "' + (Join-Path $studioRoot 'installed-ui.png') + '" --data-root "' + $studioState + '"'
    $studioProcess = Start-Process -FilePath $studioExe -ArgumentList $studioLaunchArgs -WindowStyle Hidden -PassThru -Wait
    if ($studioProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $studioRoot 'installed-ui.png'))) { throw 'Installed app could not render.' }
    $studioProcess = Start-Process -FilePath $studioSetup -ArgumentList $studioArgs -WindowStyle Hidden -PassThru -Wait
    if ($studioProcess.ExitCode -ne 0 -or (Get-FileHash -LiteralPath (Join-Path $studioState 'settings.json')).Hash -ne $studioSavedHash) { throw 'Repeated installation failed or erased settings.' }
    $studioUninstall = Join-Path $studioInstall 'unins000.exe'
    $studioProcess = Start-Process -FilePath $studioUninstall -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -PassThru -Wait
    if ($studioProcess.ExitCode -ne 0) { throw ('Uninstall failed: ' + $studioProcess.ExitCode) }
    $studioInstalled = $false
    $studioDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Test-Path -LiteralPath $studioExe) -and [DateTime]::UtcNow -lt $studioDeadline) { Start-Sleep -Milliseconds 100 }
    if ((Test-Path -LiteralPath $studioExe) -or (Test-Path -LiteralPath $studioDesktopLink) -or (Test-Path -LiteralPath $studioProgramsLink)) { throw 'Uninstaller left payload or shortcuts.' }
    if ((Get-FileHash -LiteralPath (Join-Path $studioState 'settings.json')).Hash -ne $studioSavedHash) { throw 'Uninstall removed retained data.' }
    if ($studioBefore -and (Get-FileHash -LiteralPath $studioPublicShortcut).Hash -ne $studioBefore) { throw 'Ordinary Studio shortcut changed.' }
    $studioResult = [ordered]@{Success=$true; Installation=$true; OwnDesktopAndStartMenuShortcuts=$true; InstalledAppRender=$true; ReinstallationPreservesData=$true; Uninstall=$true; StudioShortcutUnchanged=$true}
    [IO.File]::WriteAllText((Join-Path $studioRoot 'installer-results.json'), ($studioResult | ConvertTo-Json))
    Write-Output ($studioResult | ConvertTo-Json -Compress)
}
finally {
    if ($studioInstalled -and (Test-Path -LiteralPath (Join-Path $studioInstall 'unins000.exe'))) {
        Start-Process -FilePath (Join-Path $studioInstall 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -Wait
    }
}
