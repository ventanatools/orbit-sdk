// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>Who a transcript entry comes from.</summary>
internal enum TranscriptSource
{
    Host = 1,
    Companion = 2,
    Tool = 3,
}

/// <summary>
/// The simulation transcript (contract §11.1): one line per message and event, as text, or with
/// <c>--json</c> one compact JSON object per line. Nonces, proofs and the secret are never printed;
/// face text is shown as a host would hold it after cleaning (contract §7.7.3).
/// </summary>
internal sealed class Transcript(ToolConsole console, bool json)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    /// <summary>A message or event.</summary>
    public void Write(TranscriptSource from, string type, string? session = null, string? sessionId = null, string? request = null,
        string? requestId = null, string? detail = null, Action<Utf8JsonWriter>? extra = null)
    {
        var at = _clock.Elapsed.TotalSeconds;
        string line;
        if (json)
        {
            line = DiagnosticReport.ToAsciiJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("at", Math.Round(at, 3));
                writer.WriteString("from", from switch { TranscriptSource.Host => "Host", TranscriptSource.Companion => "Companion", _ => "Tool" });
                writer.WriteString("type", type);
                if (session is not null)
                {
                    writer.WriteString("session", session);
                }

                if (sessionId is not null)
                {
                    writer.WriteString("sessionId", sessionId);
                }

                if (request is not null)
                {
                    writer.WriteString("request", request);
                }

                if (requestId is not null)
                {
                    writer.WriteString("requestId", requestId);
                }

                if (detail is not null)
                {
                    writer.WriteString("detail", detail);
                }

                extra?.Invoke(writer);
                writer.WriteEndObject();
            }, indented: false);
        }
        else
        {
            var arrow = from switch { TranscriptSource.Host => "->", TranscriptSource.Companion => "<-", _ => "  " };
            var names = string.Join(' ', new[] { session, request }.Where(name => name is not null));
            line = "+" + at.ToString("0.000", CultureInfo.InvariantCulture) + "s " + arrow + " " + type
                + (names.Length > 0 ? " " + names : string.Empty) + (detail is { Length: > 0 } ? "  " + detail : string.Empty);
        }

        lock (_gate)
        {
            console.Out.WriteLine(line);
            console.Out.Flush();
        }
    }

    /// <summary>A note from the tool itself: listening, connected, an expectation, a failure.</summary>
    public void Note(string type, string? detail = null) => Write(TranscriptSource.Tool, type, detail: detail);
}
