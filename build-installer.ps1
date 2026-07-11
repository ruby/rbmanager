<#
.SYNOPSIS
  Build the rbmanager MSI (rb.exe installer).

.DESCRIPTION
  Publishes rb.exe with NativeAOT and builds src/rbmanager.Installer
  (WiX v5, SDK-style) into a per-user MSI under dist/. ICE validation
  runs as part of the build. Code signing is a separate, later step;
  the output is unsigned.

.EXAMPLE
  .\build-installer.ps1
  .\build-installer.ps1 -Version 0.1.0
#>
param(
  [string]$Version = '0.1.0',
  [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
  [string]$OutDir = (Join-Path $PSScriptRoot 'dist'),
  [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
  throw "Version must be numeric x.y.z for MSI ProductVersion, got '$Version'"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnet) { $dotnet.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }

$publishDir = Join-Path $PSScriptRoot 'artifacts\publish\rbmanager'
$rbExe = Join-Path $publishDir 'rb.exe'

Push-Location $PSScriptRoot
try {
  if (-not $SkipPublish) {
    Write-Host "Publishing rb.exe ($Arch) ..."
    & $dotnet publish src\rbmanager -r "win-$Arch" -c Release -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
  }
  if (-not (Test-Path $rbExe)) { throw "$rbExe not found" }

  Write-Host "Building the MSI ..."
  & $dotnet build src\rbmanager.Installer -c Release `
    "-p:ProductVersion=$Version" "-p:InstallerPlatform=$Arch" "-p:RbExe=$rbExe"
  if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

  New-Item -ItemType Directory -Force $OutDir | Out-Null
  $msi = Join-Path $OutDir "rbmanager-$Version-$Arch.msi"
  Copy-Item (Join-Path $PSScriptRoot "artifacts\bin\rbmanager.Installer\Release\rbmanager-$Version-$Arch.msi") `
    $msi -Force
} finally {
  Pop-Location
}

Write-Host "Done: $msi"
