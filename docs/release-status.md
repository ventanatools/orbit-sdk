# Release status

This repository is a source preview, **0.1.0-preview.1**, of the Orbit extension
SDK. It implements [contract generation 3](design/contract-v3.md): manifest
schema 3, wire protocol 3, pairing file version 3, package archive version 2 and
pack configuration version 1. Version 2 of every format, and every earlier one,
is withdrawn: hosts and SDKs of this generation refuse them with a stable code.
Every format and API may still change before 1.0
([contract §12](design/contract-v3.md#12-versioning-and-compatibility)), and
every break is listed in the [changelog](../CHANGELOG.md).

The libraries, the tool, tests, fixtures and schemas are licensed under
[Apache-2.0](../LICENSE); the samples and templates under
[MIT-0](../LICENSES/MIT-0.txt). Authors own the extensions they build and choose
their licence.

## In this repository

- `VentanaTools.Orbit.Extensions`, the one .NET package an author installs:
  declarations, diagnostics, reason codes, pairing, the companion client
  (`CompanionApp`, `ContributionHandler`, status and backoff, face cleaning,
  coalescing and renewal), and the `Packaging` and `Wire` namespaces for hosts and
  tools. It targets plain `net10.0`, is AOT compatible and depends only on the
  base class library. The named-pipe transport runs on Windows; everything else
  is portable.
- `VentanaTools.Orbit.Extensions.Testing`, the test kit: recording sessions, an
  in-memory test host that speaks real frames, the contribution contract suite
  and conformance checks.
- `VentanaTools.Orbit.Extensions.Tool`, the `orbit-ext` tool: `new`, `validate`,
  `pack`, `verify`, `test`, `simulate`, `run`, `link` and `schema`.
- `VentanaTools.Orbit.Extensions.Templates`: `orbit-ext-action`,
  `orbit-ext-widget` and `orbit-ext-node`.
- `@ventanatools/orbit-extensions`, the Node SDK, driven by the same golden
  vectors as the .NET SDK. It is not published.
- Three samples: the smallest .NET companion, a countdown with configured
  actions and a live widget, and a Node companion that bridges to an Adobe
  Photoshop UXP plugin.
- JSON Schemas for every file format, the golden test vectors of the contract's
  Appendix A, and CI that builds, tests, packs and validates on Windows and
  Linux.

## Packages and availability

**No package is published, on nuget.org or npm.** No nuget.org package with
these IDs is official yet. Build them with `tools/build.ps1` and restore them
through a `nuget.config` with `<clear/>` and package source mapping, as the
[README](../README.md#use-the-packages-in-your-own-project) shows. The release
workflow builds, tests, packs and validates the packages on manual dispatch and
publishes nothing; [releasing.md](releasing.md) lists what comes before a first
push.

The Microsoft Store edition supports importing, installing and developing extensions; real Store-signed verification remains a release gate.

This repository contains no build of the Orbit app. Its **Settings › Extensions
& widgets** page imports and installs extensions, asks for consent, saves
connection info and, in extension developer mode, loads extensions from a
folder. Developer mode unlocks author tooling only: it never relaxes consent,
authentication, limits or text cleaning, and it does not sandbox a companion.

Companions start by hand. A package delivers reviewed, inert files and never
runs anything; the Countdown package needs the .NET 10 runtime, and the
Photoshop sample needs Node.js 22 or later, Photoshop and Adobe's developer
tooling.

## Before a release

- Verify installation, pairing, actions, live sessions, reconnection,
  cancellation, updates and revocation in a real Store-signed Orbit build,
  including the developer-mode workflow.
- An independent review of authentication and the package reader.
- Decide whether the Node SDK ships an optional native server check (it cannot
  check a pipe's owner today, contract §10) before it is published.
- Reserve the package prefix and publish with Trusted Publishing and build
  provenance ([releasing.md](releasing.md)).
- A consumer start-up and install experience for companions, so people do not
  build samples or prepare runtimes by hand.
- A clean Adobe `.ccx` installation of the Photoshop sample.
- Feedback from outside authors.

Publisher verification, signing, a curated gallery, richer setting kinds, image
faces and host-managed activation are later work; the contract reserves room for
the last three.

## Naming

This repository is the source of truth for Orbit's extension SDK, contract,
samples and fixtures. Lollipop has a separate SDK that follows the same Ventana
conventions ([contract §2.7](design/contract-v3.md#27-ventana-conventions));
these packages do not target it.

Wire and file-format identifiers are product-neutral. The product name appears
only in (a) the package family `VentanaTools.<Product>.Extensions*` and its
namespaces, assembly, folder and solution names, (b) the tool command and
template short names, (c) `fixtures/hosts.json` (host id, display name, package
file extension), and (d) display text. Before the first release, renaming the
product is a scripted, mechanical rename (`eng/rename-product.ps1`).
