param($msi)
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($msi, 0)
function Q($sql){
  $view = $db.OpenView($sql); $view.Execute()
  while($true){
    $rec = $view.Fetch(); if($null -eq $rec){break}
    $n = $rec.FieldCount; $f=@()
    for($i=1;$i -le $n;$i++){ $f += ,([string]$rec.StringData($i)) }
    ($f -join "  |  ")
  }
}
"=== Environment (PATH mutation) ==="; Q "SELECT Name, Value FROM Environment"
"=== Upgrade (MajorUpgrade) ==="; Q "SELECT UpgradeCode, VersionMin, VersionMax, Attributes, ActionProperty FROM Upgrade"
"=== Property (scope/identity) ==="; Q "SELECT Property, Value FROM Property"
