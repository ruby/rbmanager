# rbmanager

Distribution tooling for the official Ruby mswin binary packages.

The input is always the relocatable zip produced by the nmake-only
`binary-package` target in ruby/ruby (win32/Makefile.sub plus
tool/binary-package.rb): `ruby-X.Y.Z-<arch>-mswinNN_MMM.zip`, a single
root directory holding `bin/`, `lib/`, `include/`, `share/`, and
`LICENSES/`, built with `LOAD_RELATIVE` so the tree runs from any
location. The contract between ruby/ruby and this repository is nothing
more than that zip layout.

## rbmanager

rbmanager is a small version manager in the spirit of Python's install
manager (PEP 773): it fetches a binary-package zip, extracts it under
`%LOCALAPPDATA%\Ruby\rubies\`, and makes it available on PATH. No
elevation is required at any point. Both names mirror pymanager: the
command is `rb` as pymanager's is `py`, and the data directory is named
after the language (`%LOCALAPPDATA%\Ruby`, like `%LocalAppData%\Python`)
rather than after the tool. rbmanager remains the product name.

```
rb setup [--yes]           copy rb onto PATH and set up the VC++ runtime
rb install <zip|url>       install a ruby binary package
rb list                    list installed rubies
rb use <version>           switch the active ruby
rb uninstall <version>     remove an installed ruby
rb msvc enable [shell]     print the MSVC build env to eval (cmd|powershell)
rb msvc exec <command...>  run a command with the MSVC build env applied
```

rb is a bare exe; `setup` copies it to
`%LOCALAPPDATA%\Ruby\bin` and puts that directory on the user PATH,
which stands in for an installer until the winget and MSI channels
ship. It also checks for the VC++ 2015-2022 redistributable the
official mswin packages depend on, and offers to download and install
it (signature-verified, elevated); `--yes` skips the consent prompt.

`msvc enable` and `msvc exec` activate an installed Visual Studio (or
Build Tools) MSVC toolchain for building C extension gems; see
[docs/msvc-enable.md](docs/msvc-enable.md).

The active ruby is exposed through an NTFS directory junction
`%LOCALAPPDATA%\Ruby\current`, and `install` appends
`%LOCALAPPDATA%\Ruby\current\bin` to the user PATH once; switching
versions only re-points the junction. Junctions rather than symbolic
links because they need no privilege and no Developer Mode.

## Build and test

Regular development needs only the .NET 8 SDK (the feature band is
pinned by `global.json`):

```
dotnet build rbmanager.sln
dotnet test rbmanager.sln
```

Suites that need more than the SDK (Visual Studio, network access)
skip themselves when the environment lacks it; the category filters
for running them selectively are listed in
[docs/test-plan.md](docs/test-plan.md). CI
(`.github/workflows/ci.yml`) runs the same build and the full suite
on windows-latest, where VS is present.

Publishing the shipping rb.exe additionally requires MSVC link.exe:

```
dotnet publish src\rbmanager -r win-x64 -c Release -o artifacts\publish\rbmanager
```

This produces a self-contained NativeAOT `rb.exe` (~6 MB) with no
runtime dependency.

## rbmanager MSI

For channels that want a real installer instead of the bare exe,
`build-installer.ps1` wraps rb.exe in a per-user MSI
(`src/rbmanager.Installer/`, a WiX v5 SDK-style project):

```
.\build-installer.ps1
```

The MSI installs `rb.exe` into `%LOCALAPPDATA%\Ruby\bin` and puts that
directory on the user PATH, producing exactly the layout `rb setup`
creates, and removes both again on uninstall. rbmanager is a single MSI
product line; see [docs/upgrade-code.md](docs/upgrade-code.md). WiX
5.0.2 comes in through the `WixToolset.Sdk` NuGet package, and ICE
validation runs as part of the build. The output is unsigned; code
signing is tracked separately.

An earlier iteration packaged each Ruby version as its own MSI. That
direction was dropped in favor of rbmanager; see the git history for
the sources and the verification record.

## CA trust bootstrap

The vcpkg-built OpenSSL in the binary packages has no usable trust
anchors on end-user machines (its baked OPENSSLDIR does not exist
there). Until ruby/openssl can read the Windows certificate store
natively through OpenSSL's winstore loader,
`overlay/site_ruby/rubygems/defaults/operating_system.rb` exports the
Windows ROOT store to a weekly-refreshed PEM cache under
`%LOCALAPPDATA%\Ruby` and sets `SSL_CERT_FILE` for the current
process only, deferring trust management to Windows Update instead of
shipping a CA bundle. rbmanager embeds this hook and injects it into
every runtime it extracts. The hook does
nothing when `SSL_CERT_FILE` or `SSL_CERT_DIR` is already set, and its
failure modes all degrade to the previous behavior.

## Layout

- `src/rbmanager/` — the version manager (C#, NativeAOT)
- `tests/rbmanager.Tests/` — the xUnit test suite
- `overlay/` — files layered onto every installed runtime
- `src/rbmanager.Installer/`, `build-installer.ps1` — the rbmanager MSI
- `winget/` — draft winget manifests
- `docs/` — design notes
- `tools/query.ps1` — dump MSI tables via the WindowsInstaller COM API

Build output goes to `artifacts/` (`UseArtifactsOutput` in
`Directory.Build.props`), never into the project directories.
