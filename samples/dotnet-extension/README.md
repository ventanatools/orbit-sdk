# .NET companion sample

This console application consumes `VentanaTools.Orbit.Extensions` **0.1.0-preview.1** as
a NuGet package. It has no source-project or `Orbit.Core` reference. Its own
`Directory.Build.props` keeps it independent of Orbit's Windows SDK/MSIX build
settings. The application targets .NET 10; its companion transport runs on Windows.

The sample owns one boolean in memory, initially Off. **Set sample state** has
an On/Off choice and an action with a live face. **Sample status** is passive,
with a Words/Number display choice. Each placement keeps independent choices,
while all placements observe this one simulated state. No other app, document,
device, registry setting or user file is changed. Restarting the process resets
the state to Off; no command is replayed after a connection loss.

This SDK and sample are public developer previews; the sample is under [MIT-0](LICENSE)
and the SDK under [Apache-2.0](../../LICENSE).
The SDK packages are built locally; they have not been published to NuGet.
The SDK's preview version is separate from this bundle's manifest version
`0.1.0`; the current manifest grammar accepts three numeric components.
Orbit remains proprietary and is not included in this repository. Store-signed
extension support is not yet verified; use an Orbit version with
**Settings > Extensions** enabled.

## Build from the local packages

Run from the repository root with the .NET 10 SDK installed:

```powershell
pwsh -File tools/build.ps1
dotnet restore samples/dotnet-extension/DotnetExtensionSample.csproj --source artifacts/packages --packages artifacts/sdk-consumer-packages
dotnet build samples/dotnet-extension/DotnetExtensionSample.csproj -c Release --no-restore
```

The one SDK package contains the declarations and the client. No package is pushed
to a registry. To prove the consumer boundary independently, copy this sample
folder to a separate directory and restore it from the absolute path of that
local package feed; retain the sample's `Directory.Build.props`. A clean build
there must work without the Orbit source tree or its build properties.

## Connect and try two configurations

1. Start an Orbit version with extension support enabled. In **Settings > Extensions**,
   turn on **Extension developer mode**, import this folder's `extension.json`, then
   explicitly enable **.NET state sample**.
   Importing the manifest does not start this program.
2. Choose **Copy connection info** and save it to a private local file outside
   the repository. Its contents are credentials; do not paste them into command
   arguments, screenshots, bug reports or logs.
3. Start the built sample, passing paths only. From the repository root:

   ```powershell
   dotnet run --project samples/dotnet-extension/DotnetExtensionSample.csproj -c Release --no-build -- --manifest "samples/dotnet-extension/extension.json" --pairing "C:\private-folder\DotnetSample.pairing.json"
   ```

   Replace the example pairing path. The console prints fixed lifecycle and
   sample-state messages, never credentials or exception details. It waits for
   the matching allowed Orbit registration and retries lost connections.
4. In the ring editor, add **Extensions > .NET state sample: Set sample state**
   twice. Set one card's **When picked** to **Turn sample on**, and the other to
   **Turn sample off**. Rename them if useful. Add **Sample status**, keeping
   **On or Off**; an optional second status placement can use **1 or 0**.
5. Summon the ring and pick the On action, then the Off action. Each action
   closes the ring before invoking. All faces should reflect the shared state
   by their next two-second renewal. The passive status is selectable but has
   no action digit and changes nothing when picked.
6. Change one placement's choice and undo it. Its sibling's choice must not
   change. Move a face-capable item to the middle with the card switch; its
   settings remain attached. Picking the middle still closes the ring or goes
   back; the middle does not invoke the action. Turning the switch off restores
   the item to a slot.
7. Press **Ctrl+C** in the companion. Sessions stop and Orbit stops treating
   their faces as fresh. Restarting the companion resets the sample state to
   Off. Disable/re-enable the registration to check revocation: the old pairing
   file no longer works, so copy fresh connection info explicitly.

Faces expire five seconds after their last accepted update. The sample renews
them every two seconds only while their sessions exist. It makes an initial
publication on each new session; picks change the state immediately in memory
and appear at the next renewal. Changing settings, undoing, removing a placement
or reconnecting retires the old session. The SDK rejects publication from retired
sessions and cancels their handlers. V2 items/settings remain local to `ring.json`
and are omitted from shared ring exports.

## Read the implementation

- `Program.cs` bounds manifest reads to 64 KiB and pairing reads to 4 KiB, checks
  actual bytes as well as file length, and clears temporary pairing buffers.
  Pairing parsing binds the credential to this extension ID and protocol 2.
- `RunSessionAsync` owns one session's lifetime. It checks cancellation,
  publishes a finite text face and awaits the next renewal. A rejected publish
  ends that handler instead of creating an unbounded retry loop.
- `InvokeAsync` accepts only the declared action and choices. It checks the
  invocation token and active session immediately before the in-memory change.
  A real adapter must do the same immediately before its external side effect.
- The SDK owns framing, mutual authentication, bounded queues, connection loss
  and reconnect. The sample supplies behavior; it does not construct wire JSON,
  inspect ring contents or request foreground/summon information.

The [SDK author guide](../../docs/extension-sdk.md)
describes the package contract. The public [developer site](https://dev.ventana.tools/orbit/get-started/)
walks through setup. The [wire protocol](../../docs/extension-protocol.md)
remains the authority for interoperability. The separate
[Photoshop sample](../photoshop-extension/README.md) demonstrates an application
adapter; its installed-host checks are distinct from this memory-only example.
