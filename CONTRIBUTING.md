# Contributing

Thank you for helping. Report problems and propose changes through this
repository's issues and pull requests; the issue forms ask for what we need. For
a vulnerability, use private reporting instead ([SECURITY.md](SECURITY.md)).
Everyone who takes part follows the [code of conduct](.github/CODE_OF_CONDUCT.md).

Never attach connection info, pairing files, bridge credentials, private
documents, logs with personal data, or proprietary app code to an issue or pull
request.

## Build and test

You need the .NET 10 SDK (`global.json` picks the feature band) and Node.js 22
or later with npm. The scripts run on Windows PowerShell 5.1 and PowerShell 7:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify.ps1
```

Run both before you open a pull request, on Windows. `verify.ps1` runs the
lints CI runs: no private residue, no product name in samples or templates, no
raw private-use glyph, and the SPDX header of every source file. For a quicker
loop on the libraries, `dotnet build VentanaTools.Orbit.Extensions.slnx -c
Release` and `dotnet test VentanaTools.Orbit.Extensions.slnx -c Release
--no-build` need only the .NET SDK; `--filter "Platform!=Windows"` runs the
portable subset that CI runs on Linux.

Keep the libraries plain .NET 10 with no dependency beyond the base class
library, and document every public member. Public tests use xUnit's own
assertions. Add focused tests for every behavior you change, especially
authentication, cancellation, session lifetime, framing and untrusted input.

## Contract changes start here

This repository is the source of truth for Orbit's extension SDK, the contract,
the samples and the fixtures; the Orbit app follows. A change to behavior the
[contract](docs/design/contract-v3.md) describes changes the contract in the same
pull request. When you find that the code and the contract disagree (an
erratum), fix whichever is wrong, say which in the pull request, and add a line
to the changelog; where the contract says a fixture is authoritative (the code
catalogs of §4.4 and §8.3), the fixture wins.

Every format change follows the versioning policy of contract §12: an
incompatible change increments the format's version, and a new optional wire
member or message is gated as §7.11 and §7.4 describe.

## Fixtures

`fixtures/` holds the golden test vectors of the contract's Appendix A, which
the .NET SDK, the Node SDK and the host all run. Every fixture is UTF-8 without a
byte order mark, with LF line endings, uses the test host id `example-host`, and
names no product, package or tool (only `hosts.json` and
`reserved-publishers.json` name products). Write private-use glyphs as escapes,
never as the raw character.

Every fixture is pinned by SHA-256 in
`tests/VentanaTools.Orbit.Extensions.Tests/FixturePins.txt`. The generated files
(`fixtures/codes/*`, `fixtures/wire/v3/enums.json`,
`fixtures/wire/v3/capabilities.json`, the `*.expected.json` files and the pins)
are rewritten by running the tests with the maintainer switch
`VENTANA_UPDATE_FIXTURES=1`:

```powershell
$env:VENTANA_UPDATE_FIXTURES = '1'
dotnet test tests/VentanaTools.Orbit.Extensions.Tests -c Release
Remove-Item Env:VENTANA_UPDATE_FIXTURES
dotnet test tests/VentanaTools.Orbit.Extensions.Tests -c Release
```

Run the first test command until a run passes (the pins are taken after the
other files are written, so the first run can record a stale pin), then the
second without the switch, which checks every pin. Review the diff: a pin should
change only for a fixture you meant to change. The Node SDK keeps byte copies of
`fixtures/hosts.json` and `fixtures/codes/*` under `node/orbit-extensions/lib/`;
copy them again when their fixtures change, and its tests compare them.

## Naming

Wire and file-format identifiers are product-neutral. The product name appears
only in (a) the package family `VentanaTools.<Product>.Extensions*` and its
namespaces, assembly, folder and solution names, (b) the tool command and
template short names, (c) `fixtures/hosts.json` (host id, display name, package
file extension), and (d) display text. Before the first release, renaming the
product is a scripted, mechanical rename (`eng/rename-product.ps1`).

`ProductNameConfinementTests` fails when C# under `src/` names a product in a
string, comment or identifier, and the verify lint does the same for samples and
templates, which say "the host app". Read host ids, display names and package
file extensions from `HostRegistry` (or the Node SDK's `hosts`), never from a
literal.

## Licences

The libraries, the tool, tests, fixtures and schemas are Apache-2.0; their
source files start with `SPDX-License-Identifier: Apache-2.0` and
`SPDX-FileCopyrightText: 2026 Ventana Tools LLC`. The samples are MIT-0, with the
same copyright line, and template content carries no header, so authors own
what the templates generate.

Contributions intentionally submitted for inclusion are licensed under
Apache-2.0, as section 5 of [LICENSE](LICENSE) describes, or under MIT-0 for files
in `samples/` and `templates/`. You keep your copyright; no assignment is
required. This concerns contributions to this repository, not extensions you
build and distribute yourself.
