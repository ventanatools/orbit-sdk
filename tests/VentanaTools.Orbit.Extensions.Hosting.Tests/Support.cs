// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Hosting.Tests;

/// <summary>The repository the tests run in, for the documents they check.</summary>
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string PathOf(string relative) => Path.Combine([Root, .. relative.Split('/')]);

    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "fixtures", "hosts.json")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test binaries.");
    }
}

/// <summary>The manifest the add-on's tests use. It names only the test host id.</summary>
internal static class Manifests
{
    public const string Host = "example-host";
    public const string Id = "example.hosted";
    public const string Widget = "example.hosted/clock";
    public const string Action = "example.hosted/greet";

    public static ExtensionManifest Hosted { get; } = new()
    {
        Id = Id,
        Name = "Hosted",
        Description = "The Generic Host add-on's tests.",
        Version = "1.0.0",
        Hosts = [Host],
        Contributions =
        [
            new Contribution { Id = Widget, Name = "Clock", Description = "Shows the time.", Glyph = "\uE121", Provides = Provides.Face },
            new Contribution { Id = Action, Name = "Greet", Description = "Says hello.", Glyph = "\uE8BD", Provides = Provides.Invoke },
        ],
    };

    public static async Task WaitForAsync(Func<bool> condition, string what, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds))
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(5);
        }
    }
}

/// <summary>A service the handler takes from the container.</summary>
internal sealed class Clock
{
    public string Now { get; init; } = "12:00";
}

/// <summary>A widget whose face comes from an injected <see cref="Clock"/>; it records how its sessions end.</summary>
internal sealed class ClockWidget : ContributionHandler
{
    private readonly Clock _clock;

    public ClockWidget(Clock clock, ILogger<ClockWidget> logger)
    {
        _clock = clock;
        Logger = logger;
    }

    public ILogger Logger { get; }

    public TaskCompletionSource SessionCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Func<Session, Task>? OnSession { get; set; }

    public override async Task RunSessionAsync(Session session, CancellationToken cancellationToken)
    {
        if (OnSession is { } run)
        {
            await run(session);
        }

        session.SetFace(new Face { Line1 = _clock.Now, Detail = "It's " + _clock.Now + ".", GoodFor = TimeSpan.FromSeconds(30) });
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            SessionCancelled.TrySetResult();
        }
    }

    public override Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken) => Task.FromResult(InvokeResult.Done);
}

/// <summary>A log entry the capturing provider recorded.</summary>
internal sealed class LogEntry
{
    public required string Category { get; init; }

    public required LogLevel Level { get; init; }

    public required EventId EventId { get; init; }

    public required string Message { get; init; }

    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    public Exception? Exception { get; init; }
}

/// <summary>Records every log entry of every category.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// The category the add-on's entries are documented under (contract §9.5), written as text, so moving or
    /// renaming the service cannot move them unnoticed.
    /// </summary>
    public const string Category = "VentanaTools.Orbit.Extensions.Hosting.CompanionService";

    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>The entries the add-on logged.</summary>
    public IReadOnlyList<LogEntry> Companion => Entries.Where(entry => entry.Category == Category).ToList();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly CapturingLoggerProvider _owner;
        private readonly string _category;

        public Logger(CapturingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
                ? pairs.Where(pair => pair.Key != "{OriginalFormat}").ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            var entry = new LogEntry
            {
                Category = _category,
                Level = logLevel,
                EventId = eventId,
                Message = formatter(state, exception),
                Values = values,
                Exception = exception,
            };
            lock (_owner._entries)
            {
                _owner._entries.Add(entry);
            }
        }
    }
}

/// <summary>A temporary folder with a manifest and, on request, a pairing file for it.</summary>
internal sealed class CompanionFiles : IDisposable
{
    public CompanionFiles()
    {
        Folder = Path.Combine(Path.GetTempPath(), "ventana-hosting-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(ManifestPath, ManifestWriter.Write(Manifests.Hosted));
    }

    public string Folder { get; }

    public string ManifestPath => Path.Combine(Folder, "extension.json");

    public string PairingPath => Path.Combine(Folder, Manifests.Id + ".pairing.json");

    public byte[] Secret { get; } = RandomNumberGenerator.GetBytes(32);

    public string RegistrationId { get; } = Guid.NewGuid().ToString("N");

    public void WritePairing()
    {
        var json = "{\n  \"pairingVersion\": 3,\n  \"mode\": \"Persistent\",\n  \"hostId\": \"" + Manifests.Host + "\",\n  \"pipeName\": \""
            + PipeNames.Create(Manifests.Host, "test", "a8c06b3027d3fc4a", RegistrationId) + "\",\n  \"registrationId\": \""
            + RegistrationId + "\",\n  \"extensionId\": \"" + Manifests.Id + "\",\n  \"secret\": \"" + Convert.ToBase64String(Secret) + "\"\n}\n";
        File.WriteAllText(PairingPath, json, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A file the companion still holds; the folder is in the temporary directory.
        }
    }
}

/// <summary>
/// A host for one in-memory connection, written against the author package's public wire API: it
/// authenticates the companion with the pairing's secret, then sends and reads frames.
/// </summary>
internal sealed class HostPeer
{
    private readonly Stream _stream;
    private readonly byte[] _secret;

    public HostPeer(Stream stream, byte[] secret)
    {
        _stream = stream;
        _secret = secret;
    }

