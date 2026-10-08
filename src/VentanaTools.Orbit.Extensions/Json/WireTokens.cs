// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions;

/// <summary>
/// The explicit token tables every enumeration crosses the wire and file formats through
/// (contract §7.13), never a language's enum-to-string conversion. The tables are pinned by
/// <c>fixtures/wire/v3/enums.json</c>.
/// </summary>
internal static class WireTokens
{
    public static IReadOnlyList<(string Token, FaceState Value)> FaceStates { get; } =
    [
        ("None", FaceState.None), ("Playing", FaceState.Playing), ("Paused", FaceState.Paused),
        ("On", FaceState.On), ("Off", FaceState.Off),
    ];

    public static IReadOnlyList<(string Token, Failure Value)> Failures { get; } =
    [
        ("UnsupportedInput", Failure.UnsupportedInput), ("NeedsSetup", Failure.NeedsSetup), ("Network", Failure.Network),
        ("NoResult", Failure.NoResult), ("AppUnavailable", Failure.AppUnavailable),
    ];

    public static IReadOnlyList<(string Token, Outcome Value)> Outcomes { get; } =
    [
        ("Done", Outcome.Done), ("Refused", Outcome.Refused), ("Failed", Outcome.Failed), ("Unsupported", Outcome.Unsupported),
    ];

    public static IReadOnlyList<(string Token, NetworkUse Value)> NetworkUses { get; } =
    [
        ("None", NetworkUse.None), ("LocalNetwork", NetworkUse.LocalNetwork), ("Internet", NetworkUse.Internet),
    ];

    public static IReadOnlyList<(string Token, SettingKind Value)> SettingKinds { get; } = [("Choice", SettingKind.Choice)];

    public static IReadOnlyList<(string Token, Provides Value)> ProvidesValues { get; } =
    [
        ("invoke", Provides.Invoke), ("face", Provides.Face),
    ];

    public static IReadOnlyList<(string Token, PairingMode Value)> PairingModes { get; } =
    [
        ("Persistent", PairingMode.Persistent), ("PerLaunch", PairingMode.PerLaunch),
    ];

    public static IReadOnlyList<(string Token, Wire.ProofRole Value)> ProofRoles { get; } =
    [
        ("server", Wire.ProofRole.Server), ("client", Wire.ProofRole.Client),
    ];

    public static IReadOnlyList<(string Token, DiagnosticSeverity Value)> Severities { get; } =
    [
        ("Error", DiagnosticSeverity.Error), ("Warning", DiagnosticSeverity.Warning),
    ];

    public static IReadOnlyList<(string Token, HostStatus Value)> HostStatuses { get; } =
    [
        ("Active", HostStatus.Active), ("Reserved", HostStatus.Reserved),
    ];

    public static IReadOnlyList<(string Token, Disposition Value)> Dispositions { get; } =
    [
        ("close", Disposition.Close), ("advisory", Disposition.Advisory), ("refuse", Disposition.Refuse),
        ("ignore", Disposition.Ignore), ("local", Disposition.Local),
    ];

    public static FaceState? ParseFaceState(string? token) => Parse(FaceStates, token);

    public static Failure? ParseFailure(string? token) => Parse(Failures, token);

    public static Outcome? ParseOutcome(string? token) => Parse(Outcomes, token);

    public static NetworkUse? ParseNetworkUse(string? token) => Parse(NetworkUses, token);

    public static SettingKind? ParseSettingKind(string? token) => Parse(SettingKinds, token);

    public static Provides? ParseProvides(string? token) => Parse(ProvidesValues, token);

    public static PairingMode? ParsePairingMode(string? token) => Parse(PairingModes, token);

    public static string? FaceStateToken(FaceState value) => Token(FaceStates, value);

    public static string? FailureToken(Failure value) => Token(Failures, value);

    public static string? OutcomeToken(Outcome value) => Token(Outcomes, value);

    public static string? NetworkUseToken(NetworkUse value) => Token(NetworkUses, value);

    public static string? SettingKindToken(SettingKind value) => Token(SettingKinds, value);

    public static string? SeverityToken(DiagnosticSeverity value) => Token(Severities, value);

    public static string? DispositionToken(Disposition value) => Token(Dispositions, value);

    public static string? ProofRoleToken(Wire.ProofRole value) => Token(ProofRoles, value);

    private static T? Parse<T>(IReadOnlyList<(string Token, T Value)> table, string? token)
        where T : struct, Enum
    {
        if (token is null)
        {
            return null;
        }

        foreach (var (name, value) in table)
        {
            if (string.Equals(name, token, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    private static string? Token<T>(IReadOnlyList<(string Token, T Value)> table, T value)
        where T : struct, Enum
    {
        foreach (var (name, entry) in table)
        {
            if (EqualityComparer<T>.Default.Equals(entry, value))
            {
                return name;
            }
        }

        return null;
    }
}
