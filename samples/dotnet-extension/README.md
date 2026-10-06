# .NET state sample

> **Status: developer sample.** The smallest companion: one handler, no startup code. The
> automated tests run it against the SDK's test kit; the installed host app's flows need checking
> in that app.

## What it does

One on/off value, kept only in the companion's memory and starting Off, shared by every
placement:

| Contribution | When picked | Face |
|---|---|---|
| **Set sample state** (`example.dotnet-state/set-state`) | Sets the value to the placement's **When picked** choice (on or off). | The value, and what picking sets |
| **Sample status** (`example.dotnet-state/status`) | Nothing | The value as On/Off or 1/0 (the placement's **Show** choice) |

No other app, document, device, setting or file is changed, and nothing is replayed after a lost
connection. Restarting the companion resets the value to Off.

## Run it

You need Windows, the .NET 10 SDK, the SDK's NuGet packages (`VentanaTools.Orbit.Extensions` and
`VentanaTools.Orbit.Extensions.Testing`), and a build of the host app with extension support.

1. Build and test. The folder imports none of the repository's build settings and references the
   SDK only as packages; `Directory.Build.props` chooses the package version
   (`-p:VentanaExtensionsVersion=<version>` overrides it).

   ```powershell
   dotnet test tests/DotnetExtensionSample.Tests.csproj
   ```

2. In the host app, open the extension settings, turn on developer mode, import this folder's
   `extension.json`, and turn the extension on after reviewing it. Then save the connection info:
   the host app writes the pairing file to
   `%USERPROFILE%\.ventana\pairings\<host-id>\example.dotnet-state.pairing.json`, where the
   companion finds it. The pairing file is a credential: never commit, package or share it.
3. Start the companion with `dotnet run --project DotnetExtensionSample.csproj`. It prints one
   status line per change; Ctrl+C stops it. `--pairing <path>` uses a pairing file elsewhere.
4. In the host app, add **Set sample state** twice (one turns the sample on, one off) and
   **Sample status**. Pick each action: every face changes at once.

## How it works

- **`Program.cs`** is one line: `CompanionApp.RunAsync(args, new StateHandler())`. The SDK reads
  `extension.json` and the pairing file, connects, authenticates, reconnects with backoff, and
  cleans and paces faces.
- **`StateHandler.cs`** holds the value (`SampleState`) and the handler. `RunSessionAsync`
  publishes a face, then waits for the value to change; it never republishes on a timer. The face
  sets `Renew`, so the SDK keeps it alive while the session runs. `InvokeAsync` checks the
  contribution, the setting, the cancellation token and `Session.IsActive` just before the change;
  a real adapter makes the same checks just before its own side effect.
- **`tests/`** is an xUnit project on `VentanaTools.Orbit.Extensions.Testing`: a `RecordingSession`
  checks publish-on-change, a `CompanionTestHost` drives the handler through a real client, and a
  `ContributionContractSuite` subclass checks every contribution and setting combination.

## Extend it

- Replace `SampleState` with the thing you control, and keep the change-signal pattern: publish
  when what the face shows changes, and let `Renew` keep a still face alive.
- Validate and package with the SDK's command-line tool (the `VentanaTools.Orbit.Extensions.Tool`
  package; see the repository README): `validate` checks this folder, and `pack` follows
  `extension.pack.json`, publishing the companion as one executable to `payload/companion/`.
- `dotnet publish -r win-x64 -p:PublishAot=true` builds a native executable instead (it needs the
  C++ build tools).

## Troubleshooting

- **The companion waits for a pairing file.** Save the connection info in the host app again, or
  pass `--pairing <path>`.
- **The host refused the connection.** The status line names the reason. After the manifest
  changes, import it again, turn the extension on, and save fresh connection info.
- **Restore cannot find the SDK packages.** Point NuGet at a folder or feed with the
  `VentanaTools.Orbit.Extensions` packages and pass the version you have with
  `-p:VentanaExtensionsVersion=<version>`.

The sample is [MIT-0](LICENSE); the SDK is Apache-2.0.
