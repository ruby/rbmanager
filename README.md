# windows-installer

Distribution tooling for the official Ruby mswin binary packages.

The input is always the relocatable zip produced by the nmake-only
`binary-package` target in ruby/ruby (win32/Makefile.sub plus
tool/binary-package.rb): `ruby-X.Y.Z-<arch>-mswinNN_MMM.zip`, a single
root directory holding `bin/`, `lib/`, `include/`, `share/`, and
`LICENSES/`, built with `LOAD_RELATIVE` so the tree runs from any
location. The contract between ruby/ruby and this repository is nothing
more than that zip layout.

## rbmanager

`rbmanager` is a small version manager in the spirit of Python's install
manager (PEP 773): it fetches a binary-package zip, extracts it under
`%LOCALAPPDATA%\rbmanager\rubies\`, and makes it available on PATH. No
elevation is required at any point.

```
rbmanager setup               copy rbmanager itself onto PATH
rbmanager install <zip|url>   install a ruby binary package
rbmanager list                list installed rubies
rbmanager use <version>       switch the active ruby
rbmanager uninstall <version> remove an installed ruby
```

rbmanager is a bare exe; `setup` copies it to
`%LOCALAPPDATA%\rbmanager\bin` and puts that directory on the user PATH,
which stands in for an installer until a winget manifest exists.

The active ruby is exposed through an NTFS directory junction
`%LOCALAPPDATA%\rbmanager\current`, and `install` appends
`%LOCALAPPDATA%\rbmanager\current\bin` to the user PATH once; switching
versions only re-points the junction. Junctions rather than symbolic
links because they need no privilege and no Developer Mode.

Build (requires the .NET 8 SDK and MSVC link.exe):

```
dotnet publish rbmanager -r win-x64 -c Release -o rbmanager\publish
```

This produces a self-contained NativeAOT `rbmanager.exe` (~5 MB) with no
runtime dependency.

## CA trust bootstrap

The vcpkg-built OpenSSL in the binary packages has no usable trust
anchors on end-user machines (its baked OPENSSLDIR does not exist
there). Until ruby/openssl can read the Windows certificate store
natively through OpenSSL's winstore loader,
`overlay/site_ruby/rubygems/defaults/operating_system.rb` exports the
Windows ROOT store to a weekly-refreshed PEM cache under
`%LOCALAPPDATA%\ruby-mswin` and sets `SSL_CERT_FILE` for the current
process only, deferring trust management to Windows Update instead of
shipping a CA bundle. rbmanager embeds this hook and injects it into
every runtime it extracts; the suspended MSI build applies the same file
as a staging overlay, so both channels behave identically. The hook does
nothing when `SSL_CERT_FILE` or `SSL_CERT_DIR` is already set, and its
failure modes all degrade to the previous behavior.

## MSI build (suspended)

An earlier iteration packaged each Ruby version as a WiX v5 MSI. That
direction is suspended in favor of rbmanager, but the sources are kept
because they are verified working and remain the right answer if
enterprise (GPO/Intune) deployment ever needs one: `src/ruby.wxs`,
`build.ps1`, and [docs/upgrade-code.md](docs/upgrade-code.md) for the
UpgradeCode allocation scheme. WiX 5.0.2 is pinned as a dotnet local
tool in `.config/dotnet-tools.json`. See the git history for the
verification record (ICE validation, perMachine and perUser
install/uninstall round-trips).

## Layout

- `rbmanager/` — the version manager (C#, NativeAOT)
- `overlay/` — files layered onto every installed runtime
- `src/ruby.wxs`, `build.ps1` — suspended MSI build
- `docs/` — design notes
- `spike/` — the original WiX spike materials and `query.ps1` for
  dumping MSI tables via the WindowsInstaller COM API
