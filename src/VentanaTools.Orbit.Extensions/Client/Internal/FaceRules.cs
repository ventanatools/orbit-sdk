// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using VentanaTools.Orbit.Extensions.Wire;

namespace VentanaTools.Orbit.Extensions;

/// <summary>What a face command does.</summary>
internal enum FaceCommandKind
{
    /// <summary><c>setFace</c>.</summary>
    Set = 1,

    /// <summary><c>clearFace</c>.</summary>
    Clear = 2,

    /// <summary><c>fail</c>.</summary>
    Fail = 3,
}

/// <summary>One validated, cleaned face command, ready to write.</summary>
internal sealed class FaceCommand
{
    public required FaceCommandKind Kind { get; init; }

    /// <summary>The cleaned face for <see cref="FaceCommandKind.Set"/>.</summary>
    public WireFace? Face { get; init; }

    /// <summary>The failure for <see cref="FaceCommandKind.Fail"/>.</summary>
    public Failure? Failure { get; init; }

    /// <summary>Whether the SDK keeps the face current (<see cref="Extensions.Face.Renew"/>).</summary>
    public bool Renew { get; init; }

    public static FaceCommand Clear { get; } = new() { Kind = FaceCommandKind.Clear };
}

/// <summary>
/// The checks and cleaning of <see cref="Session.SetFace"/> (contract §9.2): only the lifetime,
/// the picture and the state are validated; the text is cleaned with the display limits of
/// contract §7.7.1, exactly as a host would, and never makes a call throw.
/// </summary>
internal static class FaceRules
{
    public const int Line1Elements = 40;
    public const int Line1Units = 160;
    public const int Line2Elements = 60;
    public const int Line2Units = 240;
    public const int DetailElements = 160;
    public const int DetailUnits = 640;
    public static readonly TimeSpan MinGoodFor = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxGoodFor = TimeSpan.FromDays(1);

    /// <summary>Step 1: the arguments. Throws <see cref="ArgumentException"/>.</summary>
    public static void CheckArguments(Face face)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (face.GoodFor < MinGoodFor || face.GoodFor > MaxGoodFor)
        {
            throw new ArgumentOutOfRangeException(nameof(face), "GoodFor must be between 1 second and 1 day.");
        }

        switch (face.Picture)
        {
            case null:
                throw new ArgumentException("Picture must not be null; use FacePicture.None.", nameof(face));
            case GlyphPicture glyph when !TextRules.IsGlyph(glyph.Glyph):
                throw new ArgumentException("The picture's glyph must be one private-use character (U+E000 to U+F8FF).", nameof(face));
        }

        if (face.State is < FaceState.None or > FaceState.Off)
        {
            throw new ArgumentOutOfRangeException(nameof(face), "State is not a defined FaceState value.");
        }
    }

    /// <summary>
    /// Step 2: every variant used must be available on the connection. Protocol 3's baseline
    /// variants are <see cref="NoPicture"/>, <see cref="GlyphPicture"/> and <see cref="TextLine"/>;
    /// any other variant needs a capability this SDK does not implement, so it is never effective.
    /// </summary>
    public static void CheckVariants(Face face, Session session)
    {
        _ = session;
        if (face.Picture is not (NoPicture or GlyphPicture) || !IsBaseline(face.Line1) || !IsBaseline(face.Line2))
        {
            throw new NotSupportedException("The face uses a variant whose capability is not effective on this connection.");
        }
    }

    /// <summary>The cleaned wire face. Never throws for text content or length.</summary>
    public static WireFace Clean(Face face) => new()
    {
        Picture = face.Picture is GlyphPicture glyph ? FacePicture.Glyph(glyph.Glyph) : FacePicture.None,
        Line1 = CleanLine(face.Line1, Line1Elements, Line1Units),
        Line2 = CleanLine(face.Line2, Line2Elements, Line2Units),
        State = face.State,
        Detail = TextRules.Clean(face.Detail, DetailElements, DetailUnits),
        GoodForSeconds = Seconds(face.GoodFor),
    };

    /// <summary>The face a receiver holds for a wire face, as authors compare it in tests.</summary>
    public static Face ToFace(WireFace face, bool renew) => new()
    {
        Picture = face.Picture is GlyphPicture glyph ? FacePicture.Glyph(glyph.Glyph) : FacePicture.None,
        Line1 = face.Line1 is TextLine line1 ? CleanLine(line1, Line1Elements, Line1Units) : null,
        Line2 = face.Line2 is TextLine line2 ? CleanLine(line2, Line2Elements, Line2Units) : null,
        State = face.State,
        Detail = TextRules.Clean(face.Detail, DetailElements, DetailUnits),
        GoodFor = TimeSpan.FromSeconds(face.GoodForSeconds),
        Renew = renew,
    };

    /// <summary>Whole seconds, rounded up.</summary>
    public static int Seconds(TimeSpan goodFor) => (int)((goodFor.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);

    /// <summary>Whether <paramref name="failure"/> is a baseline token of protocol 3 (contract §7.8).</summary>
    public static bool IsBaselineFailure(Failure failure) => failure is >= Failure.UnsupportedInput and <= Failure.AppUnavailable;

    private static bool IsBaseline(FaceLine? line) => line is null or TextLine;

    private static TextLine? CleanLine(FaceLine? line, int elements, int units) =>
        line is TextLine { Text: var text } && TextRules.Clean(text, elements, units) is { } clean ? new TextLine { Text = clean } : null;
}
