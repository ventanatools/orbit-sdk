# Countdown companion sample

A small .NET 10 companion consuming the local `VentanaTools.Orbit.Extensions`
**0.1.0-preview.1** NuGet package. It owns one countdown in memory, supplies
live text faces, and needs no account or external network service. The SDK
owns authenticated Windows named pipes, pairing and reconnect.

| Card choice | Behavior |
|---|---|
| Start or resume | Start the chosen duration when Ready/Finished; resume remaining time when Paused; leave an already Running timer alone |
| Pause | Freeze remaining time; leave Ready/Paused/Finished alone |
| Reset | Prepare the chosen duration in Ready without starting |
| Fresh start or reset duration | 1, 5 or 25 minutes; editing a choice does not change the timer |

All placements share one timer and keep independent card choices. Start on a
Ready timer uses that Start card's duration, even if Reset prepared a different
duration. **Countdown status** is passive and can move into the middle. Picking
the middle still closes the ring or goes back.

The process starts Ready at 5:00. Completion shows 0:00/Finished. There is no
alarm, notification, automatic restart or timer file. It continues elapsing
with no visible ring or sessions; restarting the companion loses timer state.

## Build and test

From the repository root with .NET 10:

```powershell
pwsh -File tools/build.ps1
dotnet restore samples/countdown-extension/tests/CountdownExtensionSample.Tests.csproj --source artifacts/packages --packages artifacts/countdown-consumer-packages
dotnet build samples/countdown-extension/tests/CountdownExtensionSample.Tests.csproj -c Release --no-restore
dotnet run --project samples/countdown-extension/tests/CountdownExtensionSample.Tests.csproj -c Release --no-build
```

The independent test executable adds no test-framework packages. Its 13 checks
cover choices/defaults, monotonic timing, idempotence, pause/resume, reset,
completion, refusal, finite faces, cancellation, session retirement, rejected
publication and renewal rate headroom. Fake time and explicit gates determine
results. They do not establish native rendering or accessibility.

Copy this entire folder outside the repository, keeping `Directory.Build.props`,
and restore/build using an absolute local-feed path to verify the consumer
boundary. There is no Orbit/SDK source-project reference.

Run `pwsh -File tools/verify.ps1` from the repository root for the public SDK
and sample verification suite. The native Orbit host lives in a separate
proprietary repository; these public tests do not establish Store readiness,
native rendering, or accessibility. Verify the installed host separately using
the walkthrough below.

## Try it in Orbit

1. In an Orbit version with extension support enabled, import `extension.json` under
   **Settings > Extensions** with **Extension developer mode** on, or install the sample
   package without that toggle. Enable Countdown sample.
2. Copy connection info into a private local file outside source or packages.
3. Start the built companion, passing only the credential file's path:

   ```powershell
   dotnet run --project samples/countdown-extension/CountdownExtensionSample.csproj -c Release --no-build -- --manifest "samples/countdown-extension/extension.json" --pairing "C:\private-folder\Countdown.pairing.json"
   ```

4. Add three Countdown action placements. Configure Start, Pause and Reset;
   choose one minute for Start and Reset. Add Countdown status, optionally in
   the middle.
5. Start, pause, resume, and reset. Start while running must not restart it.
   Let one run finish. Change and undo one card choice; siblings keep theirs,
   and editing settings alone performs no timer action.
6. Ctrl+C stops the companion. Revoking access requires fresh connection info
   and an explicitly restarted companion. An Orbit reconnect within the same
   companion process retains time and never replays commands.

## Implementation and distribution

`Countdown.cs` derives elapsed time from monotonic `TimeProvider` timestamps.
Delayed publications and wall-clock changes do not alter timing. It clamps at
zero and rounds up only for m:ss display. Mutation is serialized and rechecks
cancellation and captured session activity immediately before applying a choice.

`CountdownHandler.cs` publishes initial state and normally renews once a second.
At the active-session cap, renewals slow to two seconds to leave transport
headroom. Faces expire after five seconds. Retired/canceled sessions and rejected
publication end the loop; no publication loops run without demand. Orbit undo
changes settings, not completed timer invocations.

See [local distribution](../../docs/extension-distribution.md)
to package the built runtime-dependent companion and source. `orbit-package.json`
is the descriptor staged as root `package.json`; `PACKAGE-README.md` is staged
as root `README.md`. Prerequisite .NET installation remains the user's step.

See the [SDK guide](../../docs/extension-sdk.md),
[example walkthroughs](../../docs/extension-examples.md), and
[developer site](https://dev.ventana.tools/orbit/examples/countdown/).
The sample uses [MIT-0](LICENSE) and the SDK [Apache-2.0](../../LICENSE); Orbit remains proprietary.
SDK NuGet publication and real Store-signed extension verification remain open.
Use an Orbit version with **Settings > Extensions** enabled; this repository
does not include an application build.
