# UpgradeCode allocation

## Product identity model

rbmanager is one MSI product line with one fixed UpgradeCode.
`MajorUpgrade` removes the previous install and blocks downgrades. The
architecture is deliberately not part of the identity: an arm64 package
must upgrade an x64 install in place rather than sit alongside it,
because both own the same `%LOCALAPPDATA%\Ruby\bin`.

The ProductCode is regenerated on every build (WiX default when
`Package/@ProductCode` is not set), which is what `MajorUpgrade`
requires.

## Derivation

UpgradeCodes are never allocated by hand. They are derived as UUIDv5
(RFC 4122, SHA-1) from a fixed namespace GUID and a name string, and
baked into `src/rbmanager.Installer/Package.wxs` as constants:

    namespace: E2C11F7E-A84E-4363-A0EB-D0A93E07E3CF
    name:      ruby-windows-installer:upgrade-code:rbmanager

Anyone can reproduce the value without access to a shared registry, and
two machines can never allocate diverging codes. Both the namespace
GUID and the name format are frozen forever; the name prefix predates
the repository rename and is frozen with the rest. Changing either
would silently break in-place upgrades, because new packages would stop
finding the installed ones.

Derived values for reference:

| name | GUID |
|------|------|
| ruby-windows-installer:upgrade-code:rbmanager | CAB95EEE-B5A6-52EA-94B3-B4413CE23855 |
| ruby-windows-installer:path-component:rbmanager:perUser | F786646B-2533-5C48-AD8F-9CCDD8AE4D16 |

The second entry is the PATH environment component, which needs a
stable GUID for the same reason. Both are reproducible with:

```powershell
function New-UuidV5([guid]$Namespace, [string]$Name) {
  $ns = $Namespace.ToByteArray()
  # GUID byte layout is little-endian in the first three fields; RFC 4122
  # hashing requires network byte order.
  [Array]::Reverse($ns, 0, 4); [Array]::Reverse($ns, 4, 2); [Array]::Reverse($ns, 6, 2)
  $hash = [System.Security.Cryptography.SHA1]::HashData(
    $ns + [System.Text.Encoding]::UTF8.GetBytes($Name))
  $b = $hash[0..15]
  $b[6] = ($b[6] -band 0x0F) -bor 0x50   # version 5
  $b[8] = ($b[8] -band 0x3F) -bor 0x80   # RFC 4122 variant
  [Array]::Reverse($b, 0, 4); [Array]::Reverse($b, 4, 2); [Array]::Reverse($b, 6, 2)
  [guid][byte[]]$b
}
New-UuidV5 'E2C11F7E-A84E-4363-A0EB-D0A93E07E3CF' `
  'ruby-windows-installer:upgrade-code:rbmanager'
```

The retired per-version Ruby MSI used the same scheme with one product
line per (series, arch) pair; its name formats and derived codes are in
the git history.

## ProductVersion constraints

MSI ProductVersion must be numeric `major.minor.build` with major and
minor below 256 and build below 65536, and upgrade comparison only
reads those three fields. build-installer.ps1 rejects anything that is
not numeric x.y.z.
