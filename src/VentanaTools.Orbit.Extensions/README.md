# VentanaTools.Orbit.Extensions

The one .NET package for Orbit extension authors: the companion client that runs
your extension's actions and widgets, and the manifest, diagnostics, pairing,
packaging and wire types it builds on. It implements extension contract
generation 3, targets plain `net10.0` (the companion's named-pipe transport runs
on Windows), is AOT- and trimming-compatible, and depends only on the .NET base
class library. It is a preview: see the release notes in the SDK repository.

## Get started

The quickest start is the tool: `orbit-ext new action` or `orbit-ext new widget`
creates a project with this package, its test kit, a manifest, a pack
configuration and a `nuget.config` that restores these packages from the folder
the tool prepares. No nuget.org package with this ID is official yet, so a
project of your own must map `VentanaTools.Orbit.Extensions*` to that folder with
`packageSourceMapping`, never to nuget.org.

A companion is a console program. This one implements an action whose
contribution, `contoso.greeter/greet`, declares one `Choice` setting:

```csharp
using VentanaTools.Orbit.Extensions;

return await CompanionApp.RunAsync(args, new Greeter());

sealed class Greeter : ContributionHandler
{
    public override Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        var greeting = invocation.Session.Settings["greeting"];
        Console.WriteLine($"{greeting}, world");
        return Task.FromResult(InvokeResult.Done);
    }
}
```

`extension.json`, next to the program:

```json
{
  "schemaVersion": 3,
  "id": "contoso.greeter",
  "name": "Greeter",
  "description": "Writes a greeting to the companion's console.",
  "version": "0.1.0",
  "hosts": ["orbit"],
  "contributions": [
    {
      "id": "contoso.greeter/greet",
      "name": "Greet",
      "description": "Writes the chosen greeting.",
      "glyph": "\uE8BD",
      "provides": ["invoke"],
      "settings": [
        {
          "id": "greeting",
          "kind": "Choice",
          "name": "Greeting",
          "default": "hello",
          "choices": [
            { "value": "hello", "name": "Hello" },
            { "value": "good-morning", "name": "Good morning" }
          ]
        }
      ]
    }
  ]
}
```

Run the program; it prints where it waits for the connection info. In Orbit,
choose **Save connection info** for the extension, and the companion connects.
A widget overrides `RunSessionAsync` instead and publishes faces with
`Session.SetFace`. The [documentation](https://dev.ventana.tools/) has the
tutorial, the guides and the API reference.

## The companion API

- `CompanionApp.RunAsync` is the whole program: it finds `extension.json` and
  the pairing file, connects, prints one status line per change, handles Ctrl+C
  and returns an exit code.
- `ContributionHandler` (or `IContributionHandler`, or a `ContributionRouter`
  that maps contribution ids to handlers) receives sessions and invocations.
- `Session` is one placement of a contribution: its settings, UI language and
  clock, and `SetFace`, `ClearFace` and `Fail` for widgets. `Face`, `FaceLine`
  and `FacePicture` describe what it shows.
- `Invocation` is one pick of an action; `InvokeResult` answers it.
- `CompanionClient` is the connection itself, for programs that host the client
  in their own process.
- `VentanaTools.Orbit.Extensions.Hosting`, a separate add-on package, runs the
  companion in the .NET Generic Host: `services.AddCompanion<THandler>()` takes
  the handler from dependency injection and logs through `ILogger`.

Test handlers with `VentanaTools.Orbit.Extensions.Testing`: a recording session
for unit tests, a real client against an in-process test host, and the
authoring-contract checks the templates run.

## For hosts and tools

- **Declarations**: `ExtensionManifest`, `Contribution`, `Setting` and the other
  manifest types; `ManifestReader` (read a file or bytes, or validate a manifest
  built in code), `ManifestWriter` (canonical JSON and the manifest hash),
  `StringsReader` for localized strings files, `ExtensionIds`, `TextRules` and the
  host registry (`HostRegistry`).
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
  limits and token buckets, for hosts, tools and implementations in other
  languages.

## Contract and conformance

The normative specification is the extension contract in the SDK repository
(`docs/design/contract-v3.md`). The shared conformance fixtures in `fixtures/`
(ids, text rules, codes, manifests, strings, pairing files, packages and wire
vectors) are run by this package's tests, the Node SDK and the host.

## License

Apache-2.0. The package includes LICENSE, NOTICE and THIRD-PARTY-NOTICES.md;
keep LICENSE and NOTICE with every copy of the library you distribute, for
example inside an extension package (the templates' pack configuration copies
them). Build it from the repository root with
`dotnet build VentanaTools.Orbit.Extensions.slnx`.
