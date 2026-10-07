# Orbit Extensions SDK: agent instructions

This public repository holds the Apache-2.0 SDK, tool, tests, fixtures, schemas
and contract for Orbit extensions, and MIT-0 samples and templates. The
proprietary Orbit and Lollipop apps, their source and their history do not
belong here.

Read [README.md](README.md), [docs/release-status.md](docs/release-status.md)
and the [contract](docs/design/contract-v3.md) first. The contract is normative:
code follows it, and a behavior change updates it in the same change
([CONTRIBUTING.md](CONTRIBUTING.md)).

## Build and test

- `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1`, then
  `tools/verify.ps1` the same way. Both run on Windows PowerShell 5.1 and
  PowerShell 7: keep them free of `#Requires -Version 7`, `??`, the ternary
  operator, `&&`, `ForEach-Object -Parallel` and `$PSStyle`, and keep every `.ps1`
  file ASCII (Windows PowerShell reads a file without a byte order mark as ANSI).
- For the libraries alone: `dotnet build VentanaTools.Orbit.Extensions.slnx -c
  Release` and `dotnet test VentanaTools.Orbit.Extensions.slnx -c Release
  --no-build`.
- Never install anything machine-wide: install the tool with `--tool-path`, the
  templates with `--debug:custom-hive`, and pass `--feed <folder>` to
  `orbit-ext new` in automation.

## Rules

- Libraries are plain .NET 10 (`net10.0` only, no `netstandard2.0`), AnyCPU, AOT
  compatible, with no WinUI, Windows App SDK or host reference. The author
  package and the test kit depend on nothing beyond the base class library; an
  add-on package (`.Hosting`) may also depend on `Microsoft.Extensions.*.Abstractions`
  packages, never on a concrete implementation. The named-pipe transport is
  Windows-only; everything else is portable.
- No public type is a positional record or has a primary constructor, every
  public member is documented, and `PublicAPI.Unshipped.txt` tracks the surface.
- C# enums start at 1, except `[Flags]` enums. Wire tokens cross through
  explicit maps, never `ToString()` or `Enum.Parse`.
- No log line, diagnostic message or exception message contains file content,
  setting values, face text, paths, pipe names or secrets.
- Tests use xUnit's own assertions; do not add FluentAssertions.
- Fixtures use the test host id `example-host` and name no product, package or
  tool. Write private-use glyphs as escapes, never the raw character.
- Do not commit credentials, pairing files, connection info, bridge files,
  build output, `node_modules` or archives.
- Do not claim a package publication, Store verification or a consumer-ready
  Photoshop installation before it has actually happened.

## Naming

This repository is the source of truth for Orbit's extension SDK, contract,
samples and fixtures; the Orbit app consumes it. Lollipop gets a separate SDK,
which is expected to follow the same Ventana conventions
([contract §2.7](docs/design/contract-v3.md#27-ventana-conventions)).

Wire and file-format identifiers are product-neutral. The product name appears
only in (a) the package family `VentanaTools.<Product>.Extensions*` and its
namespaces, assembly, folder and solution names, (b) the tool command and
template short names, (c) `fixtures/hosts.json` (host id, display name, package
file extension), and (d) display text. Before the first release, renaming the
product is a scripted, mechanical rename (`eng/rename-product.ps1`).

Code reads host ids, display names and package file extensions from
`HostRegistry`, never from a literal; `ProductNameConfinementTests` enforces it.

## Licences

Libraries, the tool, tests, fixtures and schemas are Apache-2.0; samples and
templates are MIT-0. Source files carry the SPDX headers of their folder
(`verify.ps1` checks them); template content carries none. Keep the licence and
notice files in packages, and preserve third-party notices.
