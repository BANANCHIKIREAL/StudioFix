param(
    [Parameter(Mandatory = $true)][string]$StudioPath,
    [Parameter(Mandatory = $true)][string]$LauncherPath,
    [string]$CatalogPath = (Join-Path $PSScriptRoot 'studio-fix-rules.json')
)
$ErrorActionPreference = 'Stop'
$studioExportDirectory = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($CatalogPath))) ('export-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $studioExportDirectory | Out-Null
$studioExportPath = Join-Path $studioExportDirectory 'build-rule.json'
function Quote-StudioPublisherArg([string]$Text) {
    $studioQuoted = New-Object Text.StringBuilder
    [void]$studioQuoted.Append('"')
    $studioSlashCount = 0
    foreach ($studioChar in $Text.ToCharArray()) {
        if ($studioChar -eq '\') { $studioSlashCount++; continue }
        if ($studioChar -eq '"') { [void]$studioQuoted.Append('\', $studioSlashCount * 2 + 1) }
        else { [void]$studioQuoted.Append('\', $studioSlashCount) }
        [void]$studioQuoted.Append($studioChar)
        $studioSlashCount = 0
    }
    [void]$studioQuoted.Append('\', $studioSlashCount * 2)
    [void]$studioQuoted.Append('"')
    return $studioQuoted.ToString()
}
$studioExportArguments = '--export-rule --studio ' + (Quote-StudioPublisherArg ([IO.Path]::GetFullPath($StudioPath))) + ' --data-root ' + (Quote-StudioPublisherArg (Join-Path $studioExportDirectory 'state')) + ' --output ' + (Quote-StudioPublisherArg $studioExportPath)
$studioExportProcess = Start-Process -FilePath ([IO.Path]::GetFullPath($LauncherPath)) -ArgumentList $studioExportArguments -Wait -PassThru -WindowStyle Hidden
if ($studioExportProcess.ExitCode -ne 0) { throw ('Rule export failed. Inspect ' + $studioExportPath) }
$studioBuild = [IO.File]::ReadAllText($studioExportPath) | ConvertFrom-Json
if (-not $studioBuild.Needle -or -not $studioBuild.Replacement -or $studioBuild.OriginalHash -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Exported data does not contain a recognized patch.' }
$studioCatalog = [IO.File]::ReadAllText([IO.Path]::GetFullPath($CatalogPath)) | ConvertFrom-Json
$studioCatalog.Builds = @($studioCatalog.Builds | Where-Object { -not ($_.Version -eq $studioBuild.Version -and $_.OriginalHash -eq $studioBuild.OriginalHash) }) + @($studioBuild)
$studioCatalog.CatalogVersion = [int]$studioCatalog.CatalogVersion + 1
$studioCatalog.Published = (Get-Date).ToString('yyyy-MM-dd')
$studioCatalogBackup = $CatalogPath + '.' + [guid]::NewGuid().ToString('N') + '.backup'
Copy-Item -LiteralPath $CatalogPath -Destination $studioCatalogBackup
[IO.File]::WriteAllText([IO.Path]::GetFullPath($CatalogPath), ($studioCatalog | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
Write-Output ('Added Studio ' + $studioBuild.Version + '. Catalog version: ' + $studioCatalog.CatalogVersion)
Write-Output 'Validate an actual LIVE session on the new build before signing and publishing this catalog.'
