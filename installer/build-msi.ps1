[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$PublishDirectory = 'artifacts/publish',
    [string]$OutputDirectory = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'MSI requires a three-part numeric version.' }
$numericVersion = [version]$Version
if ($numericVersion.Major -gt 255 -or $numericVersion.Minor -gt 255 -or $numericVersion.Build -gt 65535) {
    throw 'Version exceeds Windows Installer limits (255.255.65535).'
}
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $publish 'CanAIRy.exe'))) { throw 'Publish CanAIRy first.' }
$wix = Join-Path $repositoryRoot 'artifacts/tools/wix/wix.exe'
if (-not (Test-Path -LiteralPath $wix)) {
    dotnet tool install wix --version 5.0.2 --tool-path (Split-Path $wix) --configfile (Join-Path $repositoryRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'WiX installation failed.' }
}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$output = Join-Path (Resolve-Path $OutputDirectory).Path "CanAIRy-$Version-win-x64.msi"
& $wix build -arch x64 -pdbtype none -d "ProductVersion=$Version" -d "PublishDir=$publish" `
    -d "SourceIcon=$repositoryRoot/app/CanAIRy/Assets/CanAIRy.ico" `
    -d "MarkerFile=$PSScriptRoot/install-kind-msi.txt" `
    -o $output (Join-Path $PSScriptRoot 'CanAIRy.wxs')
if ($LASTEXITCODE -ne 0) { throw 'MSI compilation failed.' }
Get-Item -LiteralPath $output
