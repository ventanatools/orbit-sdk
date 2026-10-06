// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions;

/// <summary>
/// An extension's manifest, <c>extension.json</c> (contract §3): its identity block, the hosts
/// that may admit it, optional publisher, disclosure and requirement blocks, and its
/// contributions. Property names mirror the JSON member names.
/// </summary>
/// <remarks>
/// A manifest is a declaration: it contains no executable, credential or host power. Read one
/// with <see cref="ManifestReader"/>, check one built in code with
/// <see cref="ManifestReader.Validate"/>, and write one with <see cref="ManifestWriter"/>.
/// </remarks>
public sealed class ExtensionManifest
{
    /// <summary>The only manifest schema version of this contract.</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>The <c>$schema</c> member: a schema URL or a relative path to a local copy. Ignored by hosts.</summary>
    public string? Schema { get; init; }

    /// <summary>The <c>schemaVersion</c> member; exactly 3.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The extension id, an extension author's id with no <c>/</c> (contract §2.4, §3.4).</summary>
    public required string Id { get; init; }

    /// <summary>The extension's name: declaration text of 1 to 80 UTF-16 code units.</summary>
    public required string Name { get; init; }

    /// <summary>The extension's description: declaration text of 1 to 512 UTF-16 code units.</summary>
    public required string Description { get; init; }

    /// <summary>The package version, <c>MAJOR.MINOR.PATCH</c>, compared numerically part by part.</summary>
    public required string Version { get; init; }

    /// <summary>The 1 to 8 host ids that may admit the manifest.</summary>
    public required IReadOnlyList<string> Hosts { get; init; }

    /// <summary>The glyph shown for the extension as a whole; null for the first contribution's glyph.</summary>
    public string? Glyph { get; init; }

    /// <summary>The publisher, shown as unverified text supplied by the extension.</summary>
    public Publisher? Publisher { get; init; }

    /// <summary>An https support URL (contract §3.8).</summary>
    public string? SupportUrl { get; init; }

    /// <summary>The language tag of the manifest's own text; null for <c>en-US</c>.</summary>
    public string? DefaultLanguage { get; init; }

    /// <summary>The author's statement of network use. Declarative only; never enforced.</summary>
    public Disclosures? Disclosures { get; init; }

    /// <summary>Optional wire capabilities the extension needs; null when it uses only baseline features.</summary>
    public ManifestRequirements? Requires { get; init; }

    /// <summary>The 1 to 32 contributions, with unique ids.</summary>
    public required IReadOnlyList<Contribution> Contributions { get; init; }
}

/// <summary>One entry of the manifest's <c>contributions</c>: an action, a widget, or both (contract §3.3).</summary>
public sealed class Contribution
{
    /// <summary>The contribution id: the extension id, a <c>/</c> and segments, or the extension id itself for a lone contribution.</summary>
    public required string Id { get; init; }

    /// <summary>The name: declaration text of 1 to 80 UTF-16 code units.</summary>
    public required string Name { get; init; }

    /// <summary>The description: declaration text of 1 to 512 UTF-16 code units.</summary>
    public required string Description { get; init; }

    /// <summary>One private-use glyph (contract §3.7).</summary>
    public required string Glyph { get; init; }

    /// <summary>What the contribution provides: <see cref="Provides.Invoke"/>, <see cref="Provides.Face"/> or both.</summary>
    public required Provides Provides { get; init; }

    /// <summary>The 0 to 16 setting rows, with unique ids within the contribution.</summary>
    public IReadOnlyList<Setting> Settings { get; init; } = [];
}

/// <summary>The manifest's <c>provides</c> values.</summary>
[Flags]
public enum Provides
{
    /// <summary>Nothing; never valid in a manifest.</summary>
    None = 0,

    /// <summary><c>invoke</c>: picking the contribution runs something.</summary>
    Invoke = 1,

    /// <summary><c>face</c>: the contribution shows live state.</summary>
    Face = 2,
}

/// <summary>One setting row, in the Ventana setting-row shape (contract §3.3.1).</summary>
public sealed class Setting
{
    /// <summary>The setting id (contract §2.4), for example <c>mode</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The setting kind; <see cref="SettingKind.Choice"/> is the only kind of schema 3.</summary>
    public SettingKind Kind { get; init; } = SettingKind.Choice;

    /// <summary>The name shown to people: declaration text of 1 to 80 UTF-16 code units.</summary>
    public required string Name { get; init; }

    /// <summary>An optional description: declaration text of 1 to 512 UTF-16 code units.</summary>
    public string? Description { get; init; }

    /// <summary>The default value, which equals one choice value.</summary>
    public required string Default { get; init; }

    /// <summary>The 2 to 32 choices, with unique values.</summary>
    public required IReadOnlyList<SettingChoice> Choices { get; init; }
}

/// <summary>A setting kind. <c>Toggle</c>, <c>Text</c> and <c>Number</c> are reserved names for later schema versions.</summary>
public enum SettingKind
{
    /// <summary><c>Choice</c>: one of a fixed list of values.</summary>
    Choice = 1,
}

/// <summary>One choice of a <see cref="SettingKind.Choice"/> setting.</summary>
public sealed class SettingChoice
{
    /// <summary>The value sent on the wire: a segment of at most 64 characters.</summary>
    public required string Value { get; init; }

    /// <summary>The name shown to people: declaration text of 1 to 80 UTF-16 code units.</summary>
    public required string Name { get; init; }
}

