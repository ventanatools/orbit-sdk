// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

public sealed class PublicApiTests
{
    [Fact]
    public void AuthorApiIsPortableAndContainsNoHostOrCredentials()
    {
        var assembly = typeof(CompanionClient).Assembly;
        Assert.All(assembly.GetReferencedAssemblies(), name =>
            Assert.True(name.Name!.StartsWith("System", StringComparison.Ordinal) || name.Name == "netstandard", name.Name));

        // One package: every type an author names is in the root namespace (contract §9.2).
        string[] clientTypes =
        [
            "CompanionApp", "CompanionAppOptions", "CompanionArguments", "CompanionClient", "CompanionClientOptions",
            "ConnectionState", "ContributionHandler", "ContributionRouter", "Face", "HandlerFault", "HandlerFaultedEventArgs",
            "IContributionHandler", "Invocation", "InvokeResult", "PublishResult", "Session", "StatusChangedEventArgs",
        ];
        var root = assembly.GetName().Name;
        var exported = assembly.GetExportedTypes().Where(type => type.Namespace == root).Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(clientTypes, name => Assert.Contains(name, exported));

        Assert.Equal(["ContributionId", "HostCapabilities", "Id", "IsActive", "Provides", "Settings", "Time", "UiCulture", "UiLanguage"],
            typeof(Session).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["RequestId", "Session"], typeof(Invocation).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Detail", "GoodFor", "Line1", "Line2", "Picture", "Renew", "State"],
            typeof(Face).GetProperties().Where(property => property.Name != "EqualityContract").Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal([typeof(CancellationToken)], typeof(CompanionClient).GetMethod(nameof(CompanionClient.RunAsync))!.GetParameters()
            .Select(parameter => parameter.ParameterType));
        Assert.Equal(["InvokeAsync", "RunSessionAsync"], typeof(IContributionHandler).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));

        // The secret never leaves the pairing: no public member returns it.
        Assert.DoesNotContain(typeof(Pairing).GetProperties(), property => property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || property.PropertyType == typeof(byte[]));
        Assert.DoesNotContain(typeof(StatusChangedEventArgs).GetProperties(), property => property.Name.Contains("Message", StringComparison.Ordinal));
    }

    [Fact]
    public void ProtocolHasNoApplicationDependency()
    {
        var assembly = typeof(Pairing).Assembly;
        Assert.Equal(".NETCoreApp,Version=v10.0", assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name =>
            name.Name!.StartsWith("Microsoft.Windows", StringComparison.Ordinal) || name.Name.StartsWith("WinRT", StringComparison.Ordinal)
            || name.Name.StartsWith("Microsoft.WinUI", StringComparison.Ordinal) || name.Name.StartsWith("Microsoft.WindowsAppRuntime", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePackageVersionIsStampedIntoTheAssembly()
    {
        var informational = typeof(CompanionClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var expected = typeof(PublicApiTests).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        Assert.StartsWith(expected, informational, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheTwoPipeEntryPointsAreWindowsOnly()
    {
        var assembly = typeof(CompanionClient).Assembly;
        var marked = assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(member => (Type: type, Member: member)))
            .Where(pair => pair.Member.GetCustomAttributes<SupportedOSPlatformAttribute>().Any())
            .Select(pair => pair.Type.Name + "." + pair.Member.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["CompanionApp.RunAsync", "CompanionClient.RunAsync"], marked);
        Assert.DoesNotContain(assembly.GetExportedTypes(), type => type.GetCustomAttributes<SupportedOSPlatformAttribute>().Any());
        Assert.Equal("windows", typeof(CompanionClient).GetMethod(nameof(CompanionClient.RunAsync))!.GetCustomAttribute<SupportedOSPlatformAttribute>()!.PlatformName);
    }

    [Fact]
    public void ConnectionStateStartsAtNotStartedAndEnumsStartAtOne()
    {
        using var pairing = new Pairing(TestHosts.Id, Wire.PipeNames.Create(TestHosts.Id, "test", "a8c06b3027d3fc4a", ClientHarness.RegistrationId),
            ClientHarness.RegistrationId, "example.countdown", (byte[])ClientHarness.Secret.Clone());
        Assert.Equal(ConnectionState.NotStarted, new CompanionClient(pairing, TestManifests.Countdown, new TestHandler()).State);
        foreach (var type in new[] { typeof(ConnectionState), typeof(HandlerFault), typeof(PublishResult) })
        {
            Assert.Equal(1, Enum.GetValues(type).Cast<int>().Min());
        }
    }

    [Fact]
    public void ClientOptionsAreValidated()
    {
        using var pairing = new Pairing(TestHosts.Id, Wire.PipeNames.Create(TestHosts.Id, "test", "a8c06b3027d3fc4a", ClientHarness.RegistrationId),
            ClientHarness.RegistrationId, "example.countdown", (byte[])ClientHarness.Secret.Clone());
        CompanionClientOptions[] invalid =
        [
            new() { InitialRetryDelay = TimeSpan.Zero },
            new() { MaxRetryDelay = TimeSpan.FromMilliseconds(500) },
            new() { RetryJitter = 1 },
            new() { RetryJitter = -0.1 },
            new() { HandlerStopTimeout = TimeSpan.Zero },
            new() { StableConnection = TimeSpan.FromDays(2) },
            new() { TimeProvider = null! },
        ];
        Assert.All(invalid, options => Assert.ThrowsAny<ArgumentException>(() => new CompanionClient(pairing, TestManifests.Countdown, new TestHandler(), options)));
        var broken = new ExtensionManifest
        {
            Id = "example.countdown",
            Name = "Broken",
            Description = "Broken.",
            Version = "1.0.0",
            Hosts = [TestHosts.Id],
            Contributions = [TestManifests.Contribution("other.root/action", Provides.Invoke)],
        };
        var error = Assert.Throws<ArgumentException>(() => new CompanionClient(pairing, broken, new TestHandler()));
        Assert.Contains("id.outside-namespace", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new CompanionClient(null!, TestManifests.Countdown, new TestHandler()));
    }
}
