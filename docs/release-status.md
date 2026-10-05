# Preview release status

The public SDK source preview is **0.1.0-preview.2**, using manifest schema 2
and wire protocol 2. The SDK, tools, tests, and examples use [Apache-2.0](../LICENSE)
with the repository [NOTICE](../NOTICE) and applicable third-party licenses.
Orbit and Lollipop application source remains proprietary and is not included.
Authors retain ownership of independently created extensions and select their
own extension license.

## Available in this repository

- Independent .NET 10 SDK and protocol projects, without proprietary host or
  WinUI dependencies. The .NET companion transport runs on Windows.
- Local NuGet packing, package validation, public tests, and standalone samples.
- Actions, passive live displays, contributions with both capabilities, and
  native per-placement choices through protocol 2.
- A small in-memory state example, a Countdown widget/action example, and an
  advanced Node/Adobe UXP Photoshop reference with tests and dependency notices.
- Public author documentation at [dev.ventana.tools](https://dev.ventana.tools/orbit/).

**NuGet packages have not been published.** Build the exact preview locally
using `pwsh -File tools/build.ps1`, then run `pwsh -File tools/verify.ps1`.
Do not infer a package feed release from the source version or repository being
public. This repository provides no Orbit application build.

## Orbit application support

The target is the ordinary Store application, including author tools in that
same application. **Actual Store-signed installation and interoperability
remain unverified.** Do not assume an existing Store version supports this
preview. A host exposing the current **Settings > Extensions** features is
required to exercise the live samples.

**Extension developer mode** in **Settings > Extensions** enables loose manifest import
for authoring. Normal package installation, review, enablement, and pairing do
not require that toggle. Developer mode is not Windows or Adobe developer mode,
does not replace extension consent, and does not sandbox a companion process.

The current package contract stores reviewed inert files. It does not launch
companions, install prerequisites, or declare an executable entry point.
Companion startup and runtime preparation remain manual. Countdown packages
require the .NET 10 runtime; the Photoshop source example requires Node.js 22+,
Photoshop, Adobe developer tooling, and explicit local setup.

## Remaining release work

- Verify installation, paired named-pipe access, actions, live sessions,
  reconnect, cancellation, updates, and revocation in a real Store-signed Orbit
  build, including the developer-mode workflow.
- Test SDK adoption from a fresh external checkout and publish versioned NuGet
  packages only after release checks are complete.
- Define a consumer companion installation/startup experience so users do not
  need to build examples or manually prepare runtimes.
- Verify a clean Adobe `.ccx` installation and broader installed Photoshop
  lifecycle, refusal, accessibility, and supported-version behavior. The
  existing native Photoshop check does not establish those outcomes.
- Obtain outside-author feedback and independent authentication review.
- Design publisher verification, signing, discovery, and marketplace terms.

Lollipop host compatibility, a shared marketplace, richer settings inspectors,
image faces, and additional contribution types are future work. Changing a
product display name must not silently change namespaces or wire identifiers.
