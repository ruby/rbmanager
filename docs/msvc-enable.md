# Activating the MSVC build environment for the mswin packages

## Problem

The official Ruby mswin binary (`x64-mswin64_140`, MSVC, distributed as
the relocatable `ruby-X.Y.Z-<arch>-mswinNN_MMM.zip` that rbmanager
installs) ships no devkit. Unlike RubyInstaller (mingw), which bundles
MSYS2 and exposes `ridk enable` to put a compiler on PATH, the mswin
package assumes a compiler is already present. When none is,
`gem install <native>` dies inside mkmf with the cryptic message "The
compiler failed to generate an executable file. You have to install
development tools first."

rbmanager's job here is narrow: make it possible to build native gems
from source on the end-user machine by activating an already-installed
MSVC toolchain, and to fail with an actionable message when no toolchain
is present. Precompiled fat gems for `x64-mswin64_140` are handled by a
separate project and are out of scope. CA-certificate injection is
already solved (see the trust hook in `operating_system.rb`) and is also
out of scope.

Python's pymanager is not a precedent: Python sidesteps the compiler via
prebuilt wheels, so pymanager does nothing about toolchains. The only
real model is `ridk`.

## Feasibility: proven on a real machine

Everything below was verified against a real
`ruby-4.0.5-x64-mswin64_140` install managed by rbmanager, on a machine
carrying only VS Build Tools (2017/2019/2022/v18), no full Visual
Studio.

- `vswhere.exe` at its fixed path locates the Build-Tools installs and,
  filtered to the MSVC toolset, resolves the newest install root.
- `VsDevCmd.bat -arch=amd64 -host_arch=amd64` puts `cl`/`nmake`/`link`
  on PATH and sets `INCLUDE`/`LIB`/`LIBPATH`/`VCToolsRedistDir`
  (exit 0).
- Under the prototype `rb msvc exec`, mkmf's `find_executable('cl')`
  succeeds, and a trivial C extension compiles, links, and loads:
  `extconf.rb` -> `nmake` -> `require './hello.so'` returns a value from
  native code.
- `rb msvc enable powershell | Invoke-Expression` puts `cl` on the
  current session's PATH.

The conclusion is that a compiler-only `rb msvc enable`/`rb msvc exec` is fully
feasible and small. The interesting decisions are the command surface
and how far to go on third-party dependency headers.

## Recommended command surface

A bare `rb.exe` child process cannot mutate its parent cmd/PowerShell
environment. `ridk enable` only works because it is a shell function
whose output is eval'd into the current shell. Any activation feature
must work around this, and the two useful shapes are:

1. **`rb msvc exec <command...>` (primary).** Spawns a child process
   with the toolchain and active ruby already applied. No parent
   mutation, so nothing to eval and nothing to get wrong. `rb msvc exec
   gem install nokogiri` just works. This is the recommended path for
   the common case (one build command) and for scripts/CI, and it is
   the surface that is bulletproof by construction.

2. **`rb msvc enable [cmd|powershell|pwsh]` (shell activation).** Prints
   environment assignments for the user to eval into the current shell,
   for interactive sessions where several build commands follow:

   ```
   rem cmd
   for /f "delims=" %L in ('rb msvc enable cmd') do @%L

   # PowerShell / pwsh
   rb msvc enable powershell | Invoke-Expression
   ```

   This is the escape hatch for users who want a persistently activated
   shell rather than a per-command wrapper.

Recommend shipping both. `rb msvc exec` is the headline; `rb msvc
enable` covers the interactive workflow. A third option, writing a
dot-sourced activation script into `%LOCALAPPDATA%\Ruby`, adds a file to
manage and a staleness problem (the resolved VS path is baked in) for no
gain over `rb msvc enable`, so it is not recommended.

The parent-shell-mutation constraint is handled cleanly: `rb msvc exec`
sidesteps it entirely by owning the child's environment;
`rb msvc enable` respects it by making the caller responsible for the
eval.

### Shell selection for `rb msvc enable`

The prototype takes the shell as an explicit argument and defaults to
PowerShell (the common interactive shell on modern Windows). Auto-
detecting the parent shell by walking the process tree is possible but
fragile (terminals, wrappers, `pwsh` vs `powershell`), and getting it
wrong prints syntax the shell cannot eval. Explicit selection with a
sensible default is the safer contract; auto-detection can be layered on
later as a convenience without changing the interface.