    public static Func<Stream, CancellationToken, ValueTask<bool>> Accepting(Channel<Stream> connections) =>
        (stream, _) => ValueTask.FromResult(connections.Writer.TryWrite(stream));

    public static async Task<HostPeer> AcceptAsync(Channel<Stream> connections, byte[] secret, CancellationToken cancellationToken = default) =>
        new(await connections.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken), secret);

    public async Task HandshakeAsync()
    {
        var host = await ChallengeAsync();
        await SendAsync(new ReadyMessage
        {
            Version = ProtocolVersions.Max,
            Capabilities = [],
            Host = host,
            UiLanguage = "en-US",
            Limits = HostLimits.Protocol3Defaults,
        });
    }

    /// <summary>Authenticates the companion, then refuses it with <paramref name="code"/> in place of <c>ready</c>, as a host does.</summary>
    public async Task RefuseAsync(ReasonCode code)
    {
        await ChallengeAsync();
        await SendAsync(new ErrorMessage { Code = code, Message = "Refused." });
        await WaitForCloseAsync();
    }

    /// <summary>Reads the hello, sends a challenge with the host's proof and verifies the companion's; returns the host's identity.</summary>
    private async Task<HostIdentity> ChallengeAsync()
    {
        var hello = Assert.IsType<HelloMessage>(await ReadAsync(ConnectionPhase.Handshake));
        var host = new HostIdentity { Id = Manifests.Host, Version = "1.0.0" };
        var transcript = new HandshakeTranscript
        {
            HostId = host.Id,
            HostVersion = host.Version,
            RegistrationId = hello.RegistrationId,
            ClientNonce = hello.ClientNonce,
            ServerNonce = Handshake.NewNonce(),
            Version = ProtocolVersions.Max,
            MinVersion = hello.MinVersion,
            MaxVersion = hello.MaxVersion,
            ClientCapabilities = hello.Capabilities,
            HostCapabilities = [],
            ManifestHash = hello.ManifestHash,
        };
        Assert.Equal(ManifestWriter.ComputeHash(Manifests.Hosted), hello.ManifestHash);
        await SendAsync(new ChallengeMessage
        {
            ServerNonce = transcript.ServerNonce,
            Version = transcript.Version,
            Capabilities = [],
            Host = host,
            Proof = Handshake.ComputeProof(_secret, transcript, ProofRole.Server),
        });
        var authenticate = Assert.IsType<AuthenticateMessage>(await ReadAsync(ConnectionPhase.Handshake));
        Assert.True(Handshake.VerifyProof(_secret, authenticate.Proof, transcript, ProofRole.Client));
        return host;
    }

    public Task SendAsync(WireMessage message) => Framing.WriteFrameAsync(_stream, MessageWriter.Write(message), CancellationToken.None).AsTask();

    /// <summary>Closes the connection without an <c>error</c>, as a host that goes away does.</summary>
    public ValueTask CloseAsync() => _stream.DisposeAsync();

    /// <summary>The companion's next message, or null when the companion closed the connection.</summary>
    public async Task<WireMessage?> ReadAsync(ConnectionPhase phase = ConnectionPhase.Authenticated)
    {
        var frame = await Framing.ReadFrameAsync(_stream, Framing.MaxFrameBytes, TimeProvider.System, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        if (frame is null)
        {
            return null;
        }

        var read = MessageReader.Read(frame, Sender.Companion, phase);
        Assert.Null(read.Violation);
        return read.Message;
    }

    /// <summary>Reads until a message of type <typeparamref name="T"/> arrives, answering pings.</summary>
    public async Task<T> ReadUntilAsync<T>()
        where T : WireMessage
    {
        while (true)
        {
            switch (await ReadAsync())
            {
                case T message:
                    return message;
                case PingMessage ping:
                    await SendAsync(new PongMessage { Id = ping.Id });
                    break;
                case null:
                    throw new EndOfStreamException("The companion closed the connection.");
            }
        }
    }

    /// <summary>Reads until the companion closes the connection.</summary>
    public async Task WaitForCloseAsync()
    {
        while (await ReadAsync() is not null)
        {
        }
    }
}

/// <summary>A Generic Host with the add-on and a capturing logger, as an application would build it.</summary>
internal static class TestHosts
{
    /// <summary>Client options with retries a few milliseconds apart, so a test sees many of them.</summary>
    public static CompanionClientOptions FastRetries { get; } = new()
    {
        InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        MaxRetryDelay = TimeSpan.FromMilliseconds(20),
        HostAbsentMaxRetryDelay = TimeSpan.FromMilliseconds(10),
    };

    /// <summary>A host that runs <see cref="ClockWidget"/> on the files of <paramref name="files"/>, with the options <paramref name="configure"/> sets.</summary>
    public static IHost Companion(CapturingLoggerProvider logs, CompanionFiles files, Action<CompanionServiceOptions> configure) =>
        Build(logs, services =>
        {
            services.AddSingleton(new Clock());
            services.AddCompanion<ClockWidget>(options =>
            {
                options.Arguments = [];
                options.ManifestPath = files.ManifestPath;
                options.PairingPath = files.PairingPath;
                configure(options);
            });
        });

    public static IHost Build(CapturingLoggerProvider logs, Action<IServiceCollection> services)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        services(builder.Services);
        return builder.Build();
    }

    public static CompanionService Service(IHost host) => host.Services.GetServices<IHostedService>().OfType<CompanionService>().Single();

    public static Task StoppingAsync(IHost host)
    {
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        return stopping.Task;
    }
}
