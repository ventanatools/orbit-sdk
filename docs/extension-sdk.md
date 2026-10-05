# .NET extension SDK preview

`Orbit.Extensions.Sdk` **0.1.0-preview.2** lets a separately started .NET companion
implement Orbit's v2 contributions without copying a sample's wire code or
referencing `Orbit.Core`. `Orbit.Extensions.Protocol` is its transitive shared
contract dependency. Both target .NET 10 without a Windows SDK or WinUI dependency;
the named-pipe client is a Windows API. The SDK, author tools, and samples in
this repository use [Apache-2.0](../LICENSE). Orbit remains a separate proprietary
application. Build these preview packages locally; NuGet publication has not
happened. The [developer site](https://dev.ventana.tools/orbit/) provides public
walkthroughs, and the [local package flow](extension-distribution.md) describes
explicit review/install/update. Companions currently start manually.
Store-signed extension compatibility remains unverified; this repository does
not supply an Orbit build.

The [protocol guide](extension-protocol.md) defines the wire contract,
capabilities, consent, and pairing requirements. The SDK, host, and independent
Photoshop companion implement one v2 contract. Action-only contributions declare `invoke` without
`face`; they still receive sessions and an empty settings map when no choices
are declared.

## Author workflow

1. **Declare the bundle.** A schema-2 manifest names a dotted publisher/bundle
   ID, numeric `major.minor.patch` package version, supported hosts and a bounded
   contribution list. Each contribution declares `invoke`, `face`, or both, plus
   closed choice settings with labels and defaults. `hosts: ["orbit"]` is the
   supported target here; adding `lollipop` does not create a compatible Lollipop
   adapter. A NuGet prerelease version is separate from the manifest's version.
2. **Implement a companion.** Reference `Orbit.Extensions.Sdk` at an exact preview
   version. Supply an `IContributionHandler`; the SDK carries the protocol and
   credentials. Keep application API calls and application permissions in the
   companion or its target-app adapter. Orbit runs no foreign code in-process.
3. **Validate locally.** Parse the bounded manifest through
   `ExternalExtensionManifestReader.Read`. Treat a missing `Manifest` as a
   validation failure. Unknown fields/capabilities and invalid choices are not
   silently accepted. The runtime also snapshots and validates the declaration.
4. **Import and allow.** In an Orbit version with extension support enabled, import
   a loose developer manifest with **Extension developer mode** enabled in
   **Settings > Extensions**, or install a reviewed `.orbitextension` package.
   Normal package install, enable, and pairing do not require Developer mode.
   Explicitly enable the registration. Import/install
   runs nothing. A same-ID v2 package update requires a higher numeric version,
   revokes the old pairing and stays off until enabled again. Existing ring items
   and settings remain. Unsupported older declarations are refused and preserved
   without an automatic migration. Confirm **Settings > Extensions** is available;
   actual Store-signed host compatibility remains an open verification gate.
5. **Pair explicitly.** Copy connection info to a private local file. Read it with
   a small byte limit and call `ExtensionPairing.Parse(bytes, expectedExtensionId,
   expectedProtocolVersion: 2)`. Dispose the pairing object when finished and
   clear temporary credential buffers. Pass only a file path on the command
   line, never the credential itself. Do not log the file or an exception that
   could contain it.
6. **Run and configure.** Call `CompanionClient.RunAsync(pairing, manifest, handler,
   cancellationToken)`. Add contributions from enabled bundles through Add >
   Extensions. The card renders native choice controls; each placement has its
   own settings and undo history. A passive contribution has no action key.
7. **Stop and revoke.** Ctrl+C should cancel the client token. Lost connections
   cancel active work; reconnect establishes fresh sessions without replaying
   invokes. Disable or Remove revokes the pairing credential. Re-enabling needs
   newly copied connection information and a restarted companion.

The ready-to-run [console sample](../samples/dotnet-extension/README.md)
demonstrates this sequence with no target application: two configured actions
and passive faces share a boolean stored only in companion memory.
The [Countdown sample](../samples/countdown-extension/README.md) adds a
monotonic live timer, configured Start/Pause/Reset and passive status. The
[example walkthroughs](extension-examples.md) compare it with the more complex
Photoshop adapter; the [distribution guide](extension-distribution.md) explains
reproducible local example packages.

## Handler lifetime and state

The public types used by a companion are in `Orbit.Extensions.Sdk` and
`Orbit.Extensions.Protocol`:

| API | Author responsibility |
|---|---|
| `IContributionHandler.RunSessionAsync(CompanionSession, CancellationToken)` | Own one session's full lifetime; publish initial state, then await updates or bounded renewal; end promptly when canceled |
| `CompanionSession.ActionId`, `Settings` | Route only declared contributions and use the validated choice snapshot, including defaults; never treat these values as arbitrary shell commands |
| `CompanionSession.SetFace`, `ClearFace`, `Fail` | Publish only for this session; false means it is retired, has no face capability, or the bounded queue could not accept the update |
| `CompanionFace` | Supply text, optional glyph/detail/second line, a fixed state and finite lifetime; the host owns layout, cleaning, accessibility and expiry |
| `IContributionHandler.InvokeAsync(CompanionInvocation, CancellationToken)` | Apply only the requested contribution's action; return Done, Refused, Failed or Unsupported |
| `CompanionInvocation.Session` | Use the captured session configuration; check cancellation and `IsActive` again immediately before an external mutation |

For example, a handler can publish a short value with an honest expiry:

```csharp
session.SetFace(new CompanionFace("On", 5)
{
    State = CompanionFaceState.On,
    Detail = "The sample's in-memory state is on."
});
```

Renew or replace that face before its five seconds expire only while the source
still knows the value is current. Use `Fail` when setup/data is unavailable or
`ClearFace` when there is nothing to show. Do not claim an indefinitely fresh
external value. The first wire slice accepts text and optional glyphs, not image
bytes, host-owned pictures, time-line records, markup or custom controls. The
host may show less text in a slot than in the middle or selected status.

Session IDs are opaque and temporary. Two placements of the same contribution
can have different choices. Settings changes, undo and reconnect create fresh
sessions; stop and dispose work associated with the retired object rather than
finding another session by action ID. Publication and invocation must never be
redirected from an old session to its replacement. Cancellation cannot undo a
side effect that already completed, so adapters should not retry a mutation
automatically after a timeout or reconnect.

Subscriptions follow saved demand, not ring visibility. There is no summon
callback, foreground-app identity, ring content or selection API in this slice.
The middle can display a face but its pick remains Close ring/Back. Trial gating
can stop sessions without sending a license value or stop reason. V2 placements
and their choices require local `ring.json` version 5 and are omitted entirely
from `.orbitrings` exchange; pairing and runtime faces are never ring data.

## Pack and verify the consumer boundary

From the repository root:

```powershell
pwsh -File tools/build.ps1
dotnet restore samples/dotnet-extension/DotnetExtensionSample.csproj --source artifacts/extension-sdk --packages artifacts/sdk-consumer-packages
dotnet build samples/dotnet-extension/DotnetExtensionSample.csproj -c Release --no-restore
```

The important boundary check is a second build outside the repository: copy
only the consumer sample, restore from the absolute local-feed path into a fresh
package directory, and build. No source-project reference, inherited Orbit
properties or private feed should be necessary. Inspect the package contents
for the intended assemblies, XML/API documentation, readme, version and license;
exclude user data, pairing files and host implementation dependencies. Local
packing does not publish the package anywhere.

Before calling a preview usable by another author, verify the packed consumer
against the actual Orbit host: authentication, defaults, independent settings,
settings changes and undo, stale-session rejection, finite expiry, clear/fail,
cancellation, reconnect without replay and revocation. Retain the independent
Node v2 protocol tests so a shared-code refactor does not become its own only
interoperability proof. Installed Photoshop/UXP, native UI, Narrator and large
text remain separate checks; a console test cannot establish those outcomes.

## Release boundary

This public source preview is a starting point for an author API. Packages are
not yet on NuGet. Store-signed host verification, outside-author feedback, and
independent authentication review remain open. Cross-host package negotiation,
richer inspectors, providers, image faces, discovery, automatic activation, and
the eventual Orbit/Lollipop marketplace are separate versioned capabilities.
Lollipop compatibility has not been established.

Authors retain ownership of independently created extensions and choose their
own extension license. Apache-2.0 applies to the SDK, tools, and sample source
we publish here; it does not license Orbit application code or grant general
rights to product trademarks. Preserve the SDK license and notices when
redistributing it. Dependency licenses remain applicable to bundled dependencies.
