// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VentanaTools.Orbit.Extensions.Testing;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Hosting.Tests;

/// <summary><c>AddCompanion</c>: what it registers, and that the handler it resolves works against the test kit (contract §9.5).</summary>
public sealed class RegistrationTests
{
    [Fact]
    public async Task TheHandlerIsASingletonBuiltFromTheContainerAndItRunsOnTheTestKitsHost()
    {
        var logs = new CapturingLoggerProvider();
        using var host = TestHosts.Build(logs, services =>
        {
            services.AddSingleton(new Clock { Now = "09:41" });
            services.AddCompanion<ClockWidget>();
        });

        var handler = host.Services.GetRequiredService<ClockWidget>();
        Assert.Same(handler, host.Services.GetRequiredService<ClockWidget>());
        Assert.NotNull(handler.Logger);
        Assert.Single(host.Services.GetServices<IHostedService>().OfType<CompanionService>());

        // The same instance, on the test kit's in-memory host with real frames.
        await using var testHost = await CompanionTestHost.StartAsync(Manifests.Hosted, handler);
        var session = await testHost.StartSessionAsync(Manifests.Widget);
        var face = await testHost.WaitForFaceAsync(session, TimeSpan.FromSeconds(5));
        Assert.Equal(new TextLine { Text = "09:41" }, face.Face.Line1);
        await testHost.StopSessionAsync(session);
        await handler.SessionCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(testHost.Faults);

        // And under a recording session.
        using var recording = TestSessions.FromManifest(Manifests.Hosted, Manifests.Widget);
        var run = recording.RunAsync(new ClockWidget(new Clock { Now = "10:00" }, host.Services.GetRequiredService<ILogger<ClockWidget>>()));
        Assert.Equal(new TextLine { Text = "10:00" }, (await recording.WaitForFaceAsync(TimeSpan.FromSeconds(5))).Line1);
        recording.Stop();
        await run;
    }

