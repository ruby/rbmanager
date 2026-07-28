# Comprehensive C# test plan for rbmanager

This is a handoff document. It specifies the test suite to be implemented
(by another agent) against the current behavior of `rbmanager/*.cs` at
commit 96f9763. It contains no test code; it defines scope, required
refactoring, the full test-case inventory, and the execution plan.

Ground rules for the implementer:

- Pin current behavior. Where this plan flags a behavior as questionable
  (section 6), write the test against what the code does today and mark
  it with a comment referencing the open question. Do not change product
  behavior as part of the test PR, except for the minimal seams in
  section 3.
- Tests must never touch the real `%LOCALAPPDATA%\Ruby`, the real
  `HKCU\Environment` PATH value, or the network. Every test that would
  do so goes through a seam or a loopback server.
- All tests are Windows-only by nature (junctions, registry, cmd.exe).
  Do not add cross-platform shims.

## 1. Current spec being tested

The product is a single AOT-published exe `rb.exe` (net8.0-windows).
Layout: `%LOCALAPPDATA%\Ruby` with `rubies\<name>` for installs,
`current` as an NTFS junction to the active install, `bin\rb.exe` for
the tool itself.

Command surface and contracts:

| Command | Behavior | Exit |
|---|---|---|
| `rb setup` | Copy own exe to `<root>\bin\rb.exe` (skip if already running from there, case-insensitive), append that dir to user PATH | 0 |
| `rb install <zip\|url>` | If URL, download to `%TEMP%` first (deleted in `finally`). Zip must contain exactly one root dir named `ruby-*`; extract under `rubies`, inject the embedded `operating_system.rb` trust hook into `lib\ruby\site_ruby\rubygems\defaults\`, then switch `current` to it and ensure `current\bin` on user PATH. Refuse if already installed | 0 / 1 |
| `rb list` | Installed names sorted, active one starred | 0 |
| `rb use <query>` | Resolve query (exact or case-insensitive substring; must be unambiguous), recreate the `current` junction, ensure PATH | 0 / 1 |
| `rb uninstall <query>` | Resolve; if active, delete the junction first and print a hint; delete the install dir recursively | 0 / 1 |
| `rb msvc [--vsver <year>] [--] <cmd...>` | Locate VsDevCmd via vswhere (narrowed to the requested VS product year, if any; `--vsver` > `RBMANAGER_VSVER` > newest), compute the env delta of activation and apply it to a `cmd /s /c` child (so `.cmd` shims resolve); removes `NoDefaultCurrentDirectoryInExePath`; propagates the child's exit code. Options are leading-only; `--` ends option parsing. `enable` is the only reserved word after `msvc` | child / 1 |
| `rb msvc enable [--vsver <year>] [shell]` | Same delta printed as per-shell assignments plus an unset of `NoDefaultCurrentDirectoryInExePath`. Shell defaults to PowerShell; `cmd`/`bat` selects cmd syntax. `--vsver` parses on either side of `enable`. No toolchain: actionable warning on stderr | 0 / 1 |
| `rb msvc --list` | All installs carrying the MSVC toolset, newest first, `*` on the one default resolution would pick. Terminal: nothing may follow it. No toolchain: same warning as the other two | 0 / 1 / 2 |
| `rb version` | Print `rbmanager <version> (<commit> <date>)` in cargo's shape, from the assembly's informational version (whose `+<commit>` suffix carries the commit) and the `CommitDate` assembly metadata. The parenthetical is dropped when the build had no git checkout, and the assembly version stands in for a missing informational version | 0 |
| anything else | Usage text | 2 |

Any thrown exception is caught in `Main`, printed as `rb: <message>` to
stderr, exit 1.

## 2. Test architecture

- Framework: xUnit (current stable), single test project
  `rbmanager.Tests/rbmanager.Tests.csproj`, `net8.0-windows`,
  referencing the product project. Add a `rbmanager.sln` binding both.
- The product classes are `internal static`. Add
  `InternalsVisibleTo("rbmanager.Tests")` (csproj `ItemGroup`, not an
  attribute file).
- Three layers, tagged with xUnit traits so they can be filtered:
  - `Category=Unit`: pure logic, no filesystem, no processes.
  - `Category=Integration`: real filesystem in a per-test temp dir,
    test-scoped registry subkey, stub `.bat` processes. Runs on any
    Windows machine with no VS installed.
  - `Category=E2E`: drives the built `rb.exe` as a process end to end.
  - `Category=RequiresVS`: opt-in tests that need a real VS Build Tools
    install; excluded by default.
- Isolation:
  - Every filesystem test creates a unique temp root
    (`Path.Combine(Path.GetTempPath(), "rbmanager-tests", Guid...)`)
    and deletes it in `Dispose`. Junction targets and junction points
    both live inside it.
  - Tests that redirect `Console.Out`/`Console.Error` or mutate the
    process environment (`Program`/`Msvc` in-process tests) go in one
    xUnit collection (`[Collection("process-global")]`) so they never
    run in parallel with each other. E2E tests spawn processes and can
    stay parallel because each gets its own root via the env seam.
  - Registry tests use a per-test subkey under
    `HKCU\Software\rbmanager-tests\<guid>` and delete it after.
- Zip fixtures are generated in test code with `ZipArchive` (a fake
  `ruby-9.9.x-x64-mswin64_140/` tree containing `bin/ruby.exe` stub
  bytes and a couple of nested files). No binary fixtures are committed.
- The URL install path is tested with an `HttpListener` bound to
  `http://127.0.0.1:<free port>/` serving a generated zip. No external
  network.

