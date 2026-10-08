# Countdown sample package

A developer sample: one local countdown, shared by **Countdown** actions (Start or resume, Pause,
Reset) and a passive **Countdown status** widget. It needs Windows and the x64 .NET 10 Runtime
(also on Windows on Arm, where the Arm64 runtime alone does not run it), no account and no
network. Install only packages from authors you trust.

- `payload/companion/` holds the built companion, `CountdownExtensionSample.exe`, with its
  `extension.json`.
- `payload/source/` holds the sample's source (MIT-0, see `payload/source/LICENSE`), which builds
  on its own with the .NET 10 SDK and the SDK's NuGet packages.
- `payload/licenses/sdk/` holds the SDK's license and notice (Apache-2.0).

After installing, turn the extension on in the host app's extension settings and save the
connection info, then start `payload/companion/CountdownExtensionSample.exe`. It finds the pairing
file in its default place (`--pairing <path>` for another one) and prints one status line per
change; Ctrl+C stops it. The pairing file is a credential: never share it.

Add **Countdown** with different choices and **Countdown status**. Settings belong to each
placement; all placements share one countdown, which starts Ready at 5:00 each time the companion
starts. Completion has no alarm.
