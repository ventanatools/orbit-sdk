// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Reflection;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// The tool's own names, read from its assembly (contract §2.8, §11.1): the command name comes
/// from the <c>AssemblyMetadata</c> item the project generates from <c>ToolCommandName</c>, and the
/// sibling package IDs derive from the assembly name. No source string names the product.
/// </summary>
internal static class ToolIdentity
{
    private static readonly Assembly Assembly = typeof(ToolIdentity).Assembly;

    /// <summary>The command name, for example in the summary line, <c>--json</c> output and <c>createdBy</c>.</summary>
    public static string CommandName { get; } =
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "ToolCommandName")?.Value
        ?? throw new InvalidOperationException("The tool assembly has no command name.");

    /// <summary>The tool's version without build metadata: the version of every sibling package.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>The tool package's ID (its assembly name).</summary>
    public static string ToolPackageId { get; } = Assembly.GetName().Name!;

    /// <summary>The package family: the author package's ID, which the tool's ID extends by one segment.</summary>
    public static string PackageFamily { get; } = ToolPackageId[..ToolPackageId.LastIndexOf('.')];

    /// <summary>The author package's ID.</summary>
    public static string AuthorPackageId => PackageFamily;

    /// <summary>The test kit's package ID.</summary>
    public static string TestingPackageId => PackageFamily + ".Testing";

    /// <summary>The Generic Host add-on's package ID.</summary>
    public static string HostingPackageId => PackageFamily + ".Hosting";

    /// <summary>The template pack's package ID.</summary>
    public static string TemplatesPackageId => PackageFamily + ".Templates";

    /// <summary>The file name of the Node SDK's <c>npm pack</c> tarball of this version.</summary>
    public static string NodeTarballName => PackageFamily.ToLowerInvariant().Replace('.', '-') + "-" + Version + ".tgz";

    /// <summary>A template's short name: the command name and the kind (contract §11.4).</summary>
    public static string TemplateShortName(string kind) => CommandName + "-" + kind;

    /// <summary>Replaces the <c>{tool}</c> placeholder of the diagnostic catalog's fixes (contract §4.4).</summary>
    public static string ExpandFix(string fix) => fix.Replace("{tool}", CommandName, StringComparison.Ordinal);

    private static string ReadVersion()
    {
        var informational = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