## 3. Prerequisite refactoring (test seams)

Keep this to the minimum below; each item is a mechanical change.

1. Root redirection. `Program.Root/Rubies/Current` are `static
   readonly` fields computed from the Known Folder API, which ignores
   the `LOCALAPPDATA` environment variable, so tests cannot redirect it
   from outside. Change the three fields into properties (or a small
   `Paths` holder) computed from a base directory that is
   `Environment.GetEnvironmentVariable("RBMANAGER_ROOT")` when set and
   the current value otherwise, re-read on each access (no caching in a
   static initializer, so E2E and in-process tests can both redirect).
   This also gives E2E tests their redirect for free.
2. `UserPath.Ensure` seam. Add an internal overload
   `Ensure(string entry, RegistryKey environmentKey, bool broadcast)`;
   the public method opens `HKCU\Environment` and passes
   `broadcast: true`. Tests pass their scratch subkey and
   `broadcast: false` so no `WM_SETTINGCHANGE` is sent. For E2E, honor
   an env var `RBMANAGER_ENV_KEY` naming an alternative HKCU-relative
   subkey (and suppress the broadcast when it is set) so a full
   `rb install` run never touches the real PATH.
3. `Msvc` seams. Make `VsWhere` an internal settable property (env
   override `RBMANAGER_VSWHERE` for E2E), and make `ActivatedDelta`,
   `LocateVsDevCmd`, `ParseShell`, `Assignment`, `Unset`, `QuoteArg`
   internal instead of private. `ActivatedDelta(vsdevcmd)` already takes
   the bat path as a parameter, so a stub `.bat` fixture exercises the
   whole activation pipeline without VS. Add an internal seam for the
   VsDevCmd path used by `Enable`/`Exec`
   (`RBMANAGER_VSDEVCMD` env override checked before `LocateVsDevCmd`)
   so Enable/Exec are E2E-testable with the stub.
4. No other production changes. In particular do not restructure
   `Program`'s command methods; in-process tests call the internal
   methods directly with `Console.SetOut`/`SetError` capture, and the
   dispatch/exit-code contract is covered at the E2E layer.

## 4. Test-case inventory

Names below are indicative; group into one class per production class
plus one E2E class.

### 4.1 Program: zip validation (`SingleRootDirectory`) — Unit

1. Single root `ruby-4.0.5-x64-mswin64_140/` with nested entries →
   returns the root name.
2. Single root not starting with `ruby-` → throws
   `unexpected root directory '<name>' (want ruby-*)`.
3. Two root dirs → throws `must contain a single root directory`.
4. One root dir plus one root-level file (e.g. `README.txt`) → the file
   counts as a second root → throws (pin; see 6.4).
5. Empty zip → empty roots array → `must contain a single root
   directory`.
6. Entries using backslash separators (build such an archive manually
   via `ZipArchive.CreateEntry(@"ruby-x\bin\ruby.exe")`) → every entry
   becomes its own root → throws (pin as known limitation; see 6.4).