    [Fact]
    public void AHandlerTheApplicationRegisteredIsKept()
    {
        var mine = new ClockWidget(new Clock(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ClockWidget>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton(mine);
        services.AddCompanion<ClockWidget>();
        using var provider = services.BuildServiceProvider();
        Assert.Same(mine, provider.GetRequiredService<ClockWidget>());
    }

    [Fact]
    public void TheFactoryRunsOnceWhenTheHostCreatesTheServiceWithTheApplicationsServices()
    {
        var calls = 0;
        IServiceProvider? seen = null;
        var logs = new CapturingLoggerProvider();
        using var host = TestHosts.Build(logs, services =>
        {
            services.AddSingleton(new Clock());
            services.AddCompanion(provider =>
            {
                calls++;
                seen = provider;
                return new ClockWidget(provider.GetRequiredService<Clock>(), provider.GetRequiredService<ILogger<ClockWidget>>());
            });
        });

        Assert.Equal(0, calls);
        _ = TestHosts.Service(host);
        _ = TestHosts.Service(host);
        Assert.Equal(1, calls);
        Assert.NotNull(seen!.GetService<Clock>());
    }

    [Fact]
    public void AProcessRunsOneCompanion()
    {
        var services = new ServiceCollection();
        services.AddCompanion<ClockWidget>();
        Assert.Throws<InvalidOperationException>(() => services.AddCompanion<ClockWidget>());
        Assert.Throws<InvalidOperationException>(() => services.AddCompanion(_ => new ContributionRouter()));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void ArgumentsAreCheckedWhenTheCompanionIsAdded()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddCompanion<ClockWidget>());
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddCompanion<ClockWidget>(null!));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddCompanion((Func<IServiceProvider, IContributionHandler>)null!));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddCompanion(_ => new ContributionRouter(), null!));
    }

    [Theory]
    [InlineData("client", "CompanionServiceOptions.Client.InitialRetryDelay: The delay must be greater than zero and at most one day.")]
    [InlineData("jitter", "CompanionServiceOptions.Client.RetryJitter: RetryJitter must be at least 0 and less than 1.")]
    [InlineData("manifest", "CompanionServiceOptions.ManifestPath is empty")]
    [InlineData("pairing", "CompanionServiceOptions.PairingPath is empty")]
    public async Task InvalidOptionsFailTheHostsStartWithAMessageThatNamesTheOption(string option, string message)
    {
        // Options are checked when the host starts (ValidateOnStart), not when the companion is added, so a value
        // set later, by Configure or configuration, is checked too. Nothing of the companion runs.
        var logs = new CapturingLoggerProvider();
        var created = 0;
        using var host = TestHosts.Build(logs, services =>
        {
            services.AddCompanion(provider =>
            {
                created++;
                return new ContributionRouter();
            });
            services.Configure<CompanionServiceOptions>(options =>
            {
                switch (option)
                {
                    case "client":
                        options.Client = new CompanionClientOptions { InitialRetryDelay = TimeSpan.Zero };
                        break;
                    case "jitter":
                        options.Client = new CompanionClientOptions { RetryJitter = 1 };
                        break;
                    case "manifest":
                        options.ManifestPath = " ";
                        break;
                    default:
                        options.PairingPath = string.Empty;
                        break;
                }
            });
        });

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(CompanionServiceOptions), error.OptionsType);
        Assert.Equal(0, created);
        Assert.Empty(logs.Companion);
    }

    [Fact]
    public async Task ConfigureAfterAddCompanionTakesEffect()
    {
        using var files = new CompanionFiles();
        var logs = new CapturingLoggerProvider();
        var codes = new ConcurrentQueue<int>();
        using var host = TestHosts.Build(logs, services =>
        {
            services.AddSingleton(new Clock());
            services.AddCompanion<ClockWidget>(options =>
            {
                options.Arguments = [];
                options.ManifestPath = files.ManifestPath;
                options.PairingPath = files.PairingPath;
                options.WatchFiles = true;
            });

            // The application's own configuration, registered later, wins: without watching, the missing pairing stops it.
            services.Configure<CompanionServiceOptions>(options =>
            {
                options.WatchFiles = false;
                options.TestExitCode = codes.Enqueue;
            });
        });
        Assert.False(host.Services.GetRequiredService<IOptions<CompanionServiceOptions>>().Value.WatchFiles);
        var stopping = TestHosts.StoppingAsync(host);
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        await stopping.WaitAsync(TimeSpan.FromSeconds(15));
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal([3], codes);
        Assert.DoesNotContain(logs.Companion, entry => entry.EventId.Id == 14);
    }

    [Fact]
    public async Task TheOptionsBindFromAConfigurationSection()
    {
        using var files = new CompanionFiles();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Companion:Arguments:0"] = "--verbose",
            ["Companion:ManifestPath"] = files.ManifestPath,
            ["Companion:PairingPath"] = files.PairingPath,
            ["Companion:WatchFiles"] = "false",
            ["Companion:StopApplicationOnExit"] = "false",
            ["Companion:Client:MaxRetryDelay"] = "00:00:10",
            ["Companion:Client:RetryJitter"] = "0.1",
        }).Build();
        var logs = new CapturingLoggerProvider();
        using var host = TestHosts.Build(logs, services =>
        {
            services.AddSingleton(new Clock());
            services.AddCompanion<ClockWidget>();
            services.Configure<CompanionServiceOptions>(configuration.GetSection("Companion"));
        });

        var options = host.Services.GetRequiredService<IOptions<CompanionServiceOptions>>().Value;
        Assert.Equal(["--verbose"], options.Arguments!);
        Assert.Equal(files.ManifestPath, options.ManifestPath);
        Assert.Equal(files.PairingPath, options.PairingPath);
        Assert.False(options.WatchFiles);
        Assert.False(options.StopApplicationOnExit);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Client!.MaxRetryDelay);
        Assert.Equal(0.1, options.Client.RetryJitter);
        Assert.Equal(TimeSpan.FromSeconds(1), options.Client.InitialRetryDelay);

        // The service runs on them: the missing pairing ends the companion with exit code 3, and the application keeps running.
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 21), "the exit code");
        Assert.Equal(3, TestHosts.Service(host).ExitCode);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void TheOptionsDefaultToCompanionAppsDefaults()
    {
        var options = new CompanionServiceOptions();
        var app = new CompanionAppOptions();
        Assert.Equal(app.WatchFiles, options.WatchFiles);
        Assert.Null(options.ManifestPath);
        Assert.Null(options.PairingPath);
        Assert.Null(options.Arguments);
        Assert.Null(options.Client);
        Assert.True(options.StopApplicationOnExit);
    }

    [Fact]
    public void TheAddOnDependsOnlyOnTheBaseClassLibraryTheAuthorPackageExtensionsAbstractionsAndOptions()
    {
        // The dependency rule (contract §2.7, §9): besides its author package, an add-on's direct dependencies are limited
        // to Microsoft.Extensions.*.Abstractions packages and Microsoft.Extensions.Options.
        var assembly = typeof(CompanionServiceOptions).Assembly;
        var family = typeof(ExtensionManifest).Assembly.GetName().Name!;
        Assert.All(assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name!.StartsWith("System", StringComparison.Ordinal) || reference.Name == family
                || Regex.IsMatch(reference.Name, @"^Microsoft\.Extensions\.[A-Za-z.]+\.Abstractions$")
                || reference.Name == "Microsoft.Extensions.Options", reference.Name));
        Assert.Contains(assembly.GetCustomAttributes<AssemblyMetadataAttribute>(), attribute => attribute.Key == "IsTrimmable" && attribute.Value == "True");
        Assert.Equal([assembly.GetName().Name], assembly.GetExportedTypes().Select(type => type.Namespace!).Distinct());
        Assert.Equal(family + ".Hosting", assembly.GetName().Name);
    }

    [Fact]
    public void NoPublicTypeIsAPositionalRecordOrHasAPrimaryConstructor()
    {
        foreach (var type in typeof(CompanionServiceOptions).Assembly.GetExportedTypes())
        {
            Assert.Null(type.GetMethod("Deconstruct", BindingFlags.Public | BindingFlags.Instance));
            Assert.DoesNotContain(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance), field => Regex.IsMatch(field.Name, "^<.+>P$"));
            Assert.True(type.IsSealed, type + " is not sealed or static.");
        }
    }

    [Fact]
    public void ThePackageReadmesExampleCompiles()
    {
        // The readme is the package's page; its example must work as written.
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", typeof(CompanionServiceOptions).Assembly.GetName().Name!, "README.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        // The host version it tells applications to add is the one the add-on's abstractions come from.
        var versions = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Packages.props"));
        var abstractions = Regex.Match(versions, "<PackageVersion Include=\"Microsoft\\.Extensions\\.Hosting\\.Abstractions\" Version=\"([^\"]+)\"").Groups[1].Value;
        Assert.Contains("<PackageReference Include=\"Microsoft.Extensions.Hosting\" Version=\"" + abstractions + "\" />", readme, StringComparison.Ordinal);

        var start = readme.IndexOf("```csharp\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "The readme has no csharp block.");
        start += "```csharp\n".Length;
        var code = readme[start..readme.IndexOf("\n```", start, StringComparison.Ordinal)];

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(ExtensionManifest).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(CompanionServiceOptions).Assembly.Location));
        var implicitUsings = CSharpSyntaxTree.ParseText("global using System;\nglobal using System.Threading;\nglobal using System.Threading.Tasks;");
        var compilation = CSharpCompilation.Create("Readme", [CSharpSyntaxTree.ParseText(code), implicitUsings], references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable)
                .WithSpecificDiagnosticOptions([new("CA1416", ReportDiagnostic.Suppress)]));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .Select(diagnostic => diagnostic.ToString()));
    }

    private static string RepositoryRoot()
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
