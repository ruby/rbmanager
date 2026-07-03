<#
.SYNOPSIS
  Build a Ruby MSI from a relocatable binary-package zip.

.DESCRIPTION
  Takes the zip produced by `nmake binary-package` in ruby/ruby
  (e.g. ruby-4.1.0-x64-mswin64_140.zip), extracts it, and compiles
  src/ruby.wxs with WiX v5 into an MSI under dist/.

.EXAMPLE
  .\build.ps1 -Zip V:\path\to\ruby-4.1.0-x64-mswin64_140.zip
  .\build.ps1 -Zip ... -Scope perUser -Validate
#>
param(
  [Parameter(Mandatory)][string]$Zip,
  [ValidateSet('perMachine', 'perUser')][string]$Scope = 'perMachine',
  [string]$OutDir = (Join-Path $PSScriptRoot 'dist'),
  [switch]$Validate
)

$ErrorActionPreference = 'Stop'

# UUIDv5 (RFC 4122, SHA-1) so that UpgradeCode and friends are derived
# deterministically from (series, arch) instead of being allocated by hand.
# The namespace GUID is fixed forever; see docs/upgrade-code.md.
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

# --- Parse identity out of the zip name ------------------------------------
$leaf = Split-Path $Zip -Leaf
if ($leaf -notmatch '^ruby-(?<series>\d+\.\d+)\.(?<patch>\d+)-(?<arch>x64|arm64)-mswin[\d_]+\.zip$') {
  throw "Unrecognized zip name '$leaf'. Expected ruby-X.Y.Z-<arch>-mswinNN_MMM.zip (numeric version only; map preview/rc versions before packaging)."
}
$series = $Matches.series
$version = "$($Matches.series).$($Matches.patch)"
$arch = $Matches.arch

$upgradeCode = New-UuidV5 $NamespaceGuid "ruby-windows-installer:upgrade-code:${series}:${arch}"
$pathGuid = New-UuidV5 $NamespaceGuid "ruby-windows-installer:path-component:${series}:${arch}:${Scope}"
$installDirName = "Ruby-$series-$arch-mswin"
$envSystem = if ($Scope -eq 'perMachine') { 'yes' } else { 'no' }

# --- Extract the zip into the staging area ---------------------------------
$buildDir = Join-Path $PSScriptRoot '_build'
$extractDir = Join-Path $buildDir 'staging'
if (Test-Path $extractDir) { Remove-Item -Recurse -Force $extractDir }
New-Item -ItemType Directory -Force $buildDir | Out-Null

Write-Host "Extracting $leaf ..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $Zip).Path, $extractDir)

# The zip carries a single root folder (ruby-X.Y.Z-<arch>-mswinNN_MMM/);
# harvest below it so INSTALLFOLDER maps to bin/, lib/, ...
$roots = @(Get-ChildItem $extractDir)
if ($roots.Count -ne 1 -or -not $roots[0].PSIsContainer) {
  throw "Expected a single root directory inside the zip, found: $($roots.Name -join ', ')"
}
$stagingDir = $roots[0].FullName

# --- Compile ----------------------------------------------------------------
New-Item -ItemType Directory -Force $OutDir | Out-Null
$suffix = if ($Scope -eq 'perUser') { '-perUser' } else { '' }
$msi = Join-Path $OutDir "ruby-$version-$arch-mswin$suffix.msi"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnet) { $dotnet.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }

Push-Location $PSScriptRoot
try {
  & $dotnet tool restore | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

  Write-Host "Building $msi ($Scope) ..."
  & $dotnet wix build (Join-Path $PSScriptRoot 'src\ruby.wxs') `
    -arch $arch `
    -d "RubyVersion=$version" `
    -d "RubySeries=$series" `
    -d "Arch=$arch" `
    -d "Scope=$Scope" `
    -d "UpgradeCode=$upgradeCode" `
    -d "PathComponentGuid=$pathGuid" `
    -d "EnvSystem=$envSystem" `
    -d "InstallDirName=$installDirName" `
    -d "StagingDir=$stagingDir" `
    -o $msi
  if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

  if ($Validate) {
    Write-Host "Validating $msi ..."
    # A per-user package that installs into the user profile inevitably
    # trips ICE38/ICE64/ICE91 (HKCU keypath, RemoveFile, per-user paths)
    # for every harvested file component; those rules target per-machine
    # packages and are noise here.
    $sice = if ($Scope -eq 'perUser') {
      @('-sice', 'ICE38', '-sice', 'ICE64', '-sice', 'ICE91')
    } else { @() }
    & $dotnet wix msi validate @sice $msi
    if ($LASTEXITCODE -ne 0) { throw "wix msi validate failed" }
  }
} finally {
  Pop-Location
}

Write-Host "Done: $msi"