### 4.2 Program: `Resolve` — Integration (needs `rubies` dirs)

Seed the redirected root with e.g. `ruby-4.0.5-x64-mswin64_140`,
`ruby-4.1.0-x64-mswin64_140`.

7. Exact full name → that name.
8. Unique substring `4.0.5` → full name.
9. Case-insensitive substring `RUBY-4.0` → full name.
10. No match → `no installed ruby matches '<q>'`.
11. Ambiguous substring `4.` → error message listing both matches,
    comma-separated, in sorted order.
12. Exact name that is also a substring of another install (seed
    `ruby-4.1.0` and `ruby-4.1.0-rc1`, query `ruby-4.1.0`) → currently
    ambiguous → error (pin; flagged in 6.1).
13. No `rubies` directory at all → behaves as empty → no-match error.

### 4.3 Program: install / list / use / uninstall — Integration

All with redirected root and the registry/broadcast seam. Console
captured. These call the internal methods directly.

14. `Install` from a local fixture zip: extracts to
    `rubies\<name>`, `current` junction points at it, `current\bin`
    appended to the scratch PATH value, returns 0, output contains
    `Installed <name>`.
15. Trust hook injected: after install,
    `<install>\lib\ruby\site_ruby\rubygems\defaults\operating_system.rb`
    exists and is byte-identical to
    `overlay\site_ruby\rubygems\defaults\operating_system.rb`.
16. Install the same zip twice → `InvalidOperationException`
    `<name> is already installed`; the existing tree is untouched.
17. Install a second version → `current` now targets the new one (pin:
    install always switches; see 6.2).
18. Install from URL: loopback `HttpListener` serving the fixture zip →
    installs normally and the downloaded temp file
    (`%TEMP%\<zip name>`) is gone afterwards.
19. Install from URL returning 404 → exception propagates (exit-1 path),
    no install dir created, no leftover temp file.
20. Install from URL whose path ends in `/` (empty file name) → pin
    whatever happens today (expected: `File.Create` on a directory path
    fails; see 6.3).
21. Bad zip (root not `ruby-*`): nothing created under `rubies`.
22. `List` with no rubies dir → prints nothing, returns 0.
23. `List` with two installs and one active → sorted output, `*` on the
    active line, two leading spaces otherwise (`"* name"` / `"  name"`).
24. `List` with installs but no `current` junction → no starred line.
25. `Use` with unique query → junction retargeted, PATH ensured, prints
    `Now using <full name>`, returns 0.
26. `Use` ambiguous/no-match → throws, junction unchanged.
27. `Uninstall` non-active version → its dir removed, `current` still
    points at the active one, prints `Uninstalled <name>`.
28. `Uninstall` the active version → junction removed (path no longer
    exists), install dir removed, output includes the
    `run `rb use`` hint line.
29. `Uninstall` with ambiguous query → throws, nothing deleted.
30. Exploratory: manually delete the junction target dir, then call
    `CurrentTarget` and `Uninstall` of that name. Pin observed behavior
    with a comment (dangling-junction semantics of
    `DirectoryInfo.Exists`/`LinkTarget` decide the path taken; see 6.5).

### 4.4 Program: `Setup` — Integration + E2E

31. (E2E) Run the built `rb.exe setup` with `RBMANAGER_ROOT` and
    `RBMANAGER_ENV_KEY` set → `<root>\bin\rb.exe` exists and is
    byte-identical to the source exe, scratch PATH contains
    `<root>\bin`, exit 0, output has both lines.
32. (E2E) Run setup again from the copied exe itself
    (`<root>\bin\rb.exe setup`) → no self-copy error (source==dest is
    skipped), exit 0.
33. (E2E) Setup when `bin\rb.exe` already exists → overwritten.

### 4.5 CLI dispatch and exit codes — E2E

Drive the exe built by `dotnet build` (see section 5 for AOT).

34. No args → usage on stdout, exit 2.
35. Unknown command → usage, exit 2.
36. `install` with no argument, `use` with no argument, `msvc` with no
    command → usage, exit 2 (the `msvc` parser requires a non-empty
    command). Malformed msvc options too: `msvc --vsver` (no value),
    `msvc --vsver 2022` (no command), `msvc --list cl` (a command after
    the terminal query), `msvc enable --vsver` (no value), `msvc enable`
    with two shells.
