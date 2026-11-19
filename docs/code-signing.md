# Code signing

This document is the code signing policy for rbmanager.

Signing is on hold for now, so releases ship unsigned through the
draft path described below.

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
chain, ahead of the MSI and winget channels. CI builds stay unsigned;
besides tag builds, only a manually dispatched signing rehearsal
submits binaries for signing, and those are never published.

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
US/CA/EU/UK legal entities, which is why SignPath goes first.

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
