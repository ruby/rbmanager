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

UpgradeCodes are never allocated by hand. build-installer.ps1 derives
them as UUIDv5 (RFC 4122, SHA-1) from a fixed namespace GUID and a name
string:

    namespace: E2C11F7E-A84E-4363-A0EB-D0A93E07E3CF
    name:      ruby-windows-installer:upgrade-code:rbmanager

Anyone can reproduce the value without access to a shared registry, and
two machines can never allocate diverging codes. Both the namespace
GUID and the name format are frozen forever; the name prefix predates
the repository rename and is frozen with the rest. Changing either
would silently break in-place upgrades, because new packages would stop
finding the installed ones.

Derived values for reference (reproducible with `New-UuidV5` in
build-installer.ps1):

| name | UpgradeCode |
|------|-------------|
| ruby-windows-installer:upgrade-code:rbmanager | CAB95EEE-B5A6-52EA-94B3-B4413CE23855 |

The PATH environment component needs a stable GUID for the same reason:

    name: ruby-windows-installer:path-component:rbmanager:perUser

The retired per-version Ruby MSI used the same scheme with one product
line per (series, arch) pair; its name formats and derived codes are in
the git history.

## ProductVersion constraints

MSI ProductVersion must be numeric `major.minor.build` with major and
minor below 256 and build below 65536, and upgrade comparison only
reads those three fields. build-installer.ps1 rejects anything that is
not numeric x.y.z.
