param(
    [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
    [Parameter(Mandatory = $true)][string]$CompilerPath,
    [Parameter(Mandatory = $true)][string]$NotesFile,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\build\release'),
    [string]$Summary = 'Обновление StudioFix. Настройки и резервные копии сохраняются.'
)
$ErrorActionPreference = 'Stop'
$studioProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$studioOutput = [IO.Path]::GetFullPath($OutputDirectory)
$studioNotes = [IO.Path]::GetFullPath($NotesFile)
if (-not (Test-Path -LiteralPath $studioNotes)) { throw 'Release notes file not found.' }
Get-Command gh -ErrorAction Stop | Out-Null
Push-Location $studioProject
try {
    $studioChanges = & git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $studioChanges) { throw 'Commit and push source changes before publishing.' }
    $studioHead = & git rev-parse HEAD
    $studioRemoteHead = & git rev-parse origin/main
    if ($studioHead -ne $studioRemoteHead) { throw 'The current source must match origin/main.' }
    & (Join-Path $PSScriptRoot 'Build-Release.ps1') -PrivateKeyPath $PrivateKeyPath -CompilerPath $CompilerPath -OutputDirectory $studioOutput -Notes $Summary
    $studioManifest = Get-Content -LiteralPath (Join-Path $studioOutput 'studiofix-update.json') -Raw | ConvertFrom-Json
    $studioTag = $studioManifest.Tag
    & gh release create $studioTag --repo BANANCHIKIREAL/StudioFix --target $studioHead --draft --title ('StudioFix ' + $studioManifest.Version) --notes-file $studioNotes
    if ($LASTEXITCODE -ne 0) { throw 'Draft release creation failed.' }
    $studioAssets = @('StudioFix-Setup.exe','StudioFix.exe','studiofix-update.json','studiofix-update.sig','studio-fix-rules.json','studio-fix-rules.sig','SHA256SUMS.txt') | ForEach-Object { Join-Path $studioOutput $_ }
    & gh release upload $studioTag @studioAssets --repo BANANCHIKIREAL/StudioFix
    if ($LASTEXITCODE -ne 0) { throw 'Asset upload failed. The release remains a draft.' }
    & gh release edit $studioTag --repo BANANCHIKIREAL/StudioFix --draft=false --latest
    if ($LASTEXITCODE -ne 0) { throw 'Release publication failed.' }
    Write-Output 'https://github.com/BANANCHIKIREAL/StudioFix/releases/latest/download/StudioFix-Setup.exe'
}
finally { Pop-Location }
