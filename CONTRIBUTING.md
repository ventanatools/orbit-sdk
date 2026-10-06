# Contributing

Report SDK issues or propose changes through this repository. Include the SDK,
manifest and protocol versions, operating system, a minimal reproduction, and
the output of `pwsh -File tools/verify.ps1` where relevant.

Do not upload connection info, pairing files, bridge credentials, private
documents, application logs containing personal data, or proprietary app code.

Use .NET 10 and PowerShell 7; the Photoshop tests also need Node.js 22 or newer.
Run `tools/build.ps1` and `tools/verify.ps1` before submitting a pull request.
Keep the runtime libraries independent of the proprietary application and UI.
Document any contract change. Add focused tests for changed behavior, especially
authentication, cancellation, session lifetime, framing and untrusted package
input.

Naming follows the product-name rule. Wire and file-format identifiers are
product-neutral. The product name appears only in (a) the package family
`VentanaTools.<Product>.Extensions*` and its namespaces, assembly, folder and
solution names, (b) the tool command and template short names, (c)
`fixtures/hosts.json` (host id, display name, package file extension), and (d)
display text. Before the first release, renaming Orbit to Pinwheel is a
scripted, mechanical rename.

This repository is the source of truth for Orbit's extension SDK, contract,
samples and fixtures; contract changes start here and the Orbit app follows.
Lollipop has a separate SDK that follows the same Ventana conventions
([contract §2.7](docs/design/contract-v3.md#27-ventana-conventions)).

Contributions intentionally submitted for inclusion are under Apache-2.0 as
described in section 5 of LICENSE, or under MIT-0 for files in `samples/`.
Contributors retain their copyright; no copyright assignment is required. This
policy concerns contributions to the SDK repository, not independently
distributed third-party extensions.