37. Failing command (e.g. `use nosuch`) → stderr starts with `rb: `,
    exit 1, stdout empty.
38. Full lifecycle: install A → list (A starred) → install B → list (B
    starred) → use A → uninstall B → list (only A) → uninstall A →
    list (empty). All through the process boundary with a shared
    per-test root.

### 4.6 Junction — Integration

39. Create junction to an existing dir → `Directory.Exists(junction)`
    true, `new DirectoryInfo(junction).LinkTarget` equals the full
    target path, and a file created in the target is visible through
    the junction.
40. Relative target path → resolved with `Path.GetFullPath` (LinkTarget
    is absolute).
41. Target with trailing backslash → trimmed, identical result.
42. Target does not exist → `DirectoryNotFoundException` naming the
    path.
43. Target path containing spaces and non-ASCII characters →
    round-trips.
44. Junction path already exists as an empty directory → succeeds
    (Create tolerates a pre-existing empty dir).
45. Junction path exists as a non-empty directory →
    `FSCTL_SET_REPARSE_POINT` fails → `IOException` with the Win32
    error (pin the observed error).
46. Recreate flow as `SwitchTo` does it: create junction to A,
    `Directory.Delete(junction)`, create junction to B → LinkTarget is
    B and A's contents are intact (deleting a junction never deletes
    the target's contents).
47. `CurrentTarget` parsing: junction to `...\rubies\ruby-x` → returns
    `ruby-x`; no junction → null; plain directory (not a reparse point)
    at `current` → `LinkTarget` null → null.

### 4.7 UserPath — Integration (scratch registry key)

All against `HKCU\Software\rbmanager-tests\<guid>` with
`broadcast: false`.

48. No `Path` value → value created with exactly the entry, kind
    `ExpandString`.
49. Existing REG_SZ value → append `;entry`, kind stays REG_SZ.
50. Existing REG_EXPAND_SZ with `%SystemRoot%\tools` inside → appended,
    kind stays ExpandString, `%SystemRoot%` still unexpanded in the
    stored value (read back with `DoNotExpandEnvironmentNames`).
51. Entry already present, exact → value unchanged (compare raw string
    before/after).
52. Entry present with different case → unchanged.
53. Entry present with surrounding whitespace / empty segments
    (`;; C:\x ;`) → recognized, unchanged.
54. Existing value with trailing semicolons → trimmed before append
    (no `;;` in result).
55. Empty-string existing value → result is exactly the entry, no
    leading `;`.

### 4.8 Msvc: pure helpers — Unit

56. `ParseShell`: `null`, `powershell`, `pwsh`, `ps` → PowerShell;
    `cmd`, `bat` → Cmd; `zsh` → throws `unknown shell 'zsh'`. (Note
    case sensitivity: `PowerShell` throws today. Pin; see 6.6.)
57. `Assignment` cmd: `set "K=V"` verbatim, including when V contains
    spaces, `%`, and quotes (no escaping today; pin).
58. `Assignment` PowerShell: `$env:K = 'V'`; a `V` containing `'` is
    doubled; a `V` containing `$` stays literal.
59. `Unset` both shells: exact strings
    (`set "K="` / `Remove-Item Env:\K -ErrorAction SilentlyContinue`).
60. `QuoteArg`: bare word unchanged; contains space → wrapped in
    quotes; empty string → `""`; tab → quoted; embedded `"` → not
    escaped (pin as known limitation; see 6.7).

Added with `--vsver` (numbered after the sections below; 75 was
already taken by the AOT publish smoke):

76. `EnableArgs`: shell and `--vsver <year>`/`--vsver=<year>` parse in
    either order; missing/empty value, unknown option, or two shells →
    null.
77. `ExecArgs`: options are leading-only; `--` ends option parsing and
    passes `--vsver`-looking tokens through as the command; an option
    token after the first command token is command, not option;
    missing/empty value, unknown leading option, or no remaining
    command → null.
78. `VsVerRanges` maps exactly 2017/2019/2022/2026 to the
    `[15.0,16.0)`-style installationVersion ranges.

Added when `msvc exec <cmd...>` became `msvc <cmd...>` and `msvc list`
became `msvc --list`:

