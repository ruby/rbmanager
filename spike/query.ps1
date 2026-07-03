<#
  Dump interesting MSI tables for inspection.

  Record.StringData is a parameterized property that PowerShell cannot
  invoke through the WindowsInstaller COM interop, so go through
  Database.Export, which writes each table as a tab-separated .idt file.
#>
param(
  [Parameter(Mandatory)][string]$Msi,
  [string[]]$Tables = @('Environment', 'Upgrade', 'Property')
)
$ErrorActionPreference = 'Stop'

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase((Resolve-Path $Msi).Path, 0)
$tmp = Join-Path $env:TEMP "msi-idt-$PID"
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
  foreach ($t in $Tables) {
    "=== $t ==="
    $idt = "$t.idt"
    $db.Export($t, $tmp, $idt)
    # Rows 1-3 of an .idt are column names, column definitions, and the
    # table header; the data starts at line 4.
    Get-Content (Join-Path $tmp $idt) | Select-Object -Skip 3
    ""
  }
} finally {
  Remove-Item -Recurse -Force $tmp
}
