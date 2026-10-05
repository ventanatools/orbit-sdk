# Orbit extension author guides

The public developer site is [dev.ventana.tools](https://dev.ventana.tools/orbit/).
These repository guides travel with the SDK source and describe the current
protocol-2 preview:

- [SDK and handler lifecycle](extension-sdk.md)
- [Manifest, pairing, sessions, and wire protocol](extension-protocol.md)
- [Packaging, install, enable, and update](extension-distribution.md)
- [Countdown and Photoshop examples](extension-examples.md)
- [Preview availability and remaining release work](release-status.md)

The SDK, tools, and samples are covered by [Apache-2.0](../LICENSE). Orbit
application code remains proprietary and is not included here. Authors keep
ownership of independently created extensions and choose their own license.

NuGet packages are currently built locally. Use an Orbit version with
**Settings > Extensions** enabled; real Store-signed compatibility remains
unverified. This repository supplies no application build. Companions start
manually, and the packages are developer examples requiring the runtimes listed
in their READMEs. Lollipop compatibility and a marketplace remain future work.
