# VentanaTools.Orbit.Extensions

The one .NET package for Orbit extension authors. It implements extension
contract generation 3: manifest schema 3, the diagnostics shape, package archive
version 2, pairing file version 3 and wire protocol 3. It targets plain
`net10.0`, AnyCPU, is AOT- and trimming-compatible, and depends only on the .NET
base class library.

## What is in the package

- **Declarations** (`VentanaTools.Orbit.Extensions`): `ExtensionManifest`,
  `Contribution`, `Setting` and the other manifest types; `ManifestReader`
  (read a file or bytes, or validate a manifest built in code), `ManifestWriter`
  (canonical JSON and the manifest hash), `StringsReader` for localized strings
  files, `ExtensionIds`, `TextRules` and the host registry (`HostRegistry`).
- **Diagnostics**: every reader reports findings as `Diagnostic` values with a
  stable dotted code, a JSON Pointer path, fixed English text that never echoes
  file content, a severity and, for JSON files, the line and UTF-8 byte column.
  The codes are listed in `DiagnosticCodes`.
- **Reason codes**: `ReasonCode` is an open registry of the codes that explain
  runtime events, with each known code's disposition, fix and help anchor.
- **Pairing**: `PairingReader` reads the connection info a host saves, and
  `Pairing` computes and verifies handshake proofs without ever exposing the
  secret. `PairingReader.DefaultPath` is the per-user location companions search.
- **Packaging** (`VentanaTools.Orbit.Extensions.Packaging`): `PackageReader`
  verifies package archives without extracting or running anything,
  `PackageWriter` writes deterministic archives with their
  `extension.package.json` descriptor, and `ZoneOfOrigin` carries the
  Mark-of-the-Web from a package to the files a host extracts.
- **Wire** (`VentanaTools.Orbit.Extensions.Wire`): framing, pipe names, the
  downgrade-proof handshake transcript and proofs, the capability registry, typed
  messages with a strict `MessageReader` and a canonical `MessageWriter`, host
  limits and token buckets. Hosts, tools and implementations in other languages
  use this namespace; companion authors rarely need it.

The protocol 3 companion client builds on these types in the same package.

## Contract and conformance

The normative specification is the extension contract in the SDK repository
(`docs/design/contract-v3.md`). The shared conformance fixtures in `fixtures/`
(ids, text rules, codes, manifests, strings, pairing files, packages and wire
vectors) are run by this package's tests, the Node SDK and the host.

## License and availability

This source preview is licensed under Apache-2.0, including LICENSE, NOTICE and
THIRD-PARTY-NOTICES.md in its NuGet package. Build it from the repository root
with `dotnet build VentanaTools.Orbit.Extensions.slnx`. No nuget.org package with
this ID is official yet. See the
[documentation](https://dev.ventana.tools/) for the author guide.
