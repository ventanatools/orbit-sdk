# VentanaTools.Orbit.Extensions.Testing

The test kit for Orbit extension authors. Use it from any test framework (xUnit,
NUnit, MSTest): it has no dependency on an assertion library, and every check
reports its failures as plain sentences.

It versions in lockstep with `VentanaTools.Orbit.Extensions` and depends on
exactly the same version.

## Unit-test a handler

`TestSessions` creates a `Session` with no host and no pipe. A `RecordingSession`
records what your handler publishes, as it would be sent (text cleaned and cut to
the display limits, the lifetime rounded up to whole seconds), and you stop it as
the host would.

```csharp
using VentanaTools.Orbit.Extensions;
using VentanaTools.Orbit.Extensions.Testing;

using var recording = TestSessions.FromManifest(manifest, "example.clock/time");
var run = recording.RunAsync(new TimeWidget());
// assert on recording.LastFace or recording.Publications
recording.Stop();
await run;   // an OperationCanceledException after Stop is a normal completion
```

For an action, pass `TestInvocations.Create(recording)` to your handler's
`InvokeAsync`.

## Run the real client

`CompanionTestHost` runs the real companion client against an in-process host
over an in-memory stream that carries the same frames as the named pipe. Start
sessions, invoke, cancel, disconnect, and inspect the faces, results, statuses,
faults and a transcript of every frame. Pass a `TimeProvider` to drive the
clock of both sides.

```csharp
await using var host = await CompanionTestHost.StartAsync(manifest, new TimeWidget());
var session = await host.StartSessionAsync("example.clock/time");
var face = await host.WaitForFaceAsync(session, TimeSpan.FromSeconds(5));
await host.StopSessionAsync(session);
```

## Check the contract

`ExtensionConformance.AssertAuthoringContractAsync(manifest, handler)` runs the
contract checks with their defaults. Derive from `ContributionContractSuite` to
change them. By default the checks start and stop sessions only, for every
contribution and setting combination, so your first `dotnet test` never runs your
real actions: they check that each session is accepted, that a widget publishes a
first face, that nothing is published after a session stops, that handlers end
promptly when cancelled, and that an action-only contribution never publishes a
face. List contributions in `InvokeContributions` to also run their real
invocations.

The checks drive a manual clock, so their timeouts take no real time. Handlers
that wait or measure time should use `session.Time`.

`ExtensionConformance.AssertManifestFileValid("extension.json")` checks a manifest
file the way a host does. Every `Assert…` method throws `ConformanceException`, an
`InvalidOperationException` whose `Failures` lists every problem.

## License

Apache-2.0. See LICENSE and NOTICE in the package.
