// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace VentanaTools.Orbit.Extensions;

/// <summary>
/// The text and value rules every extension string follows: the declaration text rule
/// (contract §3.6), the glyph rule (§3.7), the URL rule (§3.8), face text cleaning (§7.7.3) and
/// the shared grammars of §2.4. Every member is pure and never throws for any input.
/// </summary>
/// <remarks>
/// The same disallowed-character class is refused in declaration text and removed from face
/// text: C0 and C1 controls, line and paragraph separators, bidirectional embeddings, overrides
/// and isolates, U+FEFF, U+FFFD, noncharacters, unpaired surrogates, and every other format
/// (Cf) character except ZWNJ, ZWJ, LRM, RLM and ALM, which scripts and mixed-direction names
/// need.
/// </remarks>
public static class TextRules
{
    private const int MaxElementUnits = 16;
    private const int MaxUrlLength = 512;

    /// <summary>Whether <paramref name="codePoint"/> is a disallowed character (contract §3.6).</summary>
    /// <param name="codePoint">A Unicode code point; a surrogate code point stands for an unpaired surrogate.</param>
    /// <returns>True when the character may not appear in declaration text and is removed from face text.</returns>
    public static bool IsDisallowed(int codePoint)
    {
        if (codePoint is < 0 or > 0x10FFFF)
        {
            return true;
        }

        if (codePoint is <= 0x1F or (>= 0x7F and <= 0x9F)
            or 0x2028 or 0x2029
            or (>= 0x202A and <= 0x202E)
            or (>= 0x2066 and <= 0x2069)
            or 0xFEFF or 0xFFFD
            or (>= 0xFDD0 and <= 0xFDEF)
            or (>= 0xD800 and <= 0xDFFF)
            or (>= 0xE0000 and <= 0xE007F))
        {
            return true;
        }

        if ((codePoint & 0xFFFE) == 0xFFFE)
        {
            return true;
        }

        if (codePoint is 0x200C or 0x200D or 0x200E or 0x200F or 0x061C)
        {
            return false;
        }

        return CharUnicodeInfo.GetUnicodeCategory(codePoint) == UnicodeCategory.Format;
    }

    /// <summary>
    /// Checks one declaration string (contract §3.6) of at most <paramref name="maxUnits"/> UTF-16
    /// code units.
    /// </summary>
    /// <param name="value">The text, after JSON unescaping.</param>
    /// <param name="maxUnits">The longest allowed length in UTF-16 code units.</param>
    /// <returns>
    /// Null when the text is valid; otherwise the first code that applies, in this order:
    /// <c>text.empty</c> (null or empty), <c>string.too-long</c>, <c>text.empty</c> (only white
    /// space or format characters), <c>text.whitespace</c>, <c>text.invalid-character</c>. For a
    /// <c>name</c>, readers report the matching <c>*.label-required</c> code instead of
    /// <c>text.empty</c>.
    /// </returns>
    public static string? CheckDeclarationText(string? value, int maxUnits)
    {
        if (string.IsNullOrEmpty(value))
        {
            return DiagnosticCodes.TextEmpty;
        }

        if (value.Length > maxUnits)
        {
            return DiagnosticCodes.StringTooLong;
        }

        if (IsBlank(value))
        {
            return DiagnosticCodes.TextEmpty;
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return DiagnosticCodes.TextWhitespace;
        }

        return HasDisallowed(value) ? DiagnosticCodes.TextInvalidCharacter : null;
    }

    /// <summary>
    /// Cleans face text (contract §7.7.3): removes every disallowed character, except that line
    /// and paragraph breaks and tabs become a space; replaces a text element longer than 16
    /// UTF-16 code units with U+FFFD; trims; and cuts at a text-element boundary within both limits.
    /// </summary>
    /// <param name="text">The text to clean.</param>
    /// <param name="maxElements">The most text elements to keep.</param>
    /// <param name="maxUnits">The most UTF-16 code units to keep.</param>
    /// <returns>The cleaned text, or null when <paramref name="text"/> is null or nothing remains.</returns>
    public static string? Clean(string? text, int maxElements, int maxUnits)
    {
        if (string.IsNullOrEmpty(text) || maxElements <= 0 || maxUnits <= 0)
        {
            return null;
        }

        var kept = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            int codePoint;
            var units = 1;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(c, text[i + 1]);
                units = 2;
            }
            else
            {
                codePoint = c;
            }

            if (codePoint is >= 0x09 and <= 0x0D or 0x85 or 0x2028 or 0x2029)
            {
                kept.Append(' ');
            }
            else if (!IsDisallowed(codePoint))
            {
                kept.Append(text, i, units);
            }

