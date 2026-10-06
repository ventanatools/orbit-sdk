# Countdown companion sample

> **Status: developer sample.** The automated tests run the handler against the SDK's test kit and
> a real client over an in-memory host. Rendering, accessibility and the installed host app's
> placement flows need checking in that app.

## What it does

One countdown, kept in the companion's memory, shared by every placement:

| Contribution | When picked | Face |
|---|---|---|
| **Countdown** (`example.countdown/timer`) | **Start or resume**: starts the chosen duration when Ready or Finished, resumes the time left when Paused, and leaves a running countdown alone. **Pause**: freezes the time left while running. **Reset**: prepares the chosen duration without starting. | The time left (m:ss) and the placement's choice with the phase |
| **Countdown status** (`example.countdown/status`) | Nothing | The time left and the phase |

Each placement keeps its own **When picked** and **Duration** (1, 5 or 25 minutes); changing a
setting performs no action. The companion starts Ready at 5:00. A finished countdown shows 0:00
and Finished. There is no alarm, no notification and no saved state: restarting the companion
starts over. The sample needs no account and no network.

## Run it

You need Windows, the .NET 10 SDK, the SDK's NuGet packages (`VentanaTools.Orbit.Extensions` and
`VentanaTools.Orbit.Extensions.Testing`), and a build of the host app with extension support.

1. Build and test. The folder imports none of the repository's build settings and references the
   SDK only as packages, so it builds the same when copied elsewhere; `Directory.Build.props`
   chooses the package version (`-p:VentanaExtensionsVersion=<version>` overrides it).

   ```powershell
   dotnet test tests/CountdownExtensionSample.Tests.csproj
   ```

2. In the host app, open the extension settings, turn on developer mode, import this folder's
   `extension.json`, and turn the extension on after reviewing it. Then save the connection info:
   the host app writes the pairing file to
   `%USERPROFILE%\.ventana\pairings\<host-id>\example.countdown.pairing.json`, where the companion
   finds it. The pairing file is a credential: never commit, package or share it.
3. Start the companion:

   ```powershell
   dotnet run --project CountdownExtensionSample.csproj
   ```

   It prints one status line per change. Ctrl+C stops it. `--pairing <path>` uses a pairing file
   elsewhere, `--manifest <path>` another manifest, and `--verbose` adds the host's error text.
4. In the host app, add **Countdown** three times (Start or resume, Pause and Reset, with one
   minute) and **Countdown status**. Start, pause, resume, reset, and let one run finish.

## How it works

- **`Program.cs`** is one line: `CompanionApp.RunAsync(args, new CountdownHandler())`. The SDK
  reads `extension.json` and the pairing file, connects, authenticates, reconnects with backoff,
  cleans and paces faces, and stops sessions; there is no startup code to copy.
- **`Countdown.cs`** keeps the countdown. Elapsed time comes from `TimeProvider` timestamps, never
  from how often a face was published; nothing runs in the background. A pick that changes
  something completes `Changed`, which wakes every session.
- **`CountdownHandler.cs`** is a `ContributionHandler`. `RunSessionAsync` publishes a face, then
  waits: while the countdown runs, until the displayed second changes; otherwise, until a pick
  changes it. A still face sets `Renew`, so the SDK keeps it alive while the session runs instead
  of the handler republishing on a timer. `InvokeAsync` checks the settings, the cancellation
  token and `Session.IsActive` just before applying the pick, and answers `Done`, `Refused` or
  `Unsupported`.
- **`tests/`** is an xUnit project on `VentanaTools.Orbit.Extensions.Testing`: countdown tests on
  a `FakeTimeProvider`, a `RecordingSession` for the status face, a `CompanionTestHost` that drives
  the handler through a real client, a `ContributionContractSuite` subclass that checks every
  contribution and setting combination, and a manifest validity check.

## Extend it

- Add a contribution to `extension.json` and handle its id in `CountdownHandler` (or route ids
  with `ContributionRouter`). The contract suite picks new contributions and settings up by itself.
- Keep faces short, publish when what they show changes, and set `Renew` for faces that stay true.
- Validate the manifest and build the package with the SDK's command-line tool (the
  `VentanaTools.Orbit.Extensions.Tool` package; see the repository README). Its `validate`
  command checks this folder, and its `pack` command follows `extension.pack.json`: it publishes
  the companion as one runtime-dependent executable to `payload/companion/`, copies the source to
  `payload/source/`, and uses `PACKAGE-README.md` as the package readme.
- `CountdownExtensionSample.csproj` is AOT-compatible: `dotnet publish -r win-x64
  -p:PublishAot=true` builds a native executable instead (it needs the C++ build tools).

## Troubleshooting

- **The companion waits for a pairing file.** Save the connection info in the host app again, or
  pass `--pairing <path>`.
- **The host refused the connection.** The status line names the reason. After the manifest
  changes, import it again, turn the extension on, and save fresh connection info.
- **Restore cannot find the SDK packages.** Point NuGet at a folder or feed with the
  `VentanaTools.Orbit.Extensions` packages and pass the version you have with
  `-p:VentanaExtensionsVersion=<version>`.

The sample is [MIT-0](LICENSE); the SDK is Apache-2.0.
