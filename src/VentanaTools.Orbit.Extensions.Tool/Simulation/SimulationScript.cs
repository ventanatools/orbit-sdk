// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>What a simulation step does (contract §11.2).</summary>
internal enum StepKind
{
    Start = 1,
    Invoke = 2,
    Cancel = 3,
    Stop = 4,
    ExpectFace = 5,
    Disconnect = 6,
    Wait = 7,
}

/// <summary>One step of a simulation script.</summary>
internal sealed class SimulationStep
{
    public required StepKind Kind { get; init; }

    /// <summary>The step's index in the script, for messages.</summary>
    public required int Index { get; init; }

    /// <summary>The verb's value: a contribution id (start), a session or request name, a reason code (disconnect) or a duration (wait).</summary>
    public required string Target { get; init; }

    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    /// <summary>The name a start or invoke step gives its session or request (<c>as</c>).</summary>
    public string? As { get; init; }

    /// <summary>The outcome an invoke step expects (<c>expect</c>).</summary>
    public SimulatedOutcomeKind? Expect { get; init; }

    /// <summary>The failure an invoke step expects with <c>Failed</c> (<c>failure</c>).</summary>
    public Failure? Failure { get; init; }

    /// <summary>Whether an invoke step waits for its result (<c>wait</c>, default true).</summary>
    public bool WaitForResult { get; init; } = true;

    /// <summary>How long an expectFace step waits (<c>within</c>, default 2 s), or a wait step's duration.</summary>
    public TimeSpan Within { get; init; } = TimeSpan.FromSeconds(2);

    public string? Line1 { get; init; }

    public string? Line2 { get; init; }

    public FaceState? State { get; init; }
}

/// <summary>
/// Reads a simulation script: a JSON array of steps such as
/// <c>{ "start": "example.countdown/timer", "settings": { "mode": "pause" }, "as": "s1" }</c>,
/// <c>{ "invoke": "s1", "expect": "Done" }</c>, <c>{ "expectFace": "s1", "within": "2s" }</c>,
/// <c>{ "stop": "s1" }</c> and <c>{ "disconnect": "host.reloaded" }</c> (contract §11.2), with the
/// strict JSON rules and diagnostics of contract §3.1 and §4. Its schema is
/// <c>schemas/extensions/simulation.v1.json</c>.
/// </summary>
internal static class SimulationScriptReader
{
    public const int MaxBytes = 65_536;
    public const int MaxSteps = 1_000;

    private static readonly Dictionary<string, (StepKind Kind, string[] Members)> Verbs = new(StringComparer.Ordinal)
    {
        ["start"] = (StepKind.Start, ["start", "settings", "as"]),
        ["invoke"] = (StepKind.Invoke, ["invoke", "as", "expect", "failure", "wait"]),
        ["cancel"] = (StepKind.Cancel, ["cancel"]),
        ["stop"] = (StepKind.Stop, ["stop"]),
        ["expectFace"] = (StepKind.ExpectFace, ["expectFace", "within", "line1", "line2", "state"]),
        ["disconnect"] = (StepKind.Disconnect, ["disconnect"]),
        ["wait"] = (StepKind.Wait, ["wait"]),
    };

    private static readonly Dictionary<string, SimulatedOutcomeKind> Outcomes = new(StringComparer.Ordinal)
    {
        ["Done"] = SimulatedOutcomeKind.Done,
        ["Refused"] = SimulatedOutcomeKind.Refused,
        ["Failed"] = SimulatedOutcomeKind.Failed,
        ["Unsupported"] = SimulatedOutcomeKind.Unsupported,
        ["Cancelled"] = SimulatedOutcomeKind.Cancelled,
        ["TimedOut"] = SimulatedOutcomeKind.TimedOut,
    };

    private static readonly Dictionary<string, Failure> Failures = new(StringComparer.Ordinal)
    {
        ["UnsupportedInput"] = Failure.UnsupportedInput,
        ["NeedsSetup"] = Failure.NeedsSetup,
        ["Network"] = Failure.Network,
        ["NoResult"] = Failure.NoResult,
        ["AppUnavailable"] = Failure.AppUnavailable,
    };

    private static readonly Dictionary<string, FaceState> States = new(StringComparer.Ordinal)
    {
        ["None"] = FaceState.None,
        ["Playing"] = FaceState.Playing,
        ["Paused"] = FaceState.Paused,
        ["On"] = FaceState.On,
        ["Off"] = FaceState.Off,
    };

    public static ReadResult<IReadOnlyList<SimulationStep>> Read(ReadOnlySpan<byte> utf8)
    {
        var bag = new DiagnosticBag();
        var steps = ReadCore(utf8, bag);
        return new ReadResult<IReadOnlyList<SimulationStep>>(steps, bag.ToList());
    }

    /// <summary>Parses a duration such as <c>2s</c> or <c>500ms</c>.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (text is null || text.Length is < 2 or > 9)
        {
            return null;
        }

        var milliseconds = text.EndsWith("ms", StringComparison.Ordinal);
        var digits = milliseconds ? text[..^2] : text.EndsWith('s') ? text[..^1] : null;
        if (digits is not { Length: > 0 } || !digits.All(char.IsAsciiDigit)
            || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return milliseconds ? TimeSpan.FromMilliseconds(value) : TimeSpan.FromSeconds(value);
    }

