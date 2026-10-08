# Releasing

Nothing in this repository publishes a package, pushes a tag or creates a
release. This note says what exists today and what a maintainer adds, in order,
before the first push.

## What exists

- `.github/workflows/verify.yml` builds and verifies every push and pull request:
  Windows runs `tools/build.ps1` and `tools/verify.ps1` on Windows PowerShell
  5.1, a second Windows job publishes the Countdown sample with Native AOT, and
  Ubuntu runs the portable tests and the Node SDK's tests.
- `.github/workflows/release.yml` runs only on manual dispatch, with read-only
  repository permission. It builds with the version in `Directory.Build.props`
  (`tools/build.ps1 -RepositoryVersion`), runs `tools/verify.ps1`, and uploads the
  packages, with package validation already applied by `dotnet pack`, as a
  workflow artifact kept for 30 days. It has no publishing step.
- Every package carries SourceLink, deterministic builds in CI, a `.snupkg`
  symbol package, `LICENSE`, `NOTICE` and `THIRD-PARTY-NOTICES.md`, and release
  notes that link to its version's section of `CHANGELOG.md` on GitHub
  (`eng/Ventana.Package.props` takes the anchor from the version's heading when
  it packs; a version without a section links to the changelog itself).

## Before the first push

1. **History.** Decide whether to rewrite this repository's earliest commits
   before anyone forks it. Push only after that decision.
2. **Repository settings.** Protect `main` and release tags with a ruleset (pull
   request, the Verify SDK check required, no force-push); turn on Dependabot
   security updates (private vulnerability reporting is already on, and it is
   the only security channel); turn off CodeQL's default setup, which
   `codeql.yml` replaces; set the description, homepage and topics.
3. **The package prefix.** Make the `ventanatools` nuget.org account an
   organization and reserve the `VentanaTools.*` prefix, so no one else can
   publish a package with these IDs.
4. **The icon.** Add `eng/icon.png` (128 × 128); the package metadata picks it up.
5. **The version.** Set `VentanaExtensionsVersion` in `Directory.Build.props` and
   `version` in `node/orbit-extensions/package.json` to the release version, and
   give its changelog entry the release date (moving anything under Unreleased
   into it) before you pack: the packages' release notes link to the heading as
   it reads then.

## Adding publishing

Add these to `release.yml` only when the steps above are done, in a pull request
of its own.

- **A protected environment.** Create a GitHub environment, for example
  `release`, with required reviewers, and give the publishing job
  `environment: release`.
- **Trusted Publishing.** On nuget.org, add a Trusted Publishing policy owned by
  the `ventanatools` organization for repository owner `ventanatools`,
  repository `orbit-sdk`, workflow file `release.yml` and environment `release`.
  The publishing job gets `permissions: id-token: write`, exchanges its OIDC
  token for a short-lived API key with the `NuGet/login` action (pinned by commit,
  as every action here is), and pushes the `.nupkg` and `.snupkg` files with
  `dotnet nuget push` and that key. No long-lived API key is stored anywhere. A
  key is valid for one hour, so request it just before the push.
- **Build provenance.** Add `attestations: write` to the publishing job and
  attest the packages with `actions/attest-build-provenance`, so anyone can check
  which workflow run built them. An SBOM may be attested the same way.
- **Tags and notes.** Trigger the publishing job from a `v*` tag on `main` (for
  example `v0.1.0-preview.1`), and create the GitHub release from that tag with
  the changelog's entry as its notes.
- **Package validation baseline.** After the first release, set
  `PackageValidationBaselineVersion` to it, so each later release is checked for
  breaking changes against the last one (before 1.0, an intended break updates
  the baseline in the same change).

## npm

`@ventanatools/orbit-extensions` stays `"private": true` until a publication
decision. Before it is published: decide whether it ships the optional native
server check of contract §10, reserve the `@ventanatools` scope, and publish with
npm's trusted publishing and provenance from the same protected environment.
