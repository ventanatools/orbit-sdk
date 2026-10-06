# Orbit Extensions SDK

Build extensions that supply actions, live widgets, or both to Orbit. The public
SDK communicates with the application through authenticated, out-of-process
connections. It includes a .NET companion client, portable protocol primitives,
package tooling, and simple and advanced examples.

[Developer documentation](https://dev.ventana.tools/orbit/) ·
[Getting started](docs/extension-sdk.md) ·
[Release boundaries](docs/release-status.md)

## Preview availability

This is a source preview of `VentanaTools.Orbit.Extensions`, version
**0.1.0-preview.1**, using manifest schema 2 and wire protocol 2. There is no
published NuGet feed release yet. Build the packages locally with the commands
below. The application is distributed separately; this repository contains no
Orbit application build or source.

The release target is the ordinary Store application, with an Orbit developer
mode for author tools. Actual Store-signed interoperability and a complete
consumer companion startup/install experience remain release gates. Do not
assume an existing Store version supports this preview contract. Lollipop has
a separate SDK that follows the same Ventana conventions
([contract §2.7](docs/design/contract-v3.md#27-ventana-conventions)); these
packages do not target it.

## Build and verify

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
and PowerShell 7. Use Node.js 22 or newer for the Photoshop example.

```powershell
git clone https://github.com/ventanatools/orbit-sdk.git
cd orbit-sdk
pwsh -File tools/build.ps1
pwsh -File tools/verify.ps1
```

The build creates `artifacts/packages/*.nupkg` and builds both independent
.NET samples against those packages. Verification runs public library tests,
the Countdown checks, and the Photoshop Node tests. Windows is required to run
named-pipe companions; portable protocol/package tests can run on other systems.
No private repository, private feed, Windows App SDK or WinUI tooling is needed.

## Repository

| Directory | Purpose |
|---|---|
| `src/VentanaTools.Orbit.Extensions` | The author package: declarations, pairing, framing, bounded package validation, and the .NET companion client (`Client/`) |
| `src/VentanaTools.Orbit.Extensions.Tool` | Reproducible package creation and validation (`orbit-ext`) |
| `tools/extension-package` | Packs and verifies the example archives |
| `eng` | Shared package metadata |
| `samples/dotnet-extension` | Minimal independent SDK consumer |
| `samples/countdown-extension` | Configured actions and a live timer widget |
| `samples/photoshop-extension` | Advanced Node/Adobe UXP integration reference |
| `tests` | Standalone public contract and transport tests |
| `docs/design` | The extension contract, generation 3 |
| `docs` | Author workflow, protocol, distribution and release status |

The Photoshop example is a developer reference, including Adobe setup and
prerequisites. It is not yet a finished consumer `.ccx` installation.

## Naming

This repository is the source of truth for Orbit's extension SDK, contract,
samples and fixtures; the Orbit app consumes it. Lollipop has a separate SDK
that follows the same Ventana conventions
([contract §2.7](docs/design/contract-v3.md#27-ventana-conventions)).

Wire and file-format identifiers are product-neutral. The product name appears
only in (a) the package family `VentanaTools.<Product>.Extensions*` and its
namespaces, assembly, folder and solution names, (b) the tool command and
template short names, (c) `fixtures/hosts.json` (host id, display name, package
file extension), and (d) display text. Before the first release, renaming Orbit
to Pinwheel is a scripted, mechanical rename.

## License and extension ownership

The license is split by folder. The libraries, tools, tests, fixtures and
schemas are licensed under [Apache-2.0](LICENSE), subject to the
[notices](NOTICE) and third-party dependency licenses. The samples in
`samples/` are licensed under [MIT No Attribution](LICENSES/MIT-0.txt) (MIT-0),
with a `LICENSE` file in each sample folder, so you can copy sample code into
your own extension, open or closed source, without keeping a notice. Source
files carry SPDX headers naming their license, and Ventana Tools LLC is the
copyright holder. Orbit and Lollipop themselves remain proprietary. These
licenses grant no general rights to their product names or logos.

Your own extension work remains yours. You may choose a free, paid, proprietary
or open-source license for it. When distributing SDK code, retain the applicable
license and attribution notices; code copied from the samples needs none.
Publishing an extension does not assign its ownership to Ventana; future
marketplace terms are separate.

See [CONTRIBUTING.md](CONTRIBUTING.md) for SDK changes and
[SECURITY.md](SECURITY.md) for reporting vulnerabilities without sharing secrets.
