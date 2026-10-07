# Orbit Extensions SDK

Build extensions for Orbit, the radial launcher by Ventana Tools. An extension's
companion is a separate program, in .NET, Node.js or any other language, that
supplies actions and live widgets to Orbit over an authenticated named pipe. This
repository holds everything an author needs: the .NET author package and test
kit, the `orbit-ext` tool, project templates, the Node SDK, samples, JSON
Schemas, the golden test vectors, and the normative
[extension contract, generation 3](docs/design/contract-v3.md).

[Contract](docs/design/contract-v3.md) ·
[Author guides on dev.ventana.tools](https://dev.ventana.tools/) ·
[Release status](docs/release-status.md) ·
[Changelog](CHANGELOG.md)

## Status

This is a source preview, **0.1.0-preview.1**, of contract generation 3:
manifest schema 3, wire protocol 3, pairing file version 3, package archive
version 2 and pack configuration version 1. Every format and API may still
change before 1.0, and every break is listed in the [changelog](CHANGELOG.md).

**No package is published.** No nuget.org package with these IDs is official
yet. Build the packages from this repository and restore them through a
`nuget.config` that maps the package family to your local folder (below), so a
package with the same name from anywhere else can never be restored in their
place.

The Microsoft Store edition supports importing, installing and developing extensions; real Store-signed verification remains a release gate.

This repository contains no build or source of the Orbit app itself.

## Packages

| Package | What it is |
|---|---|
| `VentanaTools.Orbit.Extensions` | The author package for .NET: declarations, diagnostics, reason codes, pairing, the companion client (`CompanionApp`, `ContributionHandler`), and the `Packaging` and `Wire` namespaces for hosts and tools. One package, one `using`. |
| `VentanaTools.Orbit.Extensions.Testing` | The test kit: recording sessions, an in-memory test host that speaks real frames, the contribution contract suite and conformance checks. Works with any test framework. |
| `VentanaTools.Orbit.Extensions.Tool` | `orbit-ext`, the author tool: `new`, `validate`, `pack`, `verify`, `test`, `simulate`, `run`, `link` and `schema`. |
| `VentanaTools.Orbit.Extensions.Templates` | `dotnet new` templates: `orbit-ext-action`, `orbit-ext-widget` and `orbit-ext-node`. |
| `@ventanatools/orbit-extensions` | The Node SDK, in [`node/orbit-extensions`](node/orbit-extensions). Unpublished (`"private": true`); the tool carries its tarball for new Node projects. |

All five version in lockstep. The libraries target `net10.0` (AnyCPU, AOT
compatible) and depend only on the .NET base class library.

Orbit's SDK follows the Ventana conventions
([contract §2.7](docs/design/contract-v3.md#27-ventana-conventions)): the
extension id grammar and reserved publishers, the manifest identity block and
setting rows, the diagnostics shape, the failure vocabulary, the tooling verbs,
the test-kit names and the licensing pattern. Lollipop gets a separate SDK, which
is expected to follow the same Ventana conventions; these packages do not target
it.

## Build and verify

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
and [Node.js](https://nodejs.org/) 22 or later (with npm). The scripts run on
Windows PowerShell 5.1, which every Windows PC has, and on PowerShell 7.

```powershell
git clone https://github.com/ventanatools/orbit-sdk.git
cd orbit-sdk
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify.ps1
```

With PowerShell 7, run `pwsh -NoProfile -File tools/build.ps1` and
`pwsh -NoProfile -File tools/verify.ps1` instead.

`tools/build.ps1` builds the solution and packs every package into
`artifacts/packages` with a local version, `0.1.0-dev.<UTC yyyyMMddHHmmss>`, so a
package from an earlier build can never stand in for the one just built. It first
removes the repository's packages of any other version from that folder, so a
`--prerelease` install without `--version` gets the newest build. It packs
the libraries, then the templates, then the Node SDK tarball, then the tool,
which carries the other three. It then builds the .NET samples against those
packages, restored into a fresh folder, `artifacts/consumer-packages/<run id>`.
`-RepositoryVersion` stamps the version in `Directory.Build.props` instead.

`tools/verify.ps1` checks what the build produced: repository lints (no private
residue, no product name in samples or templates, no raw private-use glyph in
the docs, SPDX headers), the library, Testing, tool and schema tests, that each
sample's resolved author library is byte for byte the one in the new package,
the samples' tests, the Node SDK's and the Photoshop sample's tests, `orbit-ext
validate`, `pack` and `verify` for every sample with the freshly packed tool,
and every template, created through `orbit-ext new` outside the repository, then
built and tested with that tool's `test` command.

The named-pipe tests, the samples and the templates need Windows. The other
tests run on any platform that runs .NET 10.

### With plain `dotnet`

The libraries and their tests need nothing but the .NET SDK:

```powershell
dotnet build VentanaTools.Orbit.Extensions.slnx -c Release
dotnet test VentanaTools.Orbit.Extensions.slnx -c Release --no-build
dotnet test VentanaTools.Orbit.Extensions.slnx -c Release --no-build --filter "Platform!=Windows"
```

The last command is the portable subset that runs on Linux and macOS. To pack by
hand, keep the build script's order, because the tool package carries the
others and refuses to pack without them:

```powershell
dotnet pack src/VentanaTools.Orbit.Extensions -c Release -o artifacts/packages
dotnet pack src/VentanaTools.Orbit.Extensions.Testing -c Release -o artifacts/packages
dotnet pack templates/VentanaTools.Orbit.Extensions.Templates.csproj -c Release -o artifacts/packages
npm pack ./node/orbit-extensions --pack-destination artifacts/packages
dotnet pack src/VentanaTools.Orbit.Extensions.Tool -c Release -o artifacts/packages
```

## Install the tool and templates from your build

From the repository root, whose `NuGet.config` maps the package family to
`artifacts/packages`, install the tool into a folder of your choice and,
optionally, the templates into a template cache of their own. Then create a
project outside the repository: inside it, the repository's build settings and
central package versions would apply to the project:

```powershell
dotnet tool install VentanaTools.Orbit.Extensions.Tool --tool-path artifacts/tools --version <version>
dotnet new install "$PWD\artifacts\packages\VentanaTools.Orbit.Extensions.Templates.<version>.nupkg" --debug:custom-hive "$PWD\artifacts\template-hive"
artifacts\tools\orbit-ext new widget -n MyWidget -o ..\MyWidget --extension-id contoso.my-widget --debug:custom-hive "$PWD\artifacts\template-hive"
cd ..\MyWidget
dotnet tool restore
dotnet orbit-ext test
```

`<version>` is the one `tools/build.ps1` printed (it is also in
`artifacts/build/state.json`). The template cache path must be absolute, as
`$PWD` makes it here: given a relative path, `dotnet new install` copies the
template pack but finds no templates in it, and still reports success. Leave out
`--debug:custom-hive` to use your normal template cache. Outside this
repository, add `--add-source <folder>` to the tool install, or, where a NuGet
configuration uses package source mapping (the .NET SDK refuses `--add-source`
there), add the folder to that configuration and map
`VentanaTools.Orbit.Extensions*` to it.

`orbit-ext new` copies the packages a new project needs into a per-user feed,
`%LOCALAPPDATA%\VentanaTools\packages\<version>\` (or the folder you pass with
`--feed`), and the project it creates restores from there, so it builds before
anything is published. See the [tool's README](src/VentanaTools.Orbit.Extensions.Tool/README.md)
for every command.

## Use the packages in your own project

Until the packages are published, restore them from your build with a
`nuget.config` beside your solution. `<clear/>` keeps every other source out, and
the source mapping sends the package family to your folder only:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="ventana-local" value="path/to/orbit-sdk/artifacts/packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="ventana-local">
      <package pattern="VentanaTools.Orbit.Extensions*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

The templates write this file for you, pointing at the feed `orbit-ext new`
prepares. The pattern `VentanaTools.Orbit.Extensions*` matches the author
package's own ID as well as `.Testing`, `.Tool` and `.Templates`.

## Repository

| Folder | What it holds |
|---|---|
| `src/VentanaTools.Orbit.Extensions` | The author package; the companion client is in `Client/` |
| `src/VentanaTools.Orbit.Extensions.Testing` | The test kit |
| `src/VentanaTools.Orbit.Extensions.Tool` | `orbit-ext` |
| `templates` | The template pack |
| `node/orbit-extensions` | The Node SDK |
| `samples/dotnet-extension` | The smallest .NET companion: shared state, one action and one widget |
| `samples/countdown-extension` | A countdown with configured actions, a live widget and tests that use the test kit |
| `samples/photoshop-extension` | A Node companion that bridges to an Adobe Photoshop UXP plugin |
| `fixtures` | The golden test vectors of the contract's Appendix A, shared by the .NET SDK, the Node SDK and the host |
| `schemas` | The JSON Schemas of the manifest, strings, package, pairing, pack and simulation files |
| `tests` | The library, Testing, tool and schema tests |
| `tools` | `build.ps1`, `verify.ps1` and the host codename script |
| `eng` | Shared package metadata and the product rename script |
| `docs` | The contract, the release status and the release notes for maintainers |

## Naming

This repository is the source of truth for Orbit's extension SDK, contract,
samples and fixtures; the Orbit app consumes it as a Git submodule, and contract
changes start here.

Wire and file-format identifiers are product-neutral. The product name appears
only in (a) the package family `VentanaTools.<Product>.Extensions*` and its
namespaces, assembly, folder and solution names, (b) the tool command and
template short names, (c) `fixtures/hosts.json` (host id, display name, package
file extension), and (d) display text. Before the first release, renaming the
product is a scripted, mechanical rename (`eng/rename-product.ps1`).

After the name vote, run `eng/rename-product.ps1 -From <Current> -To <New>` on a
clean checkout, edit `docs/design/` by hand in the same change, and add a
changelog entry ([contract §12.4](docs/design/contract-v3.md#124-renaming-a-product)).
To change only a host's id, display name or package file extension, run
`tools/Set-HostCodename.ps1 -From <current-id> -To <new-id>`; it edits the entry
in `fixtures/hosts.json` and regenerates every file derived from it.
`ProductNameConfinementTests` fails when the product name escapes its places.

## Licence and extension ownership

The licence is split by folder. The libraries, the tool, tests, fixtures and
schemas are licensed under [Apache-2.0](LICENSE), subject to the
[notices](NOTICE) and the [third-party notices](THIRD-PARTY-NOTICES.md). The
samples in `samples/` and the templates in `templates/` are licensed under
[MIT No Attribution](LICENSES/MIT-0.txt) (MIT-0), with a `LICENSE` file in each
sample folder and in the template pack, so you can copy sample code into your own
extension, open or closed source, without keeping a notice; files the templates
generate carry no header at all. Source files carry SPDX headers naming their
licence, and Ventana Tools LLC is the copyright holder. Orbit and Lollipop
themselves remain proprietary, and these licences grant no rights to their
product names or logos.

Your own extension work remains yours. You choose its licence, free or paid,
open or closed. When you distribute SDK code, keep the applicable licence and
attribution notices; code copied from the samples or generated by the templates
needs none. Publishing an extension does not assign its ownership to Ventana.

See [CONTRIBUTING.md](CONTRIBUTING.md) to change the SDK and
[SECURITY.md](SECURITY.md) to report a vulnerability privately.
