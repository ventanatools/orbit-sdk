# VentanaTools.Orbit.Extensions.Hosting

Run an Orbit extension companion in the .NET Generic Host. `AddCompanion`
registers your contribution handler with dependency injection and runs the
companion as a hosted service: the same file discovery, connection-info wait,
file watching, status and exit codes as `CompanionApp.RunAsync`, logged through
`ILogger` instead of written to the console, and stopped with the host on
Ctrl+C or shutdown.

Use it when your companion already is, or wants to be, a Generic Host app: its
handler needs services (an `HttpClient`, configuration, a database, your own
types), it runs other hosted services beside the companion, or its logs go where
the rest of your logs go. For a companion that is only a handler,
`CompanionApp.RunAsync` in the author package is all you need.

It is a preview, versions in lockstep with `VentanaTools.Orbit.Extensions` and
depends on exactly the same version of it. Its other dependencies are
`Microsoft.Extensions.Hosting.Abstractions` and `Microsoft.Extensions.Options`;
your application adds the host itself, `Microsoft.Extensions.Hosting`, from
nuget.org.

## Get started

No nuget.org package with this ID is official yet. Restore it the way you restore
the author package: from the folder `orbit-ext new` prepares, or from your own
build, with a `nuget.config` that maps `VentanaTools.Orbit.Extensions*` to that
folder only. A project made with `orbit-ext new` already has one, and the folder
already holds this package.

```xml
<PackageReference Include="VentanaTools.Orbit.Extensions.Hosting" Version="<the SDK's version>" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.12" />
```

Then `Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VentanaTools.Orbit.Extensions;
using VentanaTools.Orbit.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCompanion<TimeWidget>();
await builder.Build().RunAsync();

sealed class TimeWidget : ContributionHandler
{
    private readonly ILogger<TimeWidget> _logger;

    public TimeWidget(ILogger<TimeWidget> logger) => _logger = logger;

    public override async Task RunSessionAsync(Session session, CancellationToken cancellationToken)
    {
        _logger.LogInformation("A session of {ContributionId} started.", session.ContributionId);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), session.Time);
        do
        {
            var now = session.Time.GetLocalNow();
            session.SetFace(new Face
            {
                Line1 = now.ToString("t", session.UiCulture),
                Detail = now.ToString("F", session.UiCulture),
                GoodFor = TimeSpan.FromSeconds(60),
            });
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}
```

`extension.json` sits next to the program, as for any companion. Run it; it logs
where it stands (`Waiting for connection info (pairing.missing). In Orbit, choose
Save connection info.`), and connects once you save the connection info in Orbit.

## What it registers

| Call | Does |
|---|---|
| `AddCompanion<THandler>()` | Registers `THandler` as a singleton, unless the application registered it already, and runs the companion with it. Its constructor's parameters come from the container. |
| `AddCompanion<THandler>(options => …)` | The same, with options. |
| `AddCompanion(provider => handler)` | Runs the companion with the handler the factory returns, created once when the host starts. Use it for a `ContributionRouter` or a handler built by hand. |
| `AddCompanion(provider => handler, options => …)` | The same, with options. |

A process runs one companion, for one manifest: a second `AddCompanion` throws
`InvalidOperationException`. `AddCompanion` is marked for Windows, like
`CompanionApp.RunAsync`, because the connection is a named pipe; companion
projects target `net10.0-windows`, as the templates do.

## Options

| Option | Default | Meaning |
|---|---|---|
| `Arguments` | the process's command line | Where `--manifest`, `--pairing` and `--verbose` are read from, as `CompanionApp.RunAsync` reads its `args`. Other arguments, such as the host's own, are ignored. |
| `ManifestPath`, `PairingPath` | none | Override `--manifest` and `--pairing`. |
| `WatchFiles` | `true` | Wait for a missing or invalid file to change instead of stopping. |
| `StopApplicationOnExit` | `true` | When the companion stops by itself, stop the application and make the companion's exit code the process's (`Environment.ExitCode`). |
| `StatusChanged`, `HandlerFaulted` | none | Called for every status and fault, on the thread pool, as with `CompanionApp`. |
| `Client` | the contract's values | Retry delays, the handler stop timeout and the clock. |

Without a path, the companion finds `extension.json` beside the program, then in
the current folder, and the connection info in the per-user folder Orbit's
**Save connection info** writes to, exactly as `CompanionApp.RunAsync` does.

