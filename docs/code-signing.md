# Code signing

This document is the code signing policy for rbmanager and the
operating manual for the release signing pipeline.

## What is signed, and why rb.exe first

The only signed artifacts are the release builds of `rb.exe`
(`rb-x64.exe` and `rb-arm64.exe` on the GitHub release page). They are
built by `.github/workflows/release.yml` from a version tag of
https://github.com/ruby/rbmanager, and nothing that was not built from
this repository's source is ever submitted for signing.

rb.exe is the one binary users download directly with a browser, so it
carries the Mark of the Web and faces SmartScreen head-on; unsigned, it
is effectively blocked by the "Windows protected your PC" dialog. That
makes it the highest-priority signing target of the whole distribution
chain, ahead of the MSI and winget channels. CI builds and anything
not built from a tag stay unsigned.

## Provider: SignPath Foundation now, replaceable later

Signing starts on the free open-source tier of
[SignPath Foundation](https://signpath.org). Consequences to be aware
of:

- The certificate subject is "SignPath Foundation", not a Ruby entity.
- Every signing request requires a manual approval in the SignPath UI.
- All team members with access to the SignPath project need MFA.
- Only artifacts built from this repository's source may be signed.
- The project must publish this policy and the attribution "Free code
  signing provided by SignPath.io, certificate by SignPath Foundation"
  (see README).
- Timestamping (RFC 3161) is applied by the platform.

The plan is to migrate to Azure Trusted Signing (now Artifact Signing)
or an OV certificate on a cloud HSM once either becomes practical;
Azure's Public Trust identity validation is currently limited to
US/CA/EU/UK legal entities, which is why SignPath goes first. To keep
the migration cheap, all provider-specific logic lives in the
composite action `.github/actions/sign`. The release workflow only
ever sees "unsigned artifact in, signed files out", so swapping the
provider means editing that one action.

## How the release pipeline works

Pushing a `v*` tag runs `.github/workflows/release.yml`:

1. The build job runs the test suite, publishes NativeAOT `rb.exe` for
   win-x64 and win-arm64 with `-p:Version=<tag>`, and uploads both as
   one unsigned artifact.
2. The release job runs in the `release-signing` environment, which
   holds the credentials and can require a reviewer approval before
   the job starts.
3. `.github/actions/sign` submits the artifact to SignPath and waits
   (up to an hour) for the manual approval there.
4. The same action verifies the signatures with
   `signtool verify /pa /tw`, so a signature without an RFC 3161
   timestamp fails the release. When
   `SIGNPATH_CERTIFICATE_THUMBPRINT` is set, `/sha1` additionally pins
   the signing certificate; without it any chain the default
   Authenticode policy trusts is accepted.
5. `.sha256` checksum files are generated next to the binaries and
   everything is attached to a GitHub release created from the tag.

When the SignPath credentials are not configured, the signing step is
skipped with a workflow warning and the release is created as a draft
so unsigned binaries are never published silently.

## The test certificate period

While SignPath issues a test certificate, no machine trusts its chain,
so `signtool verify` would always fail. Setting the repository
variable `SIGNPATH_TEST_CERTIFICATE=true` switches verification to a
rehearsal check: it only asserts that a signature from the expected
certificate is present (and matches the thumbprint when configured).
That mode is not a release gate; remove the variable as soon as the
production certificate is in place.

The exe's VERSIONINFO (ProductName `rbmanager`, FileDescription,
company, version) is set in `src/rbmanager/rbmanager.csproj`; SignPath
uses it to check that the artifact matches the project.

## Repository configuration

Everything lives in the `release-signing` GitHub environment
(Settings, Environments). Recommended protection: required reviewers,
so a human gates every signing run.

Secret (environment-scoped, only the release workflow can read it):

- `SIGNPATH_API_TOKEN`: API token of a SignPath user with submitter
  permission.

Variables (environment or repository level):

- `SIGNPATH_ORGANIZATION_ID`: the shared ruby organization on
  SignPath, the same one that signs the mswin binary packages
- `SIGNPATH_PROJECT_SLUG`
- `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` (optional; empty uses the
  SignPath project's default configuration)
- `SIGNPATH_SIGNING_POLICY_SLUG` (defaults to `release-signing` when
  unset in the sign action; `test-signing` during the test
  certificate period)
- `SIGNPATH_TEST_CERTIFICATE` (`true` only during the test
  certificate period, see above)
- `SIGNPATH_CERTIFICATE_THUMBPRINT` (optional; SHA-1 thumbprint of
  the signing certificate, set once the production certificate is
  issued)

Until these exist, tag pushes still work and produce unsigned draft
releases.

## SignPath Foundation application checklist

Registering the project with SignPath Foundation is a manual step.
Information the application needs:

- Repository URL: https://github.com/ruby/rbmanager
- License: BSD-2-Clause (see `LICENSE`)
- Artifact description: `rb.exe`, a self-contained .NET 8 NativeAOT
  Windows executable (the `rb` command of the rbmanager Ruby version
  manager), built for win-x64 and win-arm64 by GitHub Actions in this
  repository.
- Code signing policy: this document.
- Attribution: present in the README.

rbmanager joins the existing ruby organization on SignPath as its
second project, next to the one signing the mswin binary packages.
SignPath adds it after the Ruby project's test signing review, so the
project and artifact configuration slugs arrive later; the workflow
runs the unsigned draft path until the variables above are filled in.

SignPath-side setup after approval:

1. Install the SignPath GitHub App on the repository (already done)
   and link GitHub Actions as a trusted build system to the project.
2. Create an artifact configuration for the `rb-unsigned` artifact the
   build job uploads, a zip container holding both exes:

   ```xml
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <zip-file>
       <pe-file path="rb-x64.exe">
         <authenticode-sign/>
       </pe-file>
       <pe-file path="rb-arm64.exe">
         <authenticode-sign/>
       </pe-file>
     </zip-file>
   </artifact-configuration>
   ```

3. Create a signing policy for release signing with manual approval
   and note its slug in `SIGNPATH_SIGNING_POLICY_SLUG`.
4. Create an API token for a CI user with submitter permission and
   store it as the `SIGNPATH_API_TOKEN` secret.
