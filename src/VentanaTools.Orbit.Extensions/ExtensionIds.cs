// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions;

/// <summary>Who an id belongs to: Orbit's own widgets, or another program's.</summary>
public enum ExtensionOrigin
{
    /// <summary>One of Orbit's own: an undotted root (<c>clock</c>, <c>now-playing</c>).</summary>
    FirstParty,

    /// <summary>Another program's: a dotted root (<c>contoso.status</c>).</summary>
    ThirdParty,
}

/// <summary>
/// The one grammar for the ids of widgets and their actions (platform design B2), in the
/// shape Lollipop's manifest ids have, so an author learns one rule for both products:
/// <code>
/// seg    = [a-z0-9]+ ( "-" [a-z0-9]+ )*
/// root1p = seg                     first party: clock, now-playing, weather
/// root3p = seg ( "." seg )+        third party: contoso.status
/// id     = root ( "/" seg )*       now-playing/play-pause, contoso.status/build
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// A dot is allowed only in the root, so a third party's id can never equal or sit
/// under one of Orbit's: <c>clock.x</c> is not <c>clock</c>, and no dotted root is the
/// root of <c>now-playing/play-pause</c>. Which side an id is on is read from its shape
/// (<see cref="OriginOf"/>), never from anything it claims.
/// </para>
/// <para>
/// A third party's root may not start with a reserved publisher: Orbit's, Lollipop's,
/// Ventana's, or <c>ext</c>, the word a ring file uses for a widget item. Lollipop
/// reserves its own list beside it. The public Protocol tests cover the portable
/// grammar and Orbit's reserved-publisher policy; each product must also test
/// its own list. Shared ID syntax does not establish host compatibility.
/// </para>
/// <para>
/// The codes are Lollipop's words for the same faults, so one fixture can say what each
/// case gives in both products. They are for tests and for a later manifest reader,
/// never for a person: a ring file that names a bad id reads as an item Orbit can't
/// read by the host.
/// </para>
/// </remarks>
public static class ExtensionIds
{
    /// <summary>The longest id, in characters (Lollipop's limit).</summary>
    public const int MaxLength = 128;

    /// <summary>Nothing, or nothing but white space.</summary>
    public const string Required = "id.required";

    /// <summary>A character, a segment or a slash the grammar does not allow.</summary>
    public const string Grammar = "id.grammar";

    /// <summary>Longer than <see cref="MaxLength"/>.</summary>
    public const string TooLong = "id.too-long";

    /// <summary>A third party's root must be dotted (<c>contoso.status</c>).</summary>
    public const string RootNotDotted = "id.root-not-dotted";

    /// <summary>Orbit's own roots are never dotted.</summary>
    public const string RootDotted = "id.root-dotted";

    /// <summary>A third party's root starts with a reserved publisher (<see cref="ReservedPublishers"/>).</summary>
    public const string RootReserved = "id.root-reserved";

    /// <summary>
    /// The publishers no third party may use as the first part of its root: Orbit,
    /// Lollipop and Ventana, whose names an extension could otherwise borrow, and
    /// <c>ext</c>, the word a ring file stores a widget item under.
    /// </summary>
    public static IReadOnlyList<string> ReservedPublishers { get; } = Array.AsReadOnly<string>(["orbit", "lollipop", "ventana", "ext"]);

    /// <summary>
    /// Null when <paramref name="id"/> is a valid id for <paramref name="origin"/>: a valid
    /// root, then any number of <c>/</c> and a segment. Otherwise the code that says why.
    /// Never throws.
    /// </summary>
    /// <remarks>
    /// The whole id is checked first (present, not too long), then its root, then the
    /// segments after it, and the first fault found is the answer.
    /// </remarks>
    public static string? Classify(string? id, ExtensionOrigin origin) => Classify(id, origin, ReservedPublishers);

    /// <summary>Uses a host-owned reserved publisher policy without changing the shared grammar.</summary>
    public static string? Classify(string? id, ExtensionOrigin origin, IReadOnlyCollection<string> reservedPublishers)
    {
        ArgumentNullException.ThrowIfNull(reservedPublishers);
        if (string.IsNullOrWhiteSpace(id))
        {
            return Required;
        }

        if (id.Length > MaxLength)
        {
            return TooLong;
        }

        var slash = id.IndexOf('/', StringComparison.Ordinal);
        if (slash == 0)
        {
            return Grammar;
        }

        if (slash < 0)
        {
            return ClassifyRoot(id, origin, reservedPublishers);
        }

        return ClassifyRoot(id[..slash], origin, reservedPublishers) ?? (AreSegments(id.AsSpan(slash + 1)) ? null : Grammar);
    }

    /// <summary>Whether <paramref name="id"/> is a valid id for <paramref name="origin"/> (<see cref="Classify(string?, ExtensionOrigin)"/>).</summary>
    public static bool IsValid(string? id, ExtensionOrigin origin) => Classify(id, origin) is null;

    /// <summary>Validates an id with the caller's reserved publisher policy.</summary>
    public static bool IsValid(string? id, ExtensionOrigin origin, IReadOnlyCollection<string> reservedPublishers) =>
        Classify(id, origin, reservedPublishers) is null;

    /// <summary>
    /// Whose an id is, by its shape alone: a third party's when its root holds a dot,
    /// Orbit's otherwise. Says nothing about whether the id is valid; ask
    /// <see cref="Classify(string?, ExtensionOrigin)"/> with the answer.
    /// </summary>
    public static ExtensionOrigin OriginOf(string? id) =>
        id is not null && RootOf(id).Contains('.', StringComparison.Ordinal)
            ? ExtensionOrigin.ThirdParty
            : ExtensionOrigin.FirstParty;

    /// <summary>
    /// Whether <paramref name="id"/> is a valid id of Orbit's own: undotted. Only such an
    /// id may reach a power Orbit keeps for itself, or have free text in a ring file.
    /// </summary>
    public static bool IsFirstParty(string? id) => IsValid(id, ExtensionOrigin.FirstParty);

    /// <summary>The root of an id: all of it up to its first <c>/</c>.</summary>
    public static string RootOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var slash = id.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? id : id[..slash];
    }

    /// <summary>
    /// Whether <paramref name="id"/> is <paramref name="root"/> itself or sits under it
    /// (<paramref name="root"/> and a <c>/</c>). Ordinal; the grammar is not checked.
    /// </summary>
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
    /// A segment: one or more lower-case ASCII letters and digits, with single hyphens
    /// between them and none at either end.
    /// </summary>
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

    private static string? ClassifyRoot(string root, ExtensionOrigin origin, IReadOnlyCollection<string> reservedPublishers)
    {
        var single = IsSegment(root);
        var dotted = !single && IsDotted(root);
        if (origin == ExtensionOrigin.FirstParty)
        {
            return single ? null : dotted ? RootDotted : Grammar;
        }

        if (single)
        {
            return RootNotDotted;
        }

        if (!dotted)
        {
            return Grammar;
        }

        var publisher = root[..root.IndexOf('.', StringComparison.Ordinal)];
        return reservedPublishers.Contains(publisher, StringComparer.Ordinal) ? RootReserved : null;
    }

    /// <summary>One or more segments joined by <c>/</c>.</summary>
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

    /// <summary>Two or more segments joined by dots.</summary>
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
