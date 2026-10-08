// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace VentanaTools.Orbit.Extensions;

/// <summary>Whether a <see cref="Diagnostic"/> refuses the file.</summary>
public enum DiagnosticSeverity
{
    /// <summary>The file is refused.</summary>
    Error = 1,

    /// <summary>Reported, and never blocks.</summary>
    Warning = 2,
}

/// <summary>
/// One finding about a static file: a manifest, a strings file, a package, a pack
/// configuration or a pairing file. This is the Ventana diagnostics shape: a stable dotted
/// <see cref="Code"/>, a JSON Pointer <see cref="Path"/>, fixed English <see cref="Message"/>
/// text, a <see cref="Severity"/>, and where possible the line and column of the token.
/// </summary>
/// <remarks>
/// <para>
/// The message never contains a value from the file, so it is safe to show to the file's
/// author. Never parse it; key on <see cref="Code"/>. Hosts must not write paths or messages to
/// logs; they may log codes.
/// </para>
/// </remarks>
public sealed class Diagnostic
{
    /// <summary>The stable dotted code, from <see cref="DiagnosticCodes"/> or, for a pairing file, a <see cref="ReasonCode"/>.</summary>
    public required string Code { get; init; }

    /// <summary>
    /// The RFC 6901 JSON Pointer to the offending member or element of <see cref="File"/>, using
    /// the JSON member names; the empty string is the document root, and archive structure
    /// findings use the empty string.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>Fixed English text for authors. It never contains a value from the file.</summary>
    public required string Message { get; init; }

    /// <summary>Whether the finding refuses the file.</summary>
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

    /// <summary>
    /// The archive entry or file that <see cref="Path"/> refers to, for example
    /// <c>extension.json</c> or <c>strings/de-DE.json</c>; null when the reader was given a single file.
    /// </summary>
    public string? File { get; init; }

    /// <summary>The 1-based line of the token the path resolves to, when it is in a JSON file.</summary>
    public long? Line { get; init; }

    /// <summary>The 1-based UTF-8 byte column of the token the path resolves to, when it is in a JSON file.</summary>
    public long? Column { get; init; }

    /// <summary>Returns <c>"&lt;code&gt; &lt;path&gt;: &lt;message&gt;"</c>.</summary>
    public override string ToString() => Code + " " + Path + ": " + Message;
}

/// <summary>The outcome of a reader: a value when the file is valid, and every diagnostic found.</summary>
/// <typeparam name="T">The type read.</typeparam>
public sealed class ReadResult<T>
    where T : class
{
    internal ReadResult(T? value, IReadOnlyList<Diagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
        Value = diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ? null : value;
    }

    /// <summary>The value read; null whenever any diagnostic has severity <see cref="DiagnosticSeverity.Error"/>.</summary>
    public T? Value { get; }

    /// <summary>Every diagnostic, at most 200, the last of which is <c>diagnostics.truncated</c> when reporting stopped.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Whether <see cref="Value"/> is not null and no diagnostic is an error.</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    public bool Succeeded => Value is not null;
}

/// <summary>
/// Collects diagnostics with the rules of contract §4.2: at most one diagnostic per file and
/// path (the most specific code wins), and at most 200 in all.
/// </summary>
internal sealed class DiagnosticBag
{
    public const int Limit = 200;

    private readonly List<Diagnostic> _items = [];
    private readonly Dictionary<(string File, string Path), int> _index = [];

    public bool HasErrors => _items.Any(item => item.Severity == DiagnosticSeverity.Error);

    public int Count => _items.Count;

    public IEnumerable<Diagnostic> Items => _items;

    public void Add(Diagnostic diagnostic)
    {
        var key = (diagnostic.File ?? string.Empty, diagnostic.Path);
        if (_index.TryGetValue(key, out var at))
        {
            if (DiagnosticPrecedence.Rank(diagnostic.Code) < DiagnosticPrecedence.Rank(_items[at].Code))
            {
                _items[at] = diagnostic;
            }

            return;
        }

        _index.Add(key, _items.Count);
        _items.Add(diagnostic);
    }

    public void AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Add(diagnostic);
        }
    }

    /// <summary>Adds a diagnostic with the catalog's severity and message.</summary>
    public void Report(string code, string path, string? file = null, long? line = null, long? column = null) =>
        Add(DiagnosticCatalog.Create(code, path, file, line, column));

    public IReadOnlyList<Diagnostic> ToList()
    {
        if (_items.Count < Limit)
        {
            return new ReadOnlyCollection<Diagnostic>(_items.ToArray());
        }

        var kept = _items.Take(Limit - 1).ToList();
        kept.Add(DiagnosticCatalog.Create(DiagnosticCodes.DiagnosticsTruncated, string.Empty, null, null, null));
        return kept.AsReadOnly();
    }
}
