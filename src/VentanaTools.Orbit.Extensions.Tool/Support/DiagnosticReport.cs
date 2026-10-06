// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>One diagnostic as the tool prints it: the reader's diagnostic with the file shown as the author would type it.</summary>
internal sealed class ReportedDiagnostic
{
    public required string Code { get; init; }

    public required string Path { get; init; }

    public required string Message { get; init; }

    public required DiagnosticSeverity Severity { get; init; }

    /// <summary>The file, as a display path; null for a finding that concerns no file.</summary>
    public string? File { get; init; }

    public long? Line { get; init; }

    public long? Column { get; init; }

    /// <summary>The catalog's fix, with <c>{tool}</c> and the host's name filled in; text output only.</summary>
    public string? Fix { get; init; }
}

/// <summary>What <c>pack</c> and <c>verify</c> add to their output (contract §11.1).</summary>
internal sealed class PackageSummary
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    public required string Id { get; init; }

    public required string Version { get; init; }

    public required IReadOnlyList<string> Hosts { get; init; }

    public required IReadOnlyList<string> Contributions { get; init; }

    public required int Files { get; init; }

    public required long Bytes { get; init; }
}

/// <summary>
/// Collects a command's diagnostics and writes them: in text, one MSBuild canonical line per
/// diagnostic (<c>file(line,col): severity code: message [path]</c>) followed by the catalog's fix,
/// then a summary; with <c>--json</c>, one object with the shape of contract §11.1.
/// </summary>
internal sealed class DiagnosticReport(ToolConsole console, string command, bool json, bool warningsAsErrors = false)
{
    /// <summary>The code the tool reports for its own defects (contract §11.1).</summary>
    public const string ToolInternalError = "tool.internal-error";

    private readonly List<ReportedDiagnostic> _items = [];

    public IReadOnlyList<ReportedDiagnostic> Items => _items;

    /// <summary>The display name that replaces "the host" in reason-code fixes (contract §9.2 output).</summary>
    public string? HostDisplayName { get; set; }

    /// <summary>The package <c>pack</c> wrote or <c>verify</c> read.</summary>
    public PackageSummary? Package { get; set; }

    /// <summary>Further members of the <c>--json</c> object, after <c>summary</c>.</summary>
    public Action<Utf8JsonWriter>? ExtraJson { get; set; }

    /// <summary>Further text, written before the summary line.</summary>
    public Action<TextWriter>? ExtraText { get; set; }

    public bool Json => json;

    public int Errors => _items.Count(item => item.Severity == DiagnosticSeverity.Error);

    public int Warnings => _items.Count(item => item.Severity == DiagnosticSeverity.Warning);

    /// <summary>No errors, and no warnings when they count as errors.</summary>
    public bool Ok => Errors == 0 && !(warningsAsErrors && Warnings > 0);

    /// <summary>Adds a reader's diagnostic; <paramref name="file"/> is how its file is shown.</summary>
    public void Add(Diagnostic diagnostic, string? file) => _items.Add(new ReportedDiagnostic
    {
        Code = diagnostic.Code,
        Path = diagnostic.Path,
        Message = diagnostic.Message,
        Severity = diagnostic.Severity,
        File = file,
        Line = diagnostic.Line,
        Column = diagnostic.Column,
        Fix = FixFor(diagnostic.Code),
    });

    /// <summary>Adds a diagnostic of the catalog with its fixed message and severity.</summary>
    public void Add(string code, string path, string? file, long? line = null, long? column = null) =>
        Add(DiagnosticCatalog.Create(code, path, null, line, column), file);

    /// <summary>Adds <c>tool.internal-error</c>, the tool-only code for a defect of the tool itself.</summary>
    public void AddToolInternalError() => _items.Add(new ReportedDiagnostic
    {
        Code = ToolInternalError,
        Path = string.Empty,
        Message = "The tool failed in a way it does not describe.",
        Severity = DiagnosticSeverity.Error,
        Fix = "Report it, with the command you ran.",
    });

    /// <summary>Writes the report and returns the exit code: 0 when <see cref="Ok"/>, else 1.</summary>
    public int Write() => Write(Ok ? ExitCodes.Success : ExitCodes.Failed);

