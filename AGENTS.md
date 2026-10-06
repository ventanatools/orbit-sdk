# Orbit SDK contributor instructions

This public repository contains Apache-2.0 SDK components and MIT-0 examples
only. The proprietary Orbit and Lollipop apps and their Git history do not
belong here.

Read README.md and docs/release-status.md first. Keep production libraries plain
.NET 10 and AnyCPU, with no WinUI, Windows App SDK or proprietary host references.
The .NET client runs on Windows; protocol and package primitives are portable.

Use `pwsh -File tools/build.ps1` and `pwsh -File tools/verify.ps1`. No private
repository or feed is needed. Use xUnit assertions in public tests; do not add
the commercially restricted FluentAssertions 8 dependency.

Do not commit credentials, connection info, bridge files, build outputs,
node_modules, archives or private source paths. Packages are built locally;
do not claim NuGet publication, Store verification or consumer-ready Photoshop
installation until those steps have actually completed.

Naming follows the product-name rule. Wire and file-format identifiers are
product-neutral. The product name appears only in (a) the package family
`VentanaTools.<Product>.Extensions*` and its namespaces, assembly, folder and
solution names, (b) the tool command and template short names, (c)
`fixtures/hosts.json` (host id, display name, package file extension), and (d)
display text. Before the first release, renaming Orbit to Pinwheel is a
scripted, mechanical rename.

This repository is the source of truth for Orbit's extension SDK, contract,
samples and fixtures; the Orbit app consumes it. Lollipop has a separate SDK
that follows the same Ventana conventions
([contract §2.7](docs/design/contract-v3.md#27-ventana-conventions)).

Libraries, tools, tests, fixtures and schemas are Apache-2.0; samples are MIT-0.
Keep Apache-2.0 license/NOTICE metadata in packages, preserve third-party
notices, and document focused behavior changes with meaningful tests.
Application license terms and future marketplace terms are separate from this
SDK license.
