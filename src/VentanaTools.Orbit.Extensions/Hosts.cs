// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.ObjectModel;
using System.Text.Json;

namespace VentanaTools.Orbit.Extensions;

/// <summary>Whether a host-id registry entry is in use (contract §2.3).</summary>
public enum HostStatus
{
    /// <summary>A host that admits extensions under this contract.</summary>
    Active = 1,

    /// <summary>An id no third party may claim, which no host under this contract uses.</summary>
    Reserved = 2,
}

/// <summary>One entry of the host-id registry (contract §2.3).</summary>
public sealed class HostInfo
{
    /// <summary>The host id, a stable technical codename (contract §2.4).</summary>
    public required string Id { get; init; }

    /// <summary>The host's display name. Display text only.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The package file extension with its leading dot; null while the host has none.</summary>
    public string? PackageExtension { get; init; }

    /// <summary>Former ids that a renamed host still accepts. Empty in this version.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Whether the host is in use.</summary>
    public required HostStatus Status { get; init; }
}

/// <summary>
/// The host-id registry, read once from the embedded copy of <c>fixtures/hosts.json</c>, the one
/// definition of every host id, display name and package file extension (contract §2.1, §2.3).
/// </summary>
public static class HostRegistry
{
    private static readonly (IReadOnlyList<HostInfo> Known, IReadOnlyList<string> ReservedIds) Data = EmbeddedData.Hosts();

    /// <summary>Every registry entry, active or reserved.</summary>
    public static IReadOnlyList<HostInfo> Known => Data.Known;

    /// <summary>Every id no third party may claim, active or reserved, with or without an entry.</summary>
    public static IReadOnlyList<string> ReservedIds => Data.ReservedIds;

    /// <summary>The entry whose id or one of whose aliases equals <paramref name="id"/>, ordinally.</summary>
    /// <param name="id">A host id.</param>
    /// <returns>The entry, or null.</returns>
    public static HostInfo? Find(string id) => Find(id, Known);

    /// <summary>
    /// The entry of the first id in <paramref name="hostIds"/> that names an active host, by id or alias.
    /// </summary>
    /// <param name="hostIds">Host ids in order, usually a manifest's <c>hosts</c>.</param>
    /// <param name="knownHosts">The registry to search; null for <see cref="Known"/>.</param>
    /// <returns>The first active entry, or null.</returns>
    public static HostInfo? FirstActive(IEnumerable<string> hostIds, IReadOnlyList<HostInfo>? knownHosts = null)
    {
        ArgumentNullException.ThrowIfNull(hostIds);
        foreach (var id in hostIds)
        {
            if (id is not null && Find(id, knownHosts ?? Known) is { Status: HostStatus.Active } host)
            {
                return host;
            }
        }

        return null;
    }

    internal static HostInfo? Find(string? id, IReadOnlyList<HostInfo> knownHosts)
    {
        if (id is null)
        {
            return null;
        }

        foreach (var host in knownHosts)
        {
            if (host is null)
            {
                continue;
            }

            if (string.Equals(host.Id, id, StringComparison.Ordinal)
                || (host.Aliases?.Contains(id, StringComparer.Ordinal) ?? false))
            {
                return host;
            }
        }

        return null;
    }
}

/// <summary>Reads the data files the package embeds from <c>fixtures/</c>.</summary>
internal static class EmbeddedData
{
    public static IReadOnlyList<string> ReservedPublishers()
    {
        var reader = new Utf8JsonReader(Read("reserved-publishers.json"));
        var names = new List<string>();
        ReadObject(ref reader, (ref Utf8JsonReader r, string member) =>
        {
            if (member == "reservedPublishers")
            {
                names.AddRange(ReadStrings(ref r));
            }
            else
            {
                r.Skip();
            }
        });
        return new ReadOnlyCollection<string>(names);
    }

    public static (IReadOnlyList<HostInfo>, IReadOnlyList<string>) Hosts()
    {
        var reader = new Utf8JsonReader(Read("hosts.json"));
        var hosts = new List<HostInfo>();
        var reserved = new List<string>();
        ReadObject(ref reader, (ref Utf8JsonReader r, string member) =>
        {
            switch (member)
            {
                case "hosts":
                    Expect(ref r, JsonTokenType.StartArray);
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        hosts.Add(ReadHost(ref r));
                    }

                    break;
                case "reservedIds":
                    reserved.AddRange(ReadStrings(ref r));
                    break;
                default:
                    r.Skip();
                    break;
            }
        });
        return (new ReadOnlyCollection<HostInfo>(hosts), new ReadOnlyCollection<string>(reserved));
    }

    private delegate void MemberReader(ref Utf8JsonReader reader, string member);

    private static HostInfo ReadHost(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException("The embedded host registry is malformed.");
        }

        string? id = null, displayName = null, packageExtension = null;
        var status = HostStatus.Reserved;
        IReadOnlyList<string> aliases = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var member = reader.GetString()!;
            reader.Read();
            switch (member)
            {
                case "id":
                    id = reader.GetString();
                    break;
                case "displayName":
                    displayName = reader.GetString();
                    break;
                case "packageExtension":
                    packageExtension = reader.GetString();
                    break;
                case "aliases":
                    aliases = new ReadOnlyCollection<string>(ReadStringsAt(ref reader));
                    break;
                case "status":
                    status = reader.GetString() switch
                    {
                        "Active" => HostStatus.Active,
                        "Reserved" => HostStatus.Reserved,
                        _ => throw new InvalidDataException("The embedded host registry is malformed."),
                    };
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new HostInfo
        {
            Id = id ?? throw new InvalidDataException("The embedded host registry is malformed."),
            DisplayName = displayName ?? throw new InvalidDataException("The embedded host registry is malformed."),
            PackageExtension = packageExtension,
            Aliases = aliases,
            Status = status,
        };
    }

    private static void ReadObject(ref Utf8JsonReader reader, MemberReader member)
    {
        reader.Read();
        Expect(ref reader, JsonTokenType.StartObject);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!;
            reader.Read();
            member(ref reader, name);
        }
    }

    private static List<string> ReadStrings(ref Utf8JsonReader reader) => ReadStringsAt(ref reader);

    private static List<string> ReadStringsAt(ref Utf8JsonReader reader)
    {
        Expect(ref reader, JsonTokenType.StartArray);
        var values = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(reader.GetString() ?? throw new InvalidDataException("The embedded data is malformed."));
        }

        return values;
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (reader.TokenType != type)
        {
            throw new InvalidDataException("The embedded data is malformed.");
        }
    }

    private static byte[] Read(string name)
    {
        using var stream = typeof(EmbeddedData).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("The embedded data is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