    /// <summary>Writes the report and returns <paramref name="exitCode"/>.</summary>
    public int Write(int exitCode)
    {
        if (json)
        {
            console.Out.WriteLine(ToJson(exitCode == ExitCodes.Success && Ok));
            return exitCode;
        }

        foreach (var item in _items)
        {
            console.Out.WriteLine(Line(item));
            if (item.Fix is { Length: > 0 } fix)
            {
                console.Out.WriteLine("  fix: " + fix);
            }
        }

        ExtraText?.Invoke(console.Out);
        console.Out.WriteLine(ToolIdentity.CommandName + ": " + Count(Errors, "error") + ", " + Count(Warnings, "warning"));
        return exitCode;
    }

    /// <summary>One diagnostic in the MSBuild canonical form.</summary>
    public static string Line(ReportedDiagnostic item)
    {
        var builder = new StringBuilder();
        builder.Append(item.File ?? ToolIdentity.CommandName);
        if (item.Line is { } line)
        {
            builder.Append('(').Append(line.ToString(CultureInfo.InvariantCulture));
            if (item.Column is { } column)
            {
                builder.Append(',').Append(column.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append(')');
        }

        builder.Append(": ").Append(item.Severity == DiagnosticSeverity.Warning ? "warning" : "error")
            .Append(' ').Append(item.Code).Append(": ").Append(item.Message)
            .Append(" [").Append(item.Path).Append(']');
        return builder.ToString();
    }

    /// <summary>Serializes JSON with every character outside printable ASCII escaped, so the output survives any console code page.</summary>
    public static string ToAsciiJson(Action<Utf8JsonWriter> write, bool indented = true)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            write(writer);
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c > '~')
            {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private string ToJson(bool ok) => ToAsciiJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("tool", ToolIdentity.CommandName);
        writer.WriteString("version", ToolIdentity.Version);
        writer.WriteString("command", command);
        writer.WriteBoolean("ok", ok);
        writer.WriteStartArray("diagnostics");
        foreach (var item in _items)
        {
            writer.WriteStartObject();
            writer.WriteString("code", item.Code);
            writer.WriteString("path", item.Path);
            writer.WriteString("message", item.Message);
            writer.WriteString("severity", item.Severity == DiagnosticSeverity.Warning ? "Warning" : "Error");
            if (item.File is not null)
            {
                writer.WriteString("file", item.File);
            }

            if (item.Line is { } line)
            {
                writer.WriteNumber("line", line);
            }

            if (item.Column is { } column)
            {
                writer.WriteNumber("column", column);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("summary");
        writer.WriteNumber("errors", Errors);
        writer.WriteNumber("warnings", Warnings);
        writer.WriteEndObject();
        if (Package is { } package)
        {
            writer.WriteStartObject("package");
            writer.WriteString("path", package.Path);
            writer.WriteString("sha256", package.Sha256);
            writer.WriteString("id", package.Id);
            writer.WriteString("version", package.Version);
            writer.WriteStartArray("hosts");
            foreach (var host in package.Hosts)
            {
                writer.WriteStringValue(host);
            }

            writer.WriteEndArray();
            writer.WriteNumber("files", package.Files);
            writer.WriteNumber("bytes", package.Bytes);
            writer.WriteEndObject();
        }

        ExtraJson?.Invoke(writer);
        writer.WriteEndObject();
    });

    private string? FixFor(string code)
    {
        if (DiagnosticCatalog.Find(code) is { } info)
        {
            return ToolIdentity.ExpandFix(info.Fix);
        }

        if (ReasonCode.TryParse(code, out var reason) && reason.IsKnown && reason.Info.Fix is { } fix)
        {
            return HostWording(fix, HostDisplayName);
        }

        return null;
    }

    /// <summary>Renders a reason code's fix for one host, as the SDK's status lines do: "In the host, …" names it.</summary>
    public static string HostWording(string fix, string? hostDisplayName) =>
        hostDisplayName is null ? fix : fix.Replace("the host", hostDisplayName, StringComparison.Ordinal);

    private static string Count(int count, string noun) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");
}
