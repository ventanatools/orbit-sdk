// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Frozen;

namespace VentanaTools.Orbit.Extensions;

/// <summary>Whose an id is: a host's own (first party) or an extension author's (third party).</summary>
public enum IdOrigin
{
    /// <summary>A host's own id: a single segment root such as <c>clock</c> or <c>now-playing</c>.</summary>
    FirstParty = 1,

    /// <summary>An extension author's id: a dotted root such as <c>contoso.status</c>.</summary>
    ThirdParty = 2,
}

/// <summary>
/// The extension id grammar (contract §2.4, §3.4 and §3.5):
/// <code>
/// segment        = [a-z0-9]+ ( "-" [a-z0-9]+ )*
/// first party    = segment                       clock, now-playing
/// third party    = segment ( "." segment )+      contoso.status
/// id             = root ( "/" segment )*         contoso.status/build
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// A dot is allowed only in the root, so an extension author's id can never equal or sit under
/// a host's own ids. An id is at most 128 characters. Ids compare ordinally, and they are
/// claims: nothing verifies who may use a publisher segment.
/// </para>
/// <para>
/// An extension author's id must not start with a reserved publisher
/// (<see cref="ReservedPublishers"/>), and no id's first segment may be a Windows device name
/// (<c>con</c>, <c>prn</c>, <c>aux</c>, <c>nul</c>, <c>com1</c> to <c>com9</c>, <c>lpt1</c> to
/// <c>lpt9</c>), because ids begin file names. The device-name check is part of the grammar, so a
/// caller that passes its own reserved-publisher list cannot turn it off.
/// </para>
/// </remarks>
public static class ExtensionIds
{
    /// <summary>The longest id, in characters.</summary>
    public const int MaxLength = 128;

    private static readonly FrozenSet<string> DeviceNames = new[]
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The publisher segments no extension author's id may start with, from the conformance
    /// fixture <c>fixtures/reserved-publishers.json</c> (contract §3.5).
    /// </summary>
    public static IReadOnlyList<string> ReservedPublishers { get; } = EmbeddedData.ReservedPublishers();

    /// <summary>
    /// Classifies <paramref name="id"/> for <paramref name="origin"/>: a valid root, then any
    /// number of <c>/</c> and a segment.
    /// </summary>
    /// <param name="id">The id to check.</param>
    /// <param name="origin">Whose id it should be.</param>
    /// <param name="reservedPublishers">The reserved publishers; null for <see cref="ReservedPublishers"/>.</param>
    /// <returns>
    /// Null when the id is valid; otherwise the first code that applies, in this order:
    /// <c>id.required</c>, <c>id.grammar</c>, <c>id.root-not-dotted</c> or
    /// <c>id.root-dotted</c>, <c>id.root-reserved</c>, <c>id.too-long</c>.
    /// </returns>
    public static string? Classify(string? id, IdOrigin origin, IReadOnlyCollection<string>? reservedPublishers = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return DiagnosticCodes.IdRequired;
        }

        var slash = id.IndexOf('/', StringComparison.Ordinal);
        if (slash == 0)
        {
            return DiagnosticCodes.IdGrammar;
        }

        var root = slash < 0 ? id.AsSpan() : id.AsSpan(0, slash);
        if (slash >= 0 && !AreSegments(id.AsSpan(slash + 1)))
        {
            return DiagnosticCodes.IdGrammar;
        }

        var single = IsSegment(root);
        var dotted = !single && IsDotted(root);
        if (!single && !dotted)
        {
            return DiagnosticCodes.IdGrammar;
        }

        if (origin == IdOrigin.FirstParty && dotted)
        {
            return DiagnosticCodes.IdRootDotted;
        }

        if (origin != IdOrigin.FirstParty && single)
        {
            return DiagnosticCodes.IdRootNotDotted;
        }

        var dot = root.IndexOf('.');
        var first = (dot < 0 ? root : root[..dot]).ToString();
        if (DeviceNames.Contains(first))
        {
            return DiagnosticCodes.IdRootReserved;
        }

        if (origin != IdOrigin.FirstParty
            && (reservedPublishers ?? ReservedPublishers).Contains(first, StringComparer.Ordinal))
        {
            return DiagnosticCodes.IdRootReserved;
        }

        return id.Length > MaxLength ? DiagnosticCodes.IdTooLong : null;
    }

    /// <summary>Whether <paramref name="id"/> is a valid id for <paramref name="origin"/> (see <see cref="Classify"/>).</summary>
    /// <param name="id">The id to check.</param>
    /// <param name="origin">Whose id it should be.</param>
    /// <param name="reservedPublishers">The reserved publishers; null for <see cref="ReservedPublishers"/>.</param>
    /// <returns>True when <see cref="Classify"/> returns null.</returns>
    public static bool IsValid(string? id, IdOrigin origin, IReadOnlyCollection<string>? reservedPublishers = null) =>
        Classify(id, origin, reservedPublishers) is null;

    /// <summary>
    /// Whose an id is, by its shape alone: an extension author's when its root holds a dot, a
    /// host's own otherwise. Says nothing about whether the id is valid.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The origin its shape implies.</returns>
    public static IdOrigin OriginOf(string? id)
    {
        if (id is null)
        {
            return IdOrigin.FirstParty;
        }

        var slash = id.IndexOf('/', StringComparison.Ordinal);
        var root = slash < 0 ? id.AsSpan() : id.AsSpan(0, slash);
        return root.Contains('.') ? IdOrigin.ThirdParty : IdOrigin.FirstParty;
    }

    /// <summary>
    /// Whether <paramref name="id"/> is <paramref name="root"/> itself or sits under it
    /// (<paramref name="root"/> and a <c>/</c>). Ordinal; the grammar is not checked.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="root">The namespace root, usually an extension id.</param>
    /// <returns>True when the id is the root or under it.</returns>
    public static bool IsInNamespace(string? id, string? root)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        return string.Equals(id, root, StringComparison.Ordinal)
            || (id.Length > root.Length + 1 && id[root.Length] == '/' && id.StartsWith(root, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a segment: one or more lowercase ASCII letters and
    /// digits, with single hyphens between them and none at either end.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>True for a segment.</returns>
    public static bool IsSegment(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        var afterHyphen = true; // so a leading hyphen is refused
        foreach (var c in value)
        {
            if (c == '-')
            {
                if (afterHyphen)
                {
                    return false;
                }

                afterHyphen = true;
            }
            else if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                afterHyphen = false;
            }
            else
            {
                return false;
            }
        }

        return !afterHyphen; // and a trailing one
    }

    private static bool AreSegments(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var range in value.Split('/'))
        {
            if (!IsSegment(value[range]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDotted(ReadOnlySpan<char> value)
    {
        var parts = 0;
        foreach (var range in value.Split('.'))
        {
            if (!IsSegment(value[range]))
            {
                return false;
            }

            parts++;
        }

        return parts >= 2;
    }
}