## VS discovery

Discovery uses `vswhere.exe`, which ships with the VS Installer at the
fixed, versionless path
`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe`.
The query is:

```
vswhere -latest -products * \
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 \
        -property installationPath
```

- `-products *` is required to find Build-Tools-only installs (the
  default query excludes them).
- `-requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64` narrows
  to installs that actually carry the MSVC x64/x86 toolset. This
  component id is stable across VS versions. The legacy
  `Microsoft.VisualCpp.Tools.Host.x64` id was tried first and matched
  nothing on the v18 Build Tools install on the test machine, so it is
  the wrong id to use.
- `-latest` picks the newest when several installs qualify (the test
  machine had four).

When the query returns empty (or vswhere is absent), emit an actionable
message rather than letting mkmf fail cryptically later, e.g. suggest
`winget install Microsoft.VisualStudio.2022.BuildTools --override
"--quiet --add Microsoft.VisualStudio.Workload.VCTools"`.

**Caching.** vswhere is fast (tens of ms) and the resolved path can go
stale when VS is updated or removed, so the prototype does not cache. If
profiling later shows it matters, cache the resolved `installationPath`
under `%LOCALAPPDATA%\Ruby` and invalidate it when the path no longer
exists; the correctness risk of a stale cache is not worth taking
pre-emptively.

## Activation mechanism

Activation runs `VsDevCmd.bat` in a clean child and captures the
resulting environment, exactly as the ruby/actions `mswin-build.yml`
workflow does:

```
cmd /s /c "call "<installationPath>\Common7\Tools\VsDevCmd.bat" \
           -arch=amd64 -host_arch=amd64 -no_logo && set"
```

`-no_logo` suppresses the banner so stdout is just `set` output. The
child inherits the calling shell's environment, so diffing the captured
`set` output against rb's own environment yields exactly the variables
VsDevCmd added or changed (PATH, INCLUDE, LIB, LIBPATH,
VCToolsRedistDir, and the VSCMD bookkeeping vars). `rb msvc exec`
applies that delta to the child it spawns; `rb msvc enable` prints it as `set
"K=V"` (cmd) or `$env:K = '...'` (PowerShell, single-quoted literal
with `'` doubled).

`rb msvc exec` routes the user command through `cmd /s /c` so that `.cmd`
shims (`gem`, `bundle`) and PATHEXT resolve the way they would if the
user had typed the command directly; a bare `CreateProcess` would not
find `gem` (it is `gem.cmd`).

## `NoDefaultCurrentDirectoryInExePath`

On machines where `NoDefaultCurrentDirectoryInExePath` is set (it was
*not* set on the test machine, so this is machine-specific, not
universal), mkmf's `try_link` runs the freshly built `conftest` by bare
name and the loader refuses to find it in the current directory, which
surfaces as the same cryptic "install development tools first" error
even though the compiler is present.

Both surfaces clear it for the activated environment: `rb msvc exec`
removes the variable from the child's environment block, and `rb msvc enable` emits
the unset (`set "NoDefault...="` for cmd, `Remove-Item Env:\NoDefault...`
for PowerShell). This is cheap insurance against a confusing failure and
is recommended.

## Architecture

The prototype hard-codes `-arch=amd64 -host_arch=amd64` for the current
`x64-mswin64_140` packages. arm64 is out of scope for now. When an
arm64 mswin package exists, the arch would be derived from the active
ruby's platform (or the host) and passed as `-arch=arm64`
`-host_arch=amd64` (cross) or `-host_arch=arm64` (native), and the
vswhere `-requires` would name the arm64 toolset component. That is the
only place the change lands.

## Third-party dependency dev files (scope decision)

