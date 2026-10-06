// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Frozen;

namespace VentanaTools.Orbit.Extensions.Wire;

/// <summary>
/// The capability registry (contract §7.4). Every baseline feature of protocol 3 is implied by
/// the negotiated version; capabilities name optional features. An id is never reused for
/// another meaning, and receivers ignore ids they do not know.
/// </summary>
public static class Capabilities
{
    /// <summary>Reserved: per-launch pairing for host-managed activation.</summary>
    public const string PairingPerLaunch = "pairing.per-launch";

    /// <summary>Reserved: the host verifies the client's package family name.</summary>
    public const string IdentityPackage = "identity.package";

    /// <summary>Reserved: image pictures in faces.</summary>
    public const string FaceImage = "face.image";

    /// <summary>Reserved: time lines the host formats.</summary>
    public const string FaceTime = "face.time";

    /// <summary>Reserved: a companion may extend an invocation's deadline while it works.</summary>
    public const string InvokeProgress = "invoke.progress";

    /// <summary>Reserved: the <c>Toggle</c> setting kind.</summary>
    public const string SettingsToggle = "settings.toggle";

    /// <summary>Reserved: the <c>Text</c> setting kind.</summary>
    public const string SettingsText = "settings.text";

    /// <summary>Reserved: the <c>Number</c> setting kind.</summary>
    public const string SettingsNumber = "settings.number";

    /// <summary>Reserved: the host applies strings files and says which language it chose.</summary>
    public const string StringsLocalized = "strings.localized";

    /// <summary>Reserved: the failure token <c>LlmUnavailable</c>.</summary>
    public const string FailureLlmUnavailable = "failure.llm-unavailable";

    /// <summary>Experimental: used by conformance tests; does nothing.</summary>
    public const string TestEcho = "x.test-echo";

    /// <summary>Whether <paramref name="id"/> is an experimental capability id: <c>"x." segment ( "." segment )*</c>.</summary>
    /// <param name="id">The capability id.</param>
    /// <returns>True for a grammar-valid id that starts with <c>x.</c>.</returns>
    public static bool IsExperimental(string id) =>
        id is not null && id.StartsWith("x.", StringComparison.Ordinal) && Grammars.IsDottedCode(id);
}

/// <summary>The registry rows behind <see cref="Capabilities"/>, from which <c>fixtures/wire/v3/capabilities.json</c> is checked.</summary>
internal static class CapabilityRegistry
{
    public static IReadOnlyList<(string Id, string Status, string Meaning)> All { get; } =
    [
        (Capabilities.PairingPerLaunch, "Reserved", "Per-launch pairing for host-managed activation."),
        (Capabilities.IdentityPackage, "Reserved", "The host verifies the client's package family name."),
        (Capabilities.FaceImage, "Reserved", "Image pictures in faces."),
        (Capabilities.FaceTime, "Reserved", "Time lines the host formats."),
        (Capabilities.InvokeProgress, "Reserved", "A companion may extend an invocation's deadline while it works."),
        (Capabilities.SettingsToggle, "Reserved", "The Toggle setting kind, with a later manifest schema."),
        (Capabilities.SettingsText, "Reserved", "The Text setting kind, with a later manifest schema."),
        (Capabilities.SettingsNumber, "Reserved", "The Number setting kind, with a later manifest schema."),
        (Capabilities.StringsLocalized, "Reserved", "The host applies strings files and says which language it chose."),
        (Capabilities.FailureLlmUnavailable, "Reserved", "The failure token LlmUnavailable."),
        (Capabilities.TestEcho, "Experimental", "Used by conformance tests; does nothing."),
    ];

    private static readonly FrozenSet<string> Ids = All.Select(row => row.Id).ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string id) => Ids.Contains(id);
}