`CompanionServiceOptions` follows the .NET options pattern. Set the options in
`AddCompanion`'s delegate, with `services.Configure<CompanionServiceOptions>(…)`,
or from configuration, for example
`builder.Services.Configure<CompanionServiceOptions>(builder.Configuration.GetSection("Companion"))`;
they apply in the order you register them. Configuration sets every option but
the callbacks and the client's clock (a delay is written `00:00:05`). The options
are checked when the host starts: a `Client` option out of its range, or a
`ManifestPath` or `PairingPath` that is set but empty, stops the start with
`OptionsValidationException`, whose message names the option.

## Stopping and exit codes

When the host stops (Ctrl+C, a service stop, `StopApplication`), the companion's
connection closes, its sessions end, their handlers' tokens are cancelled, and
the hosted service completes. It never waits for a handler, only, at most 5
seconds, for status and fault callbacks still running, so a handler that ignores
its token cannot hold up the host's shutdown. Such a handler runs on until it
returns or the process exits, and is logged as `IgnoredCancellation` once
`HandlerStopTimeout` (5 seconds) has passed.

The companion stops by itself only for the reasons `CompanionApp.RunAsync` would
return: a usage error (exit code 2), an unexpected failure (1), and, with
`WatchFiles` off, a missing or invalid file (3) or a stop it cannot recover from,
such as revoked access (4). It then logs the code and, with
`StopApplicationOnExit` on, stops the application with that exit code.

## Logging

Entries go to the category `VentanaTools.Orbit.Extensions.Hosting.CompanionService`:

| Event | Level | Logged when |
|---|---|---|
| 1 `Connecting` | Debug | A connection attempt starts. |
| 2 `Connected` | Information | The companion is connected, with the host's id, version and protocol. |
| 3 `Waiting` | Information or Warning | The client waits to retry after a reason code, with the code's fix and help link: Information when the reason resolves itself, Warning when it needs someone (below). |
| 4 `Retrying` | Information | The connection closed without a reason; the client retries. |
| 5 `PairingUnusable` | Warning | No valid connection info yet (`pairing.missing` and the other `pairing.*` codes), with the fix. |
| 6 `PairingDiagnostic` | Warning | The pairing file has an error that is not a reason code. |
| 7 `Stopped` | Warning | The client stopped with a reason code it cannot retry. |
| 8 `StoppedQuietly` | Information | The client stopped because the host is stopping. |
| 10, 11 `ManifestInvalid` | Error | One error of an invalid manifest: its code, line, column and fixed message. |
| 12 `ManifestUnreadable` | Error | The manifest could not be read. |
| 13, 14 `WatchingManifest`, `WatchingPairing` | Information | The companion waits for a file to change. |
| 15 `Unmapped` | Warning | A `ContributionRouter` maps no handler for a contribution. |
| 16 `HostMessage` | Debug | The host's fixed error text, only with `--verbose`. |
| 17 `HandlerFaulted` | Error | Your handler threw, ignored cancellation, hit the session cap or returned an invalid result; with your exception. |
| 18 `CallbackFaulted` | Warning | A `StatusChanged` or `HandlerFaulted` callback threw (once). |
| 19 `Usage` | Error | `--manifest` or `--pairing` has no value. |
| 20 `Unexpected` | Error | The companion failed in a way the contract doesn't describe. |
| 21 `Exited` | Error | The companion stopped by itself, with its exit code. |

A wait is a Warning only when its reason needs someone: missing or out-of-date
connection info, revoked access, another program, differing manifests, a host
that paused the companion or a peer that broke the protocol, and any reason code
the SDK does not know. A reason that resolves itself is Information: the host is
not running or is closing, the extension is turned off, or the host reloaded the
manifest. These are the codes whose catalog fix is "None", and those for which
the client treats the host as absent; the companion connects by itself once the
host is back. So while Orbit is closed, nothing reaches the Windows event log,
which `Host.CreateApplicationBuilder` gives Warnings and above.

The client retries every few seconds while it waits. A retry that ends as the
one before it did (the same state and reason code) is not logged again, and
neither are the connection attempt and the host's message that come with it,
until the status changes: when the companion connects, or the reason changes.
The `StatusChanged` callback still receives every status.

Entries follow the SDK's logging rule: states, reason codes, fixes and help
links, and never connection info, file paths (which name the person's profile),
pipe names, setting values or face text.

## Why `net10.0` only

The SDK's packages target `net10.0` alone, with no `netstandard2.0` build. A
companion is a program of its own that brings or names its runtime, not a
library a host loads, so there is no older runtime to reach; and the SDK relies
on what .NET 10 provides: `TimeProvider` throughout, Native AOT and trimming
analysis, `LibraryImport` for the pipe's server checks, and the current
`System.Text.Json` and cryptography APIs.

## License

Apache-2.0. The package includes LICENSE, NOTICE and THIRD-PARTY-NOTICES.md.
