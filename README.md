# Orbit Extensions SDK

Build extensions that supply actions, live widgets, or both to Orbit. The public
SDK communicates with the application through authenticated, out-of-process
connections. It includes a .NET companion client, portable protocol primitives,
package tooling, and simple and advanced examples.

[Developer documentation](https://dev.ventana.tools/orbit/) ·
[Getting started](docs/extension-sdk.md) ·
[Release boundaries](docs/release-status.md)

## Preview availability

This is a source preview, version **0.1.0-preview.2**, using manifest schema 2
and wire protocol 2. There is no published NuGet feed release yet. Build the
packages locally with the commands below. The application is distributed
separately; this repository contains no Orbit application build or source.

The release target is the ordinary Store application, with an Orbit developer
mode for author tools. Actual Store-signed interoperability and a complete
consumer companion startup/install experience remain release gates. Do not
assume an existing Store version supports this preview contract. Lollipop's
extension host is coming later; these packages do not establish compatibility.

## Build and verify

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
and PowerShell 7. Use Node.js 22 or newer for the Photoshop example.

```powershell
git clone https://github.com/ventanatools/orbit-sdk.git
cd orbit-sdk
pwsh -File tools/build.ps1
pwsh -File tools/verify.ps1
```

The build creates `artifacts/extension-sdk/*.nupkg` and builds both independent
.NET samples against those packages. Verification runs public library tests,
the Countdown checks, and the Photoshop Node tests. Windows is required to run
named-pipe companions; portable protocol/package tests can run on other systems.
No private repository, private feed, Windows App SDK or WinUI tooling is needed.

## Repository

| Directory | Purpose |
|---|---|
| `src/Orbit.Extensions.Sdk` | .NET companion client and contribution handlers |
| `src/Orbit.Extensions.Protocol` | Declarations, pairing, framing and bounded package validation |
| `tools/extension-package` | Reproducible package creation and validation |
| `samples/dotnet-extension` | Minimal independent SDK consumer |
| `samples/countdown-extension` | Configured actions and a live timer widget |
| `samples/photoshop-extension` | Advanced Node/Adobe UXP integration reference |
| `tests` | Standalone public contract and transport tests |
| `docs` | Author workflow, protocol, distribution and release status |

The Photoshop example is a developer reference, including Adobe setup and
prerequisites. It is not yet a finished consumer `.ccx` installation.

## License and extension ownership

The SDK, tools, tests and examples are licensed under [Apache-2.0](LICENSE),
subject to the [notices](NOTICE) and third-party dependency licenses. Orbit and
Lollipop themselves remain proprietary. This license grants no general rights
to their product names or logos.

Your own extension work remains yours. You may choose a free, paid, proprietary
or open-source license for it. When distributing SDK code or modifying examples,
retain the applicable license and attribution notices. Publishing an extension
does not assign its ownership to Ventana; future marketplace terms are separate.

See [CONTRIBUTING.md](CONTRIBUTING.md) for SDK changes and
[SECURITY.md](SECURITY.md) for reporting vulnerabilities without sharing secrets.
