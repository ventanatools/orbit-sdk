// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    public void ArgumentsAndClientOptionsAreCheckedWhenTheCompanionIsAdded()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddCompanion<ClockWidget>());
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddCompanion<ClockWidget>(null!));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddCompanion((Func<IServiceProvider, IContributionHandler>)null!));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddCompanion(_ => new ContributionRouter(), null!));
        var services = new ServiceCollection();
        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddCompanion<ClockWidget>(options =>
            options.Client = new CompanionClientOptions { InitialRetryDelay = TimeSpan.Zero }));
        Assert.Empty(services);
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
    public void TheAddOnDependsOnlyOnTheBaseClassLibraryTheAuthorPackageAndExtensionsAbstractions()
    {
        // The dependency rule (contract §2.7, §9.5): an add-on may use Microsoft.Extensions abstractions, nothing else.
        var assembly = typeof(CompanionServiceOptions).Assembly;
        var family = typeof(ExtensionManifest).Assembly.GetName().Name!;
        Assert.All(assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name!.StartsWith("System", StringComparison.Ordinal) || reference.Name == family
                || Regex.IsMatch(reference.Name, @"^Microsoft\.Extensions\.[A-Za-z.]+\.Abstractions$"), reference.Name));
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