/// <summary>The manifest's <c>publisher</c>: a claim no host verifies (contract §3.2.1).</summary>
public sealed class Publisher
{
    /// <summary>The publisher's name: declaration text of 1 to 80 UTF-16 code units.</summary>
    public required string Name { get; init; }

    /// <summary>An https URL (contract §3.8).</summary>
    public string? Url { get; init; }
}

/// <summary>The manifest's <c>disclosures</c>: the author's statement, never enforced (contract §3.2.2).</summary>
public sealed class Disclosures
{
    /// <summary>Whether the companion contacts other computers.</summary>
    public required NetworkUse Network { get; init; }

    /// <summary>An https privacy policy URL (contract §3.8).</summary>
    public string? PrivacyUrl { get; init; }
}

/// <summary>The declared network use, ordered from least to most.</summary>
public enum NetworkUse
{
    /// <summary><c>None</c>: the companion contacts no other computer.</summary>
    None = 1,

    /// <summary><c>LocalNetwork</c>: the companion contacts computers on the local network.</summary>
    LocalNetwork = 2,

    /// <summary><c>Internet</c>: the companion contacts computers on the internet.</summary>
    Internet = 3,
}

/// <summary>The manifest's <c>requires</c> block (contract §3.2.3).</summary>
public sealed class ManifestRequirements
{
    /// <summary>The 1 to 16 unique capability ids the extension needs (contract §7.4).</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }
}

/// <summary>The state a face shows (contract §7.7.1).</summary>
public enum FaceState
{
    /// <summary><c>None</c>: no particular state.</summary>
    None = 1,

    /// <summary><c>Playing</c>.</summary>
    Playing = 2,

    /// <summary><c>Paused</c>.</summary>
    Paused = 3,

    /// <summary><c>On</c>.</summary>
    On = 4,

    /// <summary><c>Off</c>.</summary>
    Off = 5,
}

/// <summary>
/// Why a contribution failed (contract §7.8). The host owns the words it shows. Value 6 is
/// reserved for <c>LlmUnavailable</c>, which arrives with the capability <c>failure.llm-unavailable</c>.
/// </summary>
public enum Failure
{
    /// <summary><c>UnsupportedInput</c>: the request or input is not something this contribution handles.</summary>
    UnsupportedInput = 1,

    /// <summary><c>NeedsSetup</c>: the person must configure something first.</summary>
    NeedsSetup = 2,

    /// <summary><c>Network</c>: a network request it depends on failed.</summary>
    Network = 3,

    /// <summary><c>NoResult</c>: it ran but produced nothing to show.</summary>
    NoResult = 4,

    /// <summary><c>AppUnavailable</c>: an app or device it works with is not running or not reachable.</summary>
    AppUnavailable = 5,
}

/// <summary>How an invocation ended (contract §7.8).</summary>
public enum Outcome
{
    /// <summary><c>Done</c>: it worked.</summary>
    Done = 1,

    /// <summary><c>Refused</c>: the companion declined; the host says it didn't run.</summary>
    Refused = 2,

    /// <summary><c>Failed</c>: it ran and failed.</summary>
    Failed = 3,

    /// <summary><c>Unsupported</c>: the companion does not implement this action.</summary>
    Unsupported = 4,
}

/// <summary>
/// A face's picture (contract §7.7.1). Each variant mirrors a <c>$type</c>; further variants
/// arrive with capabilities.
/// </summary>
public abstract record FacePicture
{
    private protected FacePicture()
    {
    }

    /// <summary>No picture: <c>{ "$type": "none" }</c>.</summary>
    public static FacePicture None { get; } = new NoPicture();

    /// <summary>A glyph picture: <c>{ "$type": "glyph", "glyph": ... }</c>.</summary>
    /// <param name="glyph">One private-use glyph (contract §3.7).</param>
    /// <returns>The picture.</returns>
    public static GlyphPicture Glyph(string glyph) => new() { Glyph = glyph };
}

/// <summary>No picture (<c>"$type": "none"</c>).</summary>
public sealed record NoPicture : FacePicture;

/// <summary>A glyph picture (<c>"$type": "glyph"</c>).</summary>
public sealed record GlyphPicture : FacePicture
{
    /// <summary>One private-use glyph (contract §3.7).</summary>
    public new required string Glyph { get; init; }
}

/// <summary>
/// One line of a face (contract §7.7.1). A string converts to a <see cref="TextLine"/>, so
/// <c>Line1 = "4:59"</c> works. Further variants arrive with capabilities.
/// </summary>
public abstract record FaceLine
{
    private protected FaceLine()
    {
    }

    /// <summary>Makes a <see cref="TextLine"/> from text; null stays null.</summary>
    /// <param name="text">The line's text.</param>
    public static implicit operator FaceLine?(string? text) => text is null ? null : new TextLine { Text = text };
}

/// <summary>A text line (<c>"$type": "text"</c>).</summary>
public sealed record TextLine : FaceLine
{
    /// <summary>The text. Receivers clean it and cut it to the display limits.</summary>
    public required string Text { get; init; }
}

/// <summary>A host's id and version, as the handshake carries them.</summary>
public sealed class HostIdentity
{
    /// <summary>The host id (contract §2.4).</summary>
    public required string Id { get; init; }

    /// <summary>The host version: <c>[0-9A-Za-z.+-]{1,32}</c>.</summary>
    public required string Version { get; init; }
}