86. `Parse` (everything after the `msvc` token): a bare command passes
    through verbatim, with or without a leading `--vsver`; `enable`
    reaches the enable path and takes `--vsver` on either side of it;
    `--` makes even `enable` a command; `--list` is terminal, so a
    command (or the word `enable`) after it → null, while a year before
    it is read and unused; option recognition stops at the first command
    token, so `ruby --list` is a two-token command; bare `msvc`, a
    missing or empty `--vsver` value, an unknown leading option, `--`
    alone, and two shells after `enable` → null.

### 4.9 Msvc: activation with a stub VsDevCmd — Integration

Stub `.bat` fixture written per test, e.g. sets `RB_TEST_NEW=hello`,
modifies `PATH` by prefixing a marker dir, sets a var whose value
contains `=` and one containing non-ASCII, and `exit /b 0`.

61. `ActivatedDelta(stub)` → contains exactly the added and changed
    variables; unchanged inherited variables are absent.
62. Delta detects a changed value for a preexisting variable (set a
    known var in the test process first, have the stub change it).
63. Value containing `=` → split on the first `=` only; key and full
    value preserved.
64. Stub exiting nonzero → `VsDevCmd activation failed`.
65. `Enable` (via the `RBMANAGER_VSDEVCMD` seam) PowerShell → stdout
    lines are valid `$env:` assignments for the stub's vars and the
    final line is the `Remove-Item Env:\NoDefaultCurrentDirectoryInExePath`
    unset; exit 0. Same for cmd syntax.
66. `Enable`/`Exec` with the toolchain seam pointing nowhere and
    `VsWhere` set to a nonexistent path → stderr warning containing the
    winget hint, exit 1, stdout empty (the warning must not go to
    stdout, since `rb msvc enable | Invoke-Expression` would eval it).
67. `LocateVsDevCmd` with `VsWhere` nonexistent → null (covered
    behaviorally by 66; also assert directly).
68. `Exec` with stub: run `cmd /c set` as the command, capture output →
    child sees the stub's variables and does not see
    `NoDefaultCurrentDirectoryInExePath` (set it in the test process
    first).
69. `Exec` exit-code propagation: `rb msvc cmd /c exit 7` → 7.
70. `Exec` resolves `.cmd` shims: put a `hello.cmd` on the stub-added
    PATH dir, `msvc hello` → runs it (proves the `cmd /s /c` routing
    and PATHEXT behavior).
71. `Exec` argument quoting: an argument with spaces survives to the
    child (child echoes `%1`-style or a tiny script writes its argv to
    a file).

Added with `--vsver`:

79. `EffectiveVsVer`: flag > `RBMANAGER_VSVER` > null (latest); the
    explicit `latest` value overrides the env var back to newest; an
    unknown year (flag or env) throws `unknown Visual Studio version`.
80. `Enable` with a `--vsver` year and the `RBMANAGER_VSDEVCMD` stub
    set → the stub still short-circuits discovery.
81. `Enable`/`Exec` with a requested year and `VsWhere` nonexistent →
    stderr names the year and suggests the matching
    `Microsoft.VisualStudio.<year>.BuildTools` winget id, exit 1.
82. `List` with `VsWhere` nonexistent → same warning as the other two,
    exit 1, stdout empty.

Added with the command-surface change:

87. `Dispatch` routes a parsed request to the operation it names:
    `enable cmd` prints the stub's assignments, and `cmd /c exit 7`
    runs as a command and propagates 7.

### 4.10 Msvc against real Visual Studio — RequiresVS (opt-in)

Skipped unless vswhere resolves an install (use a runtime skip, e.g.
`Assert.Skip`/`SkippableFact`).

72. `LocateVsDevCmd` returns an existing `VsDevCmd.bat`.
73. `ActivatedDelta` includes `INCLUDE`, `LIB`, and a `PATH` change.
74. `rb msvc cl` (E2E) exits 0 with cl's banner on stderr.

Added with `--vsver`:

83. `LocateVsDevCmd(<year>)` for every installed year resolves a
    `VsDevCmd.bat` under that year's install path.
84. `rb msvc --list` (E2E) prints one line per install, newest first,
    `*` on the first, install path on each line.
85. `rb msvc --vsver <newest installed year> cl` (E2E) runs cl.

