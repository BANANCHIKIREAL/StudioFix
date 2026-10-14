param(
    [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
    [Parameter(Mandatory = $true)][string]$CompilerPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\build\release'),
    [string]$Notes = 'Обновление StudioFix. Настройки и резервные копии сохраняются.'
)
$ErrorActionPreference = 'Stop'
$studioProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$studioRelease = [IO.Path]::GetFullPath($OutputDirectory)
$studioCompiler = [IO.Path]::GetFullPath($CompilerPath)
if (-not (Test-Path -LiteralPath $studioCompiler -PathType Leaf)) { throw 'ISCC.exe not found. Install Inno Setup 6.6 or newer.' }
$studioSigner = New-Object Security.Cryptography.RSACryptoServiceProvider
$studioSigner.PersistKeyInCsp = $false
try {
    $studioSigner.FromXmlString([IO.File]::ReadAllText([IO.Path]::GetFullPath($PrivateKeyPath)))
    if ($studioSigner.PublicOnly -or $studioSigner.ToXmlString($false) -ne [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'public-key.xml')).Trim()) { throw 'Wrong signing key.' }
    & (Join-Path $PSScriptRoot 'Sign-Rules.ps1') -PrivateKeyPath $PrivateKeyPath
    & (Join-Path $studioProject 'build.ps1') -OutputDirectory (Join-Path $studioProject 'build')
    $studioExe = Join-Path $studioProject 'build\StudioFix.exe'
    $studioVersionObject = [version][Diagnostics.FileVersionInfo]::GetVersionInfo($studioExe).FileVersion
    $studioVersion = '{0}.{1}.{2}' -f $studioVersionObject.Major, $studioVersionObject.Minor, $studioVersionObject.Build
    New-Item -ItemType Directory -Path $studioRelease -Force | Out-Null
    & $studioCompiler (('/DAppVersion=' + $studioVersion)) (('/DBuildDir=' + [IO.Path]::GetDirectoryName($studioExe))) (('/DReleaseDir=' + $studioRelease)) (Join-Path $studioProject 'installer\StudioFix.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $studioInstaller = Join-Path $studioRelease 'StudioFix-Setup.exe'
    $studioManifest = [ordered]@{Schema=1; Version=$studioVersion; Repository='BANANCHIKIREAL/StudioFix'; Tag=('v' + $studioVersion); Asset='StudioFix-Setup.exe'; Sha256=(Get-FileHash -LiteralPath $studioInstaller -Algorithm SHA256).Hash; Size=(Get-Item -LiteralPath $studioInstaller).Length; Notes=$Notes}
    $studioManifestBytes = [Text.Encoding]::UTF8.GetBytes(($studioManifest | ConvertTo-Json -Depth 5))
    [IO.File]::WriteAllBytes((Join-Path $studioRelease 'studiofix-update.json'), $studioManifestBytes)
    $studioSignature = $studioSigner.SignData($studioManifestBytes, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    [IO.File]::WriteAllText((Join-Path $studioRelease 'studiofix-update.sig'), [Convert]::ToBase64String($studioSignature), (New-Object Text.UTF8Encoding($false)))
    Copy-Item -LiteralPath $studioExe -Destination (Join-Path $studioRelease 'StudioFix.exe') -Force
    foreach ($studioAsset in @('studio-fix-rules.json','studio-fix-rules.sig')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $studioAsset) -Destination (Join-Path $studioRelease $studioAsset) -Force }
    $studioChecksums = foreach ($studioAsset in @('StudioFix.exe','StudioFix-Setup.exe','studiofix-update.json','studiofix-update.sig','studio-fix-rules.json','studio-fix-rules.sig')) { (Get-FileHash -LiteralPath (Join-Path $studioRelease $studioAsset) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $studioAsset }
    [IO.File]::WriteAllText((Join-Path $studioRelease 'SHA256SUMS.txt'), ($studioChecksums -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
    Write-Output ('Release v' + $studioVersion + ': ' + $studioRelease)
}
finally { $studioSigner.Dispose() }
