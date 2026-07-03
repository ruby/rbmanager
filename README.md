# windows-installer

MSI packaging for the official Ruby mswin binary distribution.

This repository is the second half of a two-layer design. The first layer
lives in ruby/ruby: the nmake-only `binary-package` target
(win32/Makefile.sub plus tool/binary-package.rb) builds Ruby with the
Microsoft toolchain and produces a relocatable zip. This repository takes
that zip as its only input and turns it into an MSI with WiX v5. The
contract between the two layers is nothing more than the zip layout.

## Input contract

The input is `ruby-X.Y.Z-<arch>-mswinNN_MMM.zip` (e.g.
`ruby-4.1.0-x64-mswin64_140.zip`). It contains a single root directory of
the same name, holding `bin/` (ruby.exe, vcpkg dependency DLLs, vcruntime
DLLs, RubyGems command stubs), `lib/`, `include/`, `share/`, and
`LICENSES/`. The binaries are built with `LOAD_RELATIVE`, so the tree runs
from any location; the MSI decides where that location is.

## Building

Requires the .NET 8 SDK. WiX 5.0.2 is pinned as a dotnet local tool in
`.config/dotnet-tools.json` and restored automatically.

```powershell
.\build.ps1 -Zip path\to\ruby-4.1.0-x64-mswin64_140.zip
```

This produces `dist/ruby-4.1.0-x64-mswin.msi`, a per-machine package that
installs under `Program Files\Ruby-4.1-x64-mswin` and appends its `bin`
directory to the system PATH. Pass `-Scope perUser` for a per-user package
(installs under `%LOCALAPPDATA%\Programs`, user PATH, no elevation
required) and `-Validate` to run `wix msi validate` (ICE checks) on the
result.

## Overlay: CA trust bootstrap

The MSI is a repackaging of the zip plus one overlay file:
`overlay/site_ruby/rubygems/defaults/operating_system.rb`, copied into the
version-independent `lib/ruby/site_ruby` during the build. The vcpkg-built
OpenSSL has no usable trust anchors on end-user machines (its baked
OPENSSLDIR does not exist there), so this hook exports the Windows ROOT
certificate store to a weekly-refreshed PEM cache under
`%LOCALAPPDATA%\ruby-mswin` and sets `SSL_CERT_FILE` for the current
process only, deferring trust management to Windows Update instead of
shipping a CA bundle. The export runs through a one-shot powershell.exe
child using the .NET X509Store API. This is an interim measure until
ruby/openssl can read the Windows store natively through OpenSSL's
winstore loader; the hook does nothing when `SSL_CERT_FILE` or
`SSL_CERT_DIR` is already set, and its failure modes all degrade to the
previous behavior.

## Identity and upgrades

Each Ruby X.Y series and architecture pair is a distinct MSI product line:
different series install side by side, while installs within a series
upgrade in place via `MajorUpgrade`. The UpgradeCode for each line is
derived deterministically (UUIDv5) rather than allocated by hand; see
[docs/upgrade-code.md](docs/upgrade-code.md).

## Layout

- `src/ruby.wxs` — the WiX authoring, parameterized entirely through
  preprocessor variables supplied by build.ps1
- `build.ps1` — zip extraction, identity derivation, `wix build`
- `spike/` — the original minimal spike that validated glob harvesting,
  PATH mutation, MajorUpgrade, and per-machine scope, plus `query.ps1`
  for dumping MSI tables via the WindowsInstaller COM API