A pure-C gem (one that includes only Ruby's own headers, e.g. `json`)
compiles as soon as a compiler is on PATH, and this was confirmed. A gem
that must link a vcpkg dependency (openssl, zlib, libyaml, libffi, gmp)
cannot, because the package ships **only** Ruby's own headers
(`include/ruby-4.0.0`) and import libs (`lib/x64-vcruntime140-ruby400.lib`).
There are no `openssl/*.h`, no `zlib.h`, no `libssl.lib`/`libcrypto.lib`,
and no opt-dir pointing at any such tree on the destination machine.

### Recommendation: phase 1 is compiler-only

Ship `rb msvc exec`/`rb msvc enable` as compiler-only first, and document the
dependency-linking limitation. This unblocks the large class of pure-C
gems immediately, is small and low-risk, and does not commit rbmanager
to shipping or versioning a pile of vcpkg dev files whose provenance and
licensing need their own consideration.

### Phase 2: bundle vcpkg dev files and inject a destination-correct opt-dir

If dependency-linking gems become important, phase 2 would bundle the
vcpkg dev files (the `include/` tree and the import libs, already
present as `vcpkg_installed/x64-windows` at build time in the workflow)
and, at activation time, inject a destination-correct
`--with-opt-dir=<install>\<vcpkg-dev-root>` so mkmf finds them. This is
a package-size and build-pipeline change on the ruby/actions side as
much as an rbmanager change, which is why it is deferred.

### Finding: the opt-dir in shipped packages is not reliable

The brief states that the ruby/actions workflow scrubs the build
machine's `--with-opt-dir=<vcpkg path>` from `rbconfig`'s
`configure_args` before packaging. The two packages installed on the
test machine did **not** have it scrubbed:

- 4.0.5: `--with-opt-dir=C:/Users/hsbt/AppData/Local/Temp/rb40/src/vcpkg_installed/x64-windows`
- 4.1.0: `--with-opt-dir=V:/github.com/ruby/ruby/vcpkg_installed/x64-windows`

mkmf feeds `configure_args` into every extension build, so this leaked
path showed up verbatim as a `-I.../vcpkg_installed/x64-windows/include`
on the `cl` command line during a real gem build. On the build machine
that path happens to exist, so it is silently harmless; on a clean
end-user machine it would point at nothing. A nonexistent `/I` is only a
warning to cl, so pure-C gems still build, but this means:

1. The scrubbing is a property of how a given package was built, not a
   guarantee rbmanager can rely on. Whatever rbmanager does about
   opt-dir must assume the baked value is untrustworthy (absent,
   correct-but-stale, or a leaked build path).
2. A phase-2 opt-dir injection should **override**, not merely
   supplement, whatever `configure_args` carries, so behavior does not
   depend on the packaging pipeline's scrubbing having run.

(These particular packages appear to be local builds, which is a
plausible reason the scrub did not run. The point stands: rbmanager
should not depend on it.)

## Prototype

`src/rbmanager/Msvc.cs` implements both subcommands, wired into
`Program.cs`'s dispatch switch as `rb msvc enable [shell]` and
`rb msvc exec <command...>`. It is ~180 lines, marked as a prototype, and
covers VS discovery, VsDevCmd activation with env-diffing, the
`NoDefaultCurrentDirectoryInExePath` clearing, and the per-shell output.
It is compiler-only (phase 1). What was exercised:

- `rb msvc enable powershell|cmd` prints correct assignments; the
  PowerShell form activates a live session via `| Invoke-Expression`.
- `rb msvc exec ruby -rmkmf -e "find_executable('cl')"` finds the compiler.
- `rb msvc exec` drives a full `extconf.rb` -> `nmake` -> load of a native
  extension.

A `gem install msgpack` under `rb msvc exec` compiled several files (proving
the toolchain is live) before failing on an
`RBIMPL_UNREACHABLE_RETURN`/`C2059` macro error in msgpack 1.8.3 against
Ruby 4.0's headers. That is an upstream gem/source incompatibility, not
a toolchain problem, and it incidentally demonstrated the leaked opt-dir
appearing on the compile line.

## Open questions

1. Auto-detect the parent shell for `rb msvc enable`, or keep the explicit
   argument with a default? (Prototype: explicit, default PowerShell.)
2. Should `rb msvc exec`/`rb msvc enable` also guarantee the active ruby's
   `current\bin` is on PATH, or continue to rely on `install` having put
   it there? (Prototype relies on install.)
3. Phase 2 trigger: is dependency-linking demand high enough to justify
   bundling vcpkg dev files, and should the dev-file tree ship inside
   the mswin package or as a separate rbmanager-managed download?
4. Should a leaked/nonexistent `--with-opt-dir` in `configure_args` be
   actively stripped or overridden by `rb msvc enable` even in phase 1, to
   remove the confusing `-I<nonexistent>` from every compile line?