    private static List<SimulationStep>? ReadCore(ReadOnlySpan<byte> utf8, DiagnosticBag bag)
    {
        if (!JsonFile.TryOpen(utf8, MaxBytes, null, bag, out var document))
        {
            return null;
        }

        var bytes = document.ToArray();
        var parse = JsonTree.Parse(bytes);
        var v = new JsonValidator(bag, bytes, 0, bytes.Length, null);
        if (parse.Root is not { } root)
        {
            v.ParseFailure(parse);
            return null;
        }

        v.Duplicates(parse);
        if (!v.Expect(root, string.Empty, JsonKind.Array))
        {
            return null;
        }

        if (root.Items!.Count > MaxSteps)
        {
            v.Report(DiagnosticCodes.ListTooLong, string.Empty, root);
            return null;
        }

        var steps = new List<SimulationStep>();
        for (var i = 0; i < root.Items.Count; i++)
        {
            if (Step(root.Items[i], i, v) is { } step)
            {
                steps.Add(step);
            }
        }

        return bag.HasErrors ? null : steps;
    }

    private static SimulationStep? Step(JsonNode node, int index, JsonValidator v)
    {
        var path = JsonPointer.Append(string.Empty, index);
        if (!v.Expect(node, path, JsonKind.Object, element: true))
        {
            return null;
        }

        var verb = node.Members!.Select(member => member.Name).FirstOrDefault(Verbs.ContainsKey);
        if (verb is null)
        {
            v.Report(DiagnosticCodes.JsonRequiredMissing, path, node);
            return null;
        }

        var (kind, members) = Verbs[verb];
        var m = v.Members(node, path, members);
        var target = v.AsString(m[verb], JsonPointer.Append(path, verb));
        if (target is null)
        {
            return null;
        }

        var valid = true;
        var step = new SimulationStep { Kind = kind, Index = index, Target = target };
        switch (kind)
        {
            case StepKind.Start:
                step = new SimulationStep
                {
                    Kind = kind,
                    Index = index,
                    Target = target,
                    Settings = Settings(m, path, v, ref valid),
                    As = v.OptionalString(m, "as", path),
                };
                break;
            case StepKind.Invoke:
                step = new SimulationStep
                {
                    Kind = kind,
                    Index = index,
                    Target = target,
                    As = v.OptionalString(m, "as", path),
                    Expect = Token(m, "expect", path, Outcomes, v, ref valid),
                    Failure = Token(m, "failure", path, Failures, v, ref valid),
                    WaitForResult = WaitFlag(m, path, v, ref valid),
                };
                break;
            case StepKind.ExpectFace:
                step = new SimulationStep
                {
                    Kind = kind,
                    Index = index,
                    Target = target,
                    Within = Duration(m, "within", path, v, ref valid) ?? TimeSpan.FromSeconds(2),
                    Line1 = v.OptionalString(m, "line1", path),
                    Line2 = v.OptionalString(m, "line2", path),
                    State = Token(m, "state", path, States, v, ref valid),
                };
                break;
            case StepKind.Disconnect:
                if (!ReasonCode.TryParse(target, out _))
                {
                    v.Report(DiagnosticCodes.EnumUndefined, JsonPointer.Append(path, verb), m[verb]);
                    valid = false;
                }

                break;
            case StepKind.Wait:
                if (ParseDuration(target) is { } duration)
                {
                    step = new SimulationStep { Kind = kind, Index = index, Target = target, Within = duration };
                }
                else
                {
                    v.Report(DiagnosticCodes.EnumUndefined, JsonPointer.Append(path, verb), m[verb]);
                    valid = false;
                }

                break;
        }

        return valid ? step : null;
    }

    private static Dictionary<string, string> Settings(Dictionary<string, JsonNode> m, string path, JsonValidator v, ref bool valid)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!m.TryGetValue("settings", out var node))
        {
            return settings;
        }

        var settingsPath = JsonPointer.Append(path, "settings");
        if (!v.Expect(node, settingsPath, JsonKind.Object))
        {
            valid = false;
            return settings;
        }

        foreach (var member in node.Members!)
        {
            if (v.AsString(member.Value, JsonPointer.Append(settingsPath, member.Name)) is { } value)
            {
                settings[member.Name] = value;
            }
            else
            {
                valid = false;
            }
        }

        return settings;
    }

    private static T? Token<T>(Dictionary<string, JsonNode> m, string name, string path, Dictionary<string, T> tokens, JsonValidator v, ref bool valid)
        where T : struct
    {
        if (v.OptionalString(m, name, path) is not { } text)
        {
            valid &= !m.ContainsKey(name);
            return null;
        }

        if (tokens.TryGetValue(text, out var token))
        {
            return token;
        }

        v.Report(DiagnosticCodes.EnumUndefined, JsonPointer.Append(path, name), m[name]);
        valid = false;
        return null;
    }

    private static TimeSpan? Duration(Dictionary<string, JsonNode> m, string name, string path, JsonValidator v, ref bool valid)
    {
        if (v.OptionalString(m, name, path) is not { } text)
        {
            valid &= !m.ContainsKey(name);
            return null;
        }

        if (ParseDuration(text) is { } duration)
        {
            return duration;
        }

        v.Report(DiagnosticCodes.EnumUndefined, JsonPointer.Append(path, name), m[name]);
        valid = false;
        return null;
    }

    /// <summary>An invoke step's <c>wait</c>: true when absent.</summary>
    private static bool WaitFlag(Dictionary<string, JsonNode> m, string path, JsonValidator v, ref bool valid)
    {
        if (!m.TryGetValue("wait", out var node) || node.Kind == JsonKind.True)
        {
            return true;
        }

        if (node.Kind == JsonKind.False)
        {
            return false;
        }

        v.Expect(node, JsonPointer.Append(path, "wait"), JsonKind.True);
        valid = false;
        return true;
    }
}
