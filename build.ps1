param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'), [switch]$Tests)
$ErrorActionPreference = 'Stop'
$studioFramework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$studioWpf = Join-Path $studioFramework 'WPF'
$studioDotnet = (Get-Command dotnet -ErrorAction Stop).Source
$studioSdkBase = Join-Path ([IO.Path]::GetDirectoryName($studioDotnet)) 'sdk'
$studioSdk = Get-ChildItem -LiteralPath $studioSdkBase -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Roslyn\bincore\csc.dll') } | Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending | Select-Object -First 1
if (-not $studioSdk) { throw 'A .NET SDK with the C# compiler is required to build the source.' }
$studioCompiler = Join-Path $studioSdk.FullName 'Roslyn\bincore\csc.dll'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$studioOutput = [IO.Path]::GetFullPath($OutputDirectory)
$studioSignature = Join-Path $studioOutput 'embedded-signature.bin'
[IO.File]::WriteAllBytes($studioSignature, [Convert]::FromBase64String([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'publisher\studio-fix-rules.sig')).Trim()))
$studioArguments = @('/nologo', '/noconfig', '/nostdlib+', '/optimize+', '/langversion:latest', '/platform:x64', '/target:winexe', ('/out:' + (Join-Path $studioOutput 'StudioFix.exe')), ('/win32manifest:' + (Join-Path $PSScriptRoot 'src\app.manifest')), ('/win32icon:' + (Join-Path $PSScriptRoot 'src\StudioFix.ico')))
foreach ($studioAssembly in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Web.Extensions.dll', 'System.Net.Http.dll', 'System.Xaml.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'Microsoft.CSharp.dll')) { $studioArguments += '/reference:' + (Join-Path $studioFramework $studioAssembly) }
foreach ($studioAssembly in @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll')) { $studioArguments += '/reference:' + (Join-Path $studioWpf $studioAssembly) }
$studioArguments += '/resource:' + (Join-Path $PSScriptRoot 'publisher\public-key.xml') + ',StudioFix.public-key.xml'
$studioArguments += '/resource:' + (Join-Path $PSScriptRoot 'publisher\studio-fix-rules.json') + ',StudioFix.rules.json'
$studioArguments += '/resource:' + $studioSignature + ',StudioFix.rules.sig'
$studioArguments += '/resource:' + (Join-Path $PSScriptRoot 'src\MainWindow.xaml') + ',StudioFix.MainWindow.xaml'
$studioArguments += '/resource:' + (Join-Path $PSScriptRoot 'src\Icons.xaml') + ',StudioFix.Icons.xaml'
$studioArguments += '/resource:' + (Join-Path $PSScriptRoot 'publisher\LUCIDE-LICENSE.txt') + ',StudioFix.LucideLicense.txt'
$studioArguments += '/resource:' + (Join-Path $PSScriptRoot 'src\StudioFix.ico') + ',StudioFix.icon.ico'
$studioSourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object FullName)
& $studioDotnet $studioCompiler @studioArguments @studioSourceFiles
if ($LASTEXITCODE -ne 0) { throw 'StudioFix compilation failed.' }
if ($Tests) {
    $studioTestArguments = @($studioArguments | Where-Object { $_ -notmatch '^/out:|^/target:|^/win32manifest:|^/win32icon:' })
    $studioTestArguments += '/target:exe'
    $studioTestArguments += '/main:StudioFix.Tests.TestProgram'
    $studioTestArguments += '/out:' + (Join-Path $studioOutput 'StudioFix.Tests.exe')
    $studioTestSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter '*.cs' -File | Where-Object Name -ne 'FixtureStudio.cs' | ForEach-Object FullName)
    & $studioDotnet $studioCompiler @studioTestArguments @studioSourceFiles @studioTestSources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $studioDotnet $studioCompiler /nologo /nostdlib+ /target:winexe ('/reference:' + (Join-Path $studioFramework 'mscorlib.dll')) ('/reference:' + (Join-Path $studioFramework 'System.dll')) ('/out:' + (Join-Path $studioOutput 'FixtureStudio.exe')) (Join-Path $PSScriptRoot 'tests\FixtureStudio.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
}
Write-Output (Join-Path $studioOutput 'StudioFix.exe')
