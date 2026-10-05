# Orbit Extensions SDK

This .NET 10 package implements the Windows companion side of Orbit's v2
extension protocol. A separately started companion supplies configured actions,
passive live displays, or actions with live faces through `IContributionHandler`.
It depends only on the portable `Orbit.Extensions.Protocol` package and .NET.

Use `CompanionClient.RunAsync` with a validated manifest, a disposable
`ExtensionPairing`, a handler, and a cancellation token. Honor cancellation and
check `CompanionSession.IsActive` immediately before external mutations. Faces
must have finite lifetimes; requests are never replayed on reconnect. No foreign
code runs inside Orbit, and this client does not launch companion programs.

The repository's `samples/dotnet-extension` demonstrates an independent NuGet
consumer. See [the author guide](https://dev.ventana.tools/orbit/get-started/) for the full
local author workflow and remaining release boundaries.

Version `0.1.0-preview.2` is an Apache-2.0 source preview. Run
`pwsh -File tools/build.ps1` at the repository root to build local NuGet packages.
It has not been published to nuget.org. The Orbit application remains proprietary.