            i += units - 1;
        }

        var clean = kept.ToString().Trim();
        var cut = new StringBuilder(Math.Min(clean.Length, maxUnits));
        var elements = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(clean);
        while (elements < maxElements && enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (element.Length > MaxElementUnits)
            {
                element = "�";
            }

            if (cut.Length + element.Length > maxUnits)
            {
                break;
            }

            cut.Append(element);
            elements++;
        }

        var result = cut.ToString().TrimEnd();
        return result.Length == 0 ? null : result;
    }

    /// <summary>Whether <paramref name="value"/> is exactly one UTF-16 code unit in U+E000 to U+F8FF (contract §3.7).</summary>
    /// <param name="value">The glyph.</param>
    /// <returns>True for a valid glyph.</returns>
    public static bool IsGlyph([NotNullWhen(true)] string? value) =>
        value is { Length: 1 } && value[0] is >= '' and <= '';

    /// <summary>Whether <paramref name="value"/> is a setting id: <c>[a-z][A-Za-z0-9]*</c>, at most 32 characters.</summary>
    /// <param name="value">The setting id.</param>
    /// <returns>True for a valid setting id.</returns>
    public static bool IsSettingId([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= 32 } && char.IsAsciiLetterLower(value[0]) && value.All(char.IsAsciiLetterOrDigit);

    /// <summary>Whether <paramref name="value"/> is a choice value: a segment of at most 64 characters.</summary>
    /// <param name="value">The choice value.</param>
    /// <returns>True for a valid choice value.</returns>
    public static bool IsChoiceValue([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= 64 } && ExtensionIds.IsSegment(value);

    /// <summary>Whether <paramref name="value"/> is a host id: <c>[a-z] [a-z0-9]* ( "-" [a-z0-9]+ )*</c>, at most 32 characters.</summary>
    /// <param name="value">The host id.</param>
    /// <returns>True for a valid host id.</returns>
    public static bool IsHostId([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= 32 } && char.IsAsciiLetterLower(value[0]) && ExtensionIds.IsSegment(value);

    /// <summary>
    /// Whether <paramref name="value"/> is a language tag: <c>[A-Za-z]{2,3} ( "-" [A-Za-z0-9]{1,8} )*</c>,
    /// at most 35 characters. Tags compare case-insensitively.
    /// </summary>
    /// <param name="value">The language tag.</param>
    /// <returns>True for a valid language tag.</returns>
    public static bool IsLanguageTag([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: > 0 and <= 35 })
        {
            return false;
        }

        var first = true;
        foreach (var range in value.AsSpan().Split('-'))
        {
            var part = value.AsSpan()[range];
            if (first)
            {
                if (part.Length is < 2 or > 3)
                {
                    return false;
                }

                foreach (var c in part)
                {
                    if (!char.IsAsciiLetter(c))
                    {
                        return false;
                    }
                }

                first = false;
                continue;
            }

            if (part.Length is < 1 or > 8)
            {
                return false;
            }

            foreach (var c in part)
            {
                if (!char.IsAsciiLetterOrDigit(c))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="value"/> follows the URL rule (contract §3.8): an absolute URI
    /// with scheme <c>https</c>, an authority without user information, only printable ASCII and
    /// no white space, and at most 512 characters.
    /// </summary>
    /// <param name="value">The URL.</param>
    /// <returns>True for a valid URL.</returns>
    public static bool IsHttpsUrl([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: > 8 and <= MaxUrlLength } || !value.All(c => c is >= '!' and <= '~')
            || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var authorityEnd = value.AsSpan(8).IndexOfAny('/', '?', '#');
        var authority = authorityEnd < 0 ? value.AsSpan(8) : value.AsSpan(8, authorityEnd);
        if (authority.IsEmpty || authority.Contains('@') || authority[0] == ':')
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && uri.UserInfo.Length == 0
            && uri.Host.Length > 0;
    }

    /// <summary>Whether the text is only White_Space and format characters.</summary>
    internal static bool IsBlank(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                continue;
            }

            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(value[i], value[i + 1])) != UnicodeCategory.Format)
                {
                    return false;
                }

                i++;
                continue;
            }

            if (char.GetUnicodeCategory(value[i]) != UnicodeCategory.Format)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The number of text elements (extended grapheme clusters) in <paramref name="value"/>.</summary>
    internal static int TextElements(string value) => new StringInfo(value).LengthInTextElements;

    private static bool HasDisallowed(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                if (IsDisallowed(char.ConvertToUtf32(c, value[i + 1])))
                {
                    return true;
                }

                i++;
                continue;
            }

            if (IsDisallowed(c))
            {
                return true;
            }
        }

        return false;
    }
}
