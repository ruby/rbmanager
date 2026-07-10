<#
.SYNOPSIS
  Build the rbmanager MSI (rb.exe installer).

.DESCRIPTION
  Publishes rb.exe with NativeAOT and compiles installer/rbmanager.wxs with
  WiX v5 into a per-user MSI under dist/. Code signing is a separate,
  later step; the output is unsigned.

.EXAMPLE
  .\build-installer.ps1
  .\build-installer.ps1 -Version 0.1.0 -Validate
#>
param(
  [string]$Version = '0.1.0',
  [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
  [string]$OutDir = (Join-Path $PSScriptRoot 'dist'),
  [switch]$Validate,
  [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
  throw "Version must be numeric x.y.z for MSI ProductVersion, got '$Version'"
}

# UUIDv5 derivation; see docs/upgrade-code.md. rbmanager is a single
# product line, so the arch is not part of its identity.
$NamespaceGuid = [guid]'E2C11F7E-A84E-4363-A0EB-D0A93E07E3CF'

function New-UuidV5([guid]$Namespace, [string]$Name) {
  $ns = $Namespace.ToByteArray()
  # GUID byte layout is little-endian in the first three fields; RFC 4122
  # hashing requires network byte order.
  [Array]::Reverse($ns, 0, 4); [Array]::Reverse($ns, 4, 2); [Array]::Reverse($ns, 6, 2)
  $sha1 = [System.Security.Cryptography.SHA1]::Create()
  try {
    $hash = $sha1.ComputeHash($ns + [System.Text.Encoding]::UTF8.GetBytes($Name))
  } finally {
    $sha1.Dispose()
  }
  $b = $hash[0..15]
  $b[6] = ($b[6] -band 0x0F) -bor 0x50   # version 5
  $b[8] = ($b[8] -band 0x3F) -bor 0x80   # RFC 4122 variant
  [Array]::Reverse($b, 0, 4); [Array]::Reverse($b, 4, 2); [Array]::Reverse($b, 6, 2)
  [guid][byte[]]$b
}

$upgradeCode = New-UuidV5 $NamespaceGuid 'ruby-windows-installer:upgrade-code:rbmanager'
$pathGuid = New-UuidV5 $NamespaceGuid 'ruby-windows-installer:path-component:rbmanager:perUser'

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

  & $dotnet tool restore | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

  New-Item -ItemType Directory -Force $OutDir | Out-Null
  $msi = Join-Path $OutDir "rbmanager-$Version-$Arch.msi"

  Write-Host "Building $msi ..."
  & $dotnet wix build (Join-Path $PSScriptRoot 'installer\rbmanager.wxs') `
    -arch $Arch `
    -d "Version=$Version" `
    -d "UpgradeCode=$upgradeCode" `
    -d "PathComponentGuid=$pathGuid" `
    -d "RbExe=$rbExe" `
    -o $msi
  if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

  if ($Validate) {
    Write-Host "Validating $msi ..."
    # Per-user packages installing into the user profile trip ICE38/ICE64/
    # ICE91 by design; those rules target per-machine packages.
    & $dotnet wix msi validate '-sice' 'ICE38' '-sice' 'ICE64' '-sice' 'ICE91' $msi
    if ($LASTEXITCODE -ne 0) { throw "wix msi validate failed" }
  }
} finally {
  Pop-Location
}

Write-Host "Done: $msi"
