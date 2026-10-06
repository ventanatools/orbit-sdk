// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace VentanaTools.Orbit.Extensions;

/// <summary>Shared declaration and plain face text rules. These checks never consult a host catalog.</summary>
public static class ExtensionText
{
    public const int MaxSettingKeyLength = 32;
    public const int MaxElementUnits = 16;

    /// <summary>Cleans plain face text and cuts only at a text element, within both limits.</summary>
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
            if (char.IsSurrogatePair(text, i))
            {
                if (!IsNoncharacter(char.ConvertToUtf32(c, text[i + 1])))
                {
                    kept.Append(c).Append(text[i + 1]);
                }

                i++;
            }
            else if (BreaksTheLine(c))
            {
                kept.Append(' ');
            }
            else if (!char.IsSurrogate(c) && !char.IsControl(c) && !IsNoncharacter(c) && !ChangesLayout(c))
            {
                kept.Append(c);
            }
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
                element = "\uFFFD";
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

    private static bool IsNoncharacter(int codePoint) =>
        codePoint is >= 0xFDD0 and <= 0xFDEF || (codePoint & 0xFFFE) == 0xFFFE;

    private static bool ChangesLayout(char c) =>
        c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u2028' or '\u2029';

    private static bool BreaksTheLine(char c) =>
        c is '\t' or '\n' or (char)0x0B or (char)0x0C or '\r' or (char)0x85 or (char)0x2028 or (char)0x2029;


    /// <summary>A lower-case ASCII letter, then ASCII letters or digits, at most 32 characters.</summary>
    public static bool IsSettingKey([NotNullWhen(true)] string? key) =>
        key is { Length: > 0 and <= MaxSettingKeyLength }
        && char.IsAsciiLetterLower(key[0]) && key.All(char.IsAsciiLetterOrDigit);

    /// <summary>One character in the icon font's private-use range.</summary>
    public static bool IsGlyph(string? value) =>
        value is { Length: 1 } && value[0] is >= '\uE000' and <= '\uF8FF';

    /// <summary>Printable declaration text, without padding, controls, formatting characters or invalid UTF-16.</summary>
    public static bool IsDeclarationText([NotNullWhen(true)] string? value, int maximum)
    {
        if (value is null || value.Length is 0 || value.Length > maximum
            || string.IsNullOrWhiteSpace(value) || value != value.Trim()) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
                continue;
            }
            if (char.IsLowSurrogate(c) || char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format
                || c is '\uFFFD' or '\uFFFE' or '\uFFFF' or >= '\uFDD0' and <= '\uFDEF') return false;
        }
        return true;
    }
}
