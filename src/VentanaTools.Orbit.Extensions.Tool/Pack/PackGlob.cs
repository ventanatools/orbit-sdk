// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics.CodeAnalysis;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// One glob of a copy rule (contract §11.3), matched against paths below the copied folder.
/// </summary>
/// <remarks>
/// <para>
/// Segments are separated by <c>/</c> or <c>\</c> and match without regard to case. Within a
/// segment <c>*</c> matches any run of characters and <c>?</c> exactly one. <c>**</c> as a whole
/// segment matches any number of folders, including none; at the end of a glob it matches every
/// file below (<c>src/**</c>), and a glob that ends with a separator means the same
/// (<c>src/</c>). Leading separators, empty segments and <c>.</c> segments are ignored. As an
/// exclude glob, one that matches a folder, or ends with <c>**</c> below it, leaves out the folder
/// with everything in it (<see cref="LeavesOutFolder"/>).
/// </para>
/// <para>
/// Kept from the Microsoft.Extensions.FileSystemGlobbing rules earlier tool versions used, so a
/// glob selects what it selected before: <c>*.*</c> as a whole segment matches every name, a
/// segment that starts with <c>**.</c> matches in any folder below (<c>**.js</c> is
/// <c>**/*.js</c>), and elsewhere in a segment <c>**</c> is <c>*</c>.
/// </para>
/// <para>
/// A glob never leaves the copied folder: one with a <c>..</c> segment, or with no segment at
/// all, is refused when the pack configuration is read (<see cref="TryParse"/>).
/// </para>
/// </remarks>
internal sealed class PackGlob
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// The segments; <see langword="null"/> is <c>**</c>, any other is a name pattern. A final
    /// <c>**</c> is stored as <c>**/*</c>, since a file's own name is the last name it matches.
    /// </summary>
    private readonly string?[] _segments;

    /// <summary>Whether the glob ends with <c>**</c> (or a separator): everything below a folder.</summary>
    private readonly bool _endsWithGlobstar;

    private PackGlob(string text, List<string?> segments)
    {
        Text = text;
        _endsWithGlobstar = segments[^1] is null;
        if (_endsWithGlobstar)
        {
            segments.Add("*");
        }

        _segments = [.. segments];
    }

    /// <summary>The glob as written.</summary>
    public string Text { get; }

    /// <summary>Every file below the copied folder: the default include.</summary>
    public static PackGlob Everything { get; } = Parse("**/*");

    /// <summary>A glob known to be valid.</summary>
    public static PackGlob Parse(string text) =>
        TryParse(text, out var glob) ? glob : throw new ArgumentException("Not a valid copy rule glob: " + text, nameof(text));

    /// <summary>Reads a glob; false when it has a <c>..</c> segment or no segment at all.</summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out PackGlob? glob)
    {
        ArgumentNullException.ThrowIfNull(text);
        glob = null;
        var trimmed = text.TrimStart(Separators);
        if (trimmed.Length > 0 && Array.IndexOf(Separators, trimmed[^1]) >= 0)
        {
            trimmed = trimmed.TrimEnd(Separators) + "/**";
        }

        var segments = new List<string?>();
        foreach (var segment in trimmed.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case "..":
                    return false;
                case "**":
                    segments.Add(null);
                    continue;
            }

            if (segment.Length > 2 && segment.StartsWith("**.", StringComparison.Ordinal))
            {
                segments.Add(null);
                segments.Add(NamePattern(segment[1..]));
                continue;
            }

            segments.Add(NamePattern(segment));
        }

        if (segments.Count == 0)
        {
            return false;
        }

        glob = new PackGlob(text, segments);
        return true;
    }

    /// <summary>Whether the glob selects the file <paramref name="path"/>, the names from the copied folder down.</summary>
    public bool Matches(IReadOnlyList<string> path) => Run(path)[_segments.Length];

    /// <summary>
    /// Whether the glob could select a file below the folder <paramref name="path"/>: whether an
    /// include glob reaches into it.
    /// </summary>
    public bool CouldMatchBelow(IReadOnlyList<string> path) => Array.IndexOf(Run(path), true, 0, _segments.Length) >= 0;

    /// <summary>
    /// Whether, as an exclude glob, the glob leaves out the folder <paramref name="path"/> with
    /// everything in it: it matches the folder's path (<c>obj</c>, <c>src/*</c>), or it ends with
    /// <c>**</c> and what comes before matches the folder or a folder above it (<c>dist/**</c>).
    /// </summary>
    public bool LeavesOutFolder(IReadOnlyList<string> path)
    {
        var states = Run(path);
        return states[_segments.Length] || (_endsWithGlobstar && states[_segments.Length - 2]);
    }

    public override string ToString() => Text;

    /// <summary>
    /// The positions in the glob that <paramref name="path"/> can reach: position <c>i</c> is true
    /// when the glob's first <c>i</c> segments can match the whole path. A <c>**</c> stays where it
    /// is for each name it takes and is passed over for none.
    /// </summary>
    private bool[] Run(IReadOnlyList<string> path)
    {
        var states = new bool[_segments.Length + 1];
        states[0] = true;
        SkipGlobstars(states);
        for (var n = 0; n < path.Count; n++)
        {
            var name = path[n];
            var next = new bool[states.Length];
            for (var i = 0; i < _segments.Length; i++)
            {
                if (!states[i])
                {
                    continue;
                }

                if (_segments[i] is not { } pattern)
                {
                    next[i] = true;
                }
                else if (NameMatches(pattern, name))
                {
                    next[i + 1] = true;
                }
            }

            SkipGlobstars(next);
            states = next;
        }

        return states;
    }

    private void SkipGlobstars(bool[] states)
    {
        for (var i = 0; i < _segments.Length; i++)
        {
            if (states[i] && _segments[i] is null)
            {
                states[i + 1] = true;
            }
        }
    }

    /// <summary>Whether <paramref name="name"/> matches a segment pattern of literals, <c>*</c> and <c>?</c>, ignoring case.</summary>
    internal static bool NameMatches(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, starName = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                starName = n;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || SameLetter(pattern[p], name[n])))
            {
                p++;
                n++;
            }
            else if (star >= 0)
            {
                // Let the last * take one more character and try again from there.
                p = star + 1;
                n = ++starName;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool SameLetter(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    /// <summary>A segment's name pattern: <c>*.*</c> is <c>*</c>, and each run of <c>*</c> is written as one.</summary>
    private static string NamePattern(string segment)
    {
        if (segment == "*.*")
        {
            return "*";
        }

        if (!segment.Contains("**", StringComparison.Ordinal))
        {
            return segment;
        }

        var builder = new System.Text.StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            if (c != '*' || builder.Length == 0 || builder[^1] != '*')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
