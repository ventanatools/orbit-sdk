# .NET state sample package

A developer sample: an on/off value kept only in the companion's memory, set by **Set sample
state** actions and shown by a passive **Sample status** widget. It needs Windows and the x64 .NET
10 Runtime (also on Windows on Arm, where the Arm64 runtime alone does not run it), no account and
no network. Install only packages from authors you trust.

- `payload/companion/` holds the built companion, `DotnetExtensionSample.exe`, with its
  `extension.json`.
- `payload/source/` holds the sample's source (MIT-0, see `payload/source/LICENSE`).
- `payload/licenses/sdk/` holds the SDK's license and notice (Apache-2.0).

After installing, turn the extension on in the host app's extension settings and save the
connection info, then start `payload/companion/DotnetExtensionSample.exe`. It finds the pairing
file in its default place (`--pairing <path>` for another one) and prints one status line per
change; Ctrl+C stops it. The pairing file is a credential: never share it. The value starts Off
each time the companion starts.
