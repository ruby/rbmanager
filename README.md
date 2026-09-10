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
elevation is required for rbmanager itself or for the per-user rubies;
see below for the one exception. Both names mirror pymanager: the
command is `rb` as pymanager's is `py`, and the data directory is named
after the language (`%LOCALAPPDATA%\Ruby`, like `%LocalAppData%\Python`)
rather than after the tool. rbmanager remains the product name.

```
rb setup [--yes]           copy rb onto PATH and set up the VC++ runtime
rb install <version|zip>   install a ruby binary package
rb list                    list installed rubies
rb use <version>           switch the active ruby
rb uninstall <version>     remove an installed ruby
rb msvc <command...>       run a command with the MSVC build env applied
rb msvc enable [shell]     print the MSVC build env to eval (cmd|powershell)
rb msvc --list             list installed Visual Studio C++ toolchains
rb version                 print the rbmanager version (also --version, -V)
```

rb is a bare exe; `setup` copies it to
`%LOCALAPPDATA%\Ruby\bin` and puts that directory on the user PATH,
which stands in for an installer until the winget and MSI channels
ship. It also checks for the VC++ 2015-2022 redistributable the
official mswin packages depend on, and offers to download and install
it (signature-verified, elevated); `--yes` skips the consent prompt.

`install` takes a version or a tag and resolves it through the binary
index published at
<https://cache.ruby-lang.org/pub/ruby/binaries/index.json> (regenerated
by ruby/actions after every package publish). `rb install 4.0.5` picks
that release, `rb install 4.0` the newest release of the series, and
`rb install ruby-dev` the newest master snapshot. A reissued release
resolves to its newest revision, and the superseded packages stay
reachable by their revisioned names such as `4.0.5-0`. The download is
verified against the sha256 recorded in the index. An unsigned build
(all dev snapshots are unsigned) installs with a warning. A zip path or
URL skips the index and installs directly.

`msvc` activates an installed Visual Studio (or Build Tools) MSVC
toolchain for building C extension gems and runs the rest of the
command line under it, as in `rb msvc gem install nokogiri`;
`msvc enable` prints the same environment for a shell to eval instead.
See [docs/msvc-enable.md](docs/msvc-enable.md). By default the newest
install wins; `--vsver <2017|2019|2022|2026|latest>` (or the
`RBMANAGER_VSVER` environment variable) pins a specific Visual Studio
version, and `msvc --list` shows what is installed. Apart from
`enable`, every word after `msvc` is the command to run, so future
`msvc` operations are spelled as flags.

`version` identifies the running binary, in cargo's shape, as in
`rbmanager 0.1.0 (9a1b2c3 2026-07-28)`. `--version` and `-V` print the
same line, so probing an unknown rb never lands in usage. The version
is the release tag the build came from, or the number in
`rbmanager.csproj` between releases. The commit and date are stamped in
at build time, and are omitted when there is no git checkout to read
them from.

The official mswin packages deliberately do not bundle
vcruntime140.dll (https://bugs.ruby-lang.org/issues/22180) and expect
the machine-wide Microsoft Visual C++ Redistributable instead. That
component installs per-machine, which is the one exception to the
no-elevation rule: when it is missing, `rb setup` offers to download
and install it through a single UAC prompt. Declining the consent
question or the UAC prompt leaves rbmanager and the installed rubies
intact; `rb setup` prints the installer URL so an administrator can
install it manually, and `rb install` / `rb use` merely warn that
ruby.exe cannot start until the runtime is present.

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
validation runs as part of the build. The MSI is currently unsigned;
only the release rb.exe is signed (see below).

An earlier iteration packaged each Ruby version as its own MSI. That
direction was dropped in favor of rbmanager; see the git history for
the sources and the verification record.

## Releases and code signing

Pushing a `v*` tag runs `.github/workflows/release.yml`, which builds
NativeAOT rb.exe for win-x64 and win-arm64, signs both, and attaches
them with `.sha256` checksums to a GitHub release. rb.exe is the one
binary users download directly with a browser, so it faces SmartScreen
with the Mark of the Web attached; it is the first signing target,
ahead of the MSI and winget channels. CI builds stay unsigned.

Free code signing provided by [SignPath.io](https://signpath.io),
certificate by [SignPath Foundation](https://signpath.org). The
signing step is isolated in `.github/actions/sign` so the provider can
be replaced later; the signing policy, the repository configuration,
and the SignPath application checklist are in
[docs/code-signing.md](docs/code-signing.md).

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