### 4.11 AOT publish smoke — E2E (opt-in, slow)

75. `dotnet publish -r win-x64` the product, run
    `rb.exe install <fixture zip>` with the seams set → succeeds and
    the trust hook file is written (verifies the embedded resource and
    the `LibraryImport` P/Invokes survive AOT; this is the one place
    the AOT binary differs meaningfully from the CoreCLR build).

### 4.12 Program: `rb version` — Unit + E2E + Publish

Everything is read back from the assembly instead of a literal, so the
values are whatever the build stamped in and the tests pin the shape.

88. (E2E) `rb version` → exit 0 and one line matching
    `rbmanager <version> (<commit> <date>)`, the parenthetical optional.
89. (Unit) `FormatVersion` with all three present → the full
    cargo-shaped line.
90. (Unit) `FormatVersion` with the commit, the date, or both missing →
    the parenthetical carries only what is there, or is dropped
    entirely. A prerelease tag keeps its own `-rc1` suffix.
91. (Unit) `FormatVersion` falls back to the assembly version when there
    is no informational version, and to `unknown` when there is neither.
92. (Unit) `SelfVersion` for the current build starts with `rbmanager `
    and a parseable version.
93. (Publish) The same command against the AOT exe prints exactly what
    the in-process resolution returns. NativeAOT is the one place the
    assembly-attribute lookup could come back empty, and both builds
    come from the same source tree at the same commit, so any
    divergence is AOT.

## 5. Execution plan

Phased so each phase leaves the tree green.

- Phase 0 (seams): section 3 changes, `rbmanager.sln`, empty test
  project with one smoke test. Gate: `dotnet build` of both projects
  and a manual `rb list` still working.
- Phase 1: Unit tests (4.1, 4.8).
- Phase 2: Integration tests (4.2, 4.3, 4.6, 4.7, 4.9). This is the
  bulk; implement Junction and UserPath first since Program's tests
  depend on their correctness for assertions.
- Phase 3: E2E (4.4, 4.5), building the exe once per test run via a
  fixture (`dotnet build -c Release`, cache the output path in a
  collection fixture).
- Phase 4: opt-in suites (4.10, 4.11).

Run commands:

```
dotnet test rbmanager.sln --filter "Category=Unit|Category=Integration"   # default, no VS needed
dotnet test rbmanager.sln --filter "Category=E2E"
dotnet test rbmanager.sln --filter "Category=RequiresVS"                  # only on a VS machine
dotnet test rbmanager.sln --filter "Category=Publish"                     # AOT publish; needs VS
```

Note (implementation): case 32 ("setup run from the copied exe") moved out
of the default E2E suite into the Publish suite. The framework-dependent
Debug apphost `rb.exe` cannot run once copied away from its `rb.dll`, so
the self-copy-skip path is only meaningful against the self-contained AOT
single-file exe, which the Publish suite already builds.

Full verification at the end of each phase, not after every test added.
There is no CI wiring in scope for this repo yet; the suite must pass
locally on a Windows 11 machine without Visual Studio for everything
except `RequiresVS`.

Rough size: about 75 test cases, one test class per production class
plus `CliE2eTests`, and shared helpers for temp roots, fixture zips,
console capture, the scratch registry key, and the stub bat.

## 6. Behaviors flagged during planning (pin, do not fix)

Write the tests against current behavior and reference this section in
a comment. Each is a product decision to make separately.

1. `Resolve` gives exact matches no precedence: a name that is exactly
   the query is still ambiguous when the query is also a substring of
   another install (case 12).
2. `install` always switches `current` to the new install, even when
   another version was deliberately active (case 17).
3. `install <url>` derives the temp filename from the URL path;
   an empty last segment breaks it, and two parallel installs of the
   same filename would collide in `%TEMP%` (case 20 pins the former).
4. Zip handling assumes `/`-separated entry names and treats a
   root-level file as a second root (cases 4, 6).
5. Dangling `current` (target deleted out of band) has unpinned
   semantics in `CurrentTarget`/`Uninstall` (case 30 pins it).
6. `ParseShell` is case-sensitive (`PowerShell` is rejected).
7. `QuoteArg` does not escape embedded quotes; `rb msvc <command...>`
   with an argument containing `"` produces a broken cmd line (case 60
   pins the helper's output only).
