// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

public sealed class PublicApiTests
{
    [Fact]
    public void AuthorApiIsPortableAndContainsNoHostOrCredentials()
    {
        var assembly = typeof(CompanionClient).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name =>
            name.Name is "Orbit.Core" or "Microsoft.WinUI" or "Microsoft.WindowsAppRuntime");
        // One package: the companion client types sit beside the declaration and wire types.
        string[] clientTypes = ["CompanionClient", "CompanionFace", "CompanionFaceState", "CompanionFailure",
            "CompanionInvocation", "CompanionOutcome", "CompanionSession", "IContributionHandler"];
        string[] declarationTypes = ["ExtensionIds", "ExtensionJson", "ExtensionOrigin", "ExtensionPairing",
            "ExtensionText", "ExtensionWire", "ExternalExtensionAction", "ExternalExtensionChoice",
            "ExternalExtensionManifest", "ExternalExtensionManifestRead", "ExternalExtensionManifestReader",
            "ExternalExtensionPackage", "ExternalExtensionPackageDescriptor",
            "ExternalExtensionPackageDescriptorReader", "ExternalExtensionPackageFile",
            "ExternalExtensionPackageRead", "ExternalExtensionPackageReader", "ExternalExtensionSetting",
            "ExternalExtensionSettings"];
        Assert.Equal(
            clientTypes.Concat(declarationTypes).Order(StringComparer.Ordinal),
            assembly.GetExportedTypes().Select(type => type.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "ActionId", "IsActive", "SessionId", "Settings" },
            typeof(CompanionSession).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "RequestId", "Session" },
            typeof(CompanionInvocation).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "Detail", "Glyph", "GoodForSeconds", "Line1", "Line2", "State" },
            typeof(CompanionFace).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { typeof(ExtensionPairing), typeof(ExternalExtensionManifest), typeof(IContributionHandler), typeof(CancellationToken) },
            typeof(CompanionClient).GetMethod(nameof(CompanionClient.RunAsync))!.GetParameters()
                .Select(parameter => parameter.ParameterType));
        Assert.Equal(new[] { "InvokeAsync", "RunSessionAsync" },
            typeof(IContributionHandler).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ProtocolHasNoApplicationDependency()
    {
        Assert.DoesNotContain(typeof(ExtensionPairing).Assembly.GetReferencedAssemblies(), name =>
            name.Name is "Orbit.Core" or "Orbit" or "Microsoft.WinUI");
    }
}
