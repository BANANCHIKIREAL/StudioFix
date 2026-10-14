param(
    [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
    [string]$CatalogPath = (Join-Path $PSScriptRoot 'studio-fix-rules.json')
)
$ErrorActionPreference = 'Stop'
$studioCatalogBytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($CatalogPath))
$studioCatalog = [Text.Encoding]::UTF8.GetString($studioCatalogBytes) | ConvertFrom-Json
if ($studioCatalog.Schema -ne 1 -or $studioCatalog.CatalogVersion -lt 1 -or -not $studioCatalog.Builds) { throw 'Invalid rule catalog.' }
$studioSigner = New-Object Security.Cryptography.RSACryptoServiceProvider
$studioSigner.PersistKeyInCsp = $false
try {
    $studioSigner.FromXmlString([IO.File]::ReadAllText([IO.Path]::GetFullPath($PrivateKeyPath)))
    if ($studioSigner.PublicOnly) { throw 'The selected XML contains only the public key.' }
    $studioExpectedPublic = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'public-key.xml')).Trim()
    if ($studioSigner.ToXmlString($false) -ne $studioExpectedPublic) { throw 'The signing key does not belong to this StudioFix build.' }
    $studioSignatureBytes = $studioSigner.SignData($studioCatalogBytes, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    $studioSignaturePath = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($CatalogPath))) 'studio-fix-rules.sig'
    [IO.File]::WriteAllText($studioSignaturePath, [Convert]::ToBase64String($studioSignatureBytes), (New-Object Text.UTF8Encoding($false)))
    Write-Output ('Signed catalog version ' + $studioCatalog.CatalogVersion)
    Write-Output $studioSignaturePath
}
finally { $studioSigner.Dispose() }
