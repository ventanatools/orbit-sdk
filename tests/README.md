# SDK contract tests

This project builds against the public `VentanaTools.Orbit.Extensions` project
only. It does not require Orbit's private source, a running app, or a developer
build.

Run `pwsh -File tools/build.ps1` first, then the project from the repository root:

```sh
dotnet test tests/VentanaTools.Orbit.Extensions.Tests/VentanaTools.Orbit.Extensions.Tests.csproj -c Release
```

The declaration and wire tests (the project root) run on every .NET 10 platform.
They check manifest and package validation, immutable snapshots, path traversal
and ZIP corruption rejection, bounded framing, strict messages, pairing
identity, and proof handling.
The embedded ID golden fixture is shared with the product repositories and
pins the grammar independently of their host implementations. Preserve its
exact content and update the SHA-256 pin only alongside an intentional shared
contract change; each host's reserved-publisher policy is tested separately.

The client tests (`Client/`) check the public API on every platform. Their
transport cases run on Windows, carry `[Trait("Platform", "Windows")]` so a
non-Windows run can filter them with `--filter "Platform!=Windows"`, and
explicitly report a skip elsewhere. A small independent pipe peer
authenticates the SDK client and tests rejected host messages, separate
sessions, face publication, invocation cancellation, queue limits, and shutdown.
This peer is a test fixture; it is not an implementation of the Orbit host.

One case waits for the SDK's 12-second invocation deadline. Test projects use
xUnit assertions and do not depend on FluentAssertions.
