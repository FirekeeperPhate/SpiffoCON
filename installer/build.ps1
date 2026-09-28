# Builds the SpiffoCON installers (Inno Setup).
#   .\build.ps1                -> Light and Full
#   .\build.ps1 -Flavor Full   -> Full only
#   .\build.ps1 -SkipTests     -> skip the unit tests
# Light = framework-dependent (needs the .NET 10 Desktop Runtime), Full = self-contained (runtime included).
# Output: installer\Output\SpiffoCON-Setup-<version>-<Light|Full>.exe

param(
    [ValidateSet('Light', 'Full', 'All')]
    [string]$Flavor = 'All',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\SpiffoCON\SpiffoCON.csproj'

$iscc = @(
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'ISCC.exe (Inno Setup) not found.' }

$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version not found in SpiffoCON.csproj.' }

if (-not $SkipTests) {
    Write-Host '== Tests' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'tests\SpiffoCON.Core.Tests') -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$flavors = if ($Flavor -eq 'All') { @('Light', 'Full') } else { @($Flavor) }

foreach ($f in $flavors) {
    $out = Join-Path $root ('publish\' + $f.ToLower())
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }   # no leftovers from previous builds
    $selfContained = if ($f -eq 'Full') { 'true' } else { 'false' }

    Write-Host "== Publish $f $version (self-contained: $selfContained)" -ForegroundColor Cyan
    dotnet publish $project -c Release -r win-x64 --self-contained $selfContained -o $out -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($f) failed." }
    if (-not (Test-Path (Join-Path $out 'bridge\SpiffoCONBridge\workshop.txt'))) { throw 'The bridge mod is missing from the publish folder.' }

    Write-Host "== Installer $f" -ForegroundColor Cyan
    & $iscc /Q "/DFlavor=$f" "/DAppVersion=$version" (Join-Path $PSScriptRoot 'SpiffoCON.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC ($f) failed." }
}

Get-ChildItem (Join-Path $PSScriptRoot 'Output') -Filter "SpiffoCON-Setup-$version-*.exe" |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
