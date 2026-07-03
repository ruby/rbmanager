# UpgradeCode allocation

## Product identity model

Each (Ruby X.Y series, architecture) pair is one MSI product line with one
fixed UpgradeCode. Different series install side by side, so Ruby 4.1 and
Ruby 4.2 can coexist on the same machine. Within a series, `MajorUpgrade`
removes the previous install: 4.1.0 to 4.1.1 upgrades in place, and
downgrades are blocked.

The Visual Studio toolchain suffix in the platform string (`mswin64_140`)
is deliberately not part of the identity. A future toolchain bump within a
series (say `mswin64_140` to `mswin64_150`) must still upgrade in place,
not install alongside.

The ProductCode is regenerated on every build (WiX default when
`Package/@ProductCode` is not set), which is what `MajorUpgrade` requires.

## Derivation

UpgradeCodes are never allocated by hand. build.ps1 derives them as UUIDv5
(RFC 4122, SHA-1) from a fixed namespace GUID and a name string:

    namespace: E2C11F7E-A84E-4363-A0EB-D0A93E07E3CF
    name:      ruby-windows-installer:upgrade-code:<series>:<arch>

Anyone can reproduce the value for any series without access to a shared
registry, and two machines can never allocate diverging codes for the same
series. Both the namespace GUID and the name format are frozen forever.
Changing either would silently break in-place upgrades for every published
series, because new packages would stop finding the installed ones.

Derived values for reference (reproducible with `New-UuidV5` in build.ps1):

| name | UpgradeCode |
|------|-------------|
| ruby-windows-installer:upgrade-code:4.1:x64 | D6CD8785-36FA-5FFC-9367-26DB090DA838 |
| ruby-windows-installer:upgrade-code:4.1:arm64 | F4B8F180-42D3-5386-98D5-4F80BAE66099 |
| ruby-windows-installer:upgrade-code:rbmanager | CAB95EEE-B5A6-52EA-94B3-B4413CE23855 |

rbmanager itself (built by build-installer.ps1) is one product line with
no arch in its identity: an arm64 package must upgrade an x64 install in
place rather than sit alongside it, because both own the same
%LOCALAPPDATA%\Ruby\bin. The name prefix predates the repository rename
and is frozen with the rest of the format. Its PATH component is

    name: ruby-windows-installer:path-component:rbmanager:perUser

The PATH environment component needs a stable GUID per product line for
the same reason, with the install scope added because per-machine and
per-user variants register different keypaths:

    name: ruby-windows-installer:path-component:<series>:<arch>:<scope>

## Scope

The per-machine and per-user packages of a product line share the
UpgradeCode. Windows Installer evaluates FindRelatedProducts within the
install context, so a per-user install never upgrades or blocks a
per-machine one and vice versa. Sharing the code keeps "same product"
true in both worlds without extra bookkeeping.

## ProductVersion constraints

MSI ProductVersion must be numeric `major.minor.build` with major and
minor below 256 and build below 65536, and upgrade comparison only reads
those three fields. Released Ruby versions (for example 4.1.0) fit as-is.
Preview and rc versions (4.2.0-preview1) are not representable, and
build.ps1 rejects them at the zip-name check. If preview MSIs are ever
wanted, they need a separate decision: either map the prerelease sequence
into the build field of a pre-release version number (for example
4.2.0-preview1 as ProductVersion 4.1.9001 style mapping) or give previews
their own product line. Do not reuse the release UpgradeCode with a
fake-higher version, because that would block the real release install.
