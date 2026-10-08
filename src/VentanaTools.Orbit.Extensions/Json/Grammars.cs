// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace VentanaTools.Orbit.Extensions;

/// <summary>The shared ASCII grammars of contract §2.4, as plain checks.</summary>
internal static class Grammars
{
    public const int MaxDottedCode = 64;
    public const int MaxMemberName = 32;

    public static bool IsAsciiLowerOrDigit(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c);

    public static bool IsLowerHex(char c) => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f';

    /// <summary><c>segment ( "." segment )+</c>, at most 64 characters.</summary>
    public static bool IsDottedCode([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: > 0 and <= MaxDottedCode })
        {
            return false;
        }

        var parts = 0;
        foreach (var range in value.AsSpan().Split('.'))
        {
            if (!ExtensionIds.IsSegment(value.AsSpan()[range]))
            {
                return false;
            }

            parts++;
        }

        return parts >= 2;
    }

    /// <summary>32 lowercase hexadecimal digits, not all zero.</summary>
    public static bool IsGuid([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: 32 })
        {
            return false;
        }

        var nonZero = false;
        foreach (var c in value)
        {
            if (!IsLowerHex(c))
            {
                return false;
            }

            nonZero |= c != '0';
        }

        return nonZero;
    }

    /// <summary>64 lowercase hexadecimal digits.</summary>
    public static bool IsSha256([NotNullWhen(true)] string? value) =>
        value is { Length: 64 } && value.All(IsLowerHex);

    /// <summary>Canonical standard Base64 with padding of exactly 32 bytes (44 characters).</summary>
    public static bool IsKey32([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: 44 })
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[33];
        try
        {
            return TryDecodeKey32(value, bytes[..32]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Decodes a canonical 44-character Base64 value of exactly 32 bytes into <paramref name="destination"/>.</summary>
    public static bool TryDecodeKey32(ReadOnlySpan<char> value, Span<byte> destination)
    {
        if (value.Length != 44 || destination.Length < 32 || value[43] != '=' || value[42] == '=')
        {
            return false;
        }

        foreach (var c in value[..43])
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/'))
            {
                return false;
            }
        }

        if (!Convert.TryFromBase64Chars(value, destination, out var written) || written != 32)
        {
            return false;
        }

        // Canonical: the unused low bits of the last character are zero.
        return (Base64Index(value[42]) & 0b11) == 0;
    }

    /// <summary>Decodes a canonical Base64 value of exactly 32 bytes from UTF-8 into <paramref name="destination"/>.</summary>
    public static bool TryDecodeKey32(ReadOnlySpan<byte> utf8, Span<byte> destination)
    {
        if (utf8.Length != 44 || destination.Length < 32 || utf8[43] != (byte)'=' || utf8[42] == (byte)'=')
        {
            return false;
        }

        foreach (var b in utf8[..43])
        {
            if (!(char.IsAsciiLetterOrDigit((char)b) || b is (byte)'+' or (byte)'/'))
            {
                return false;
            }
        }

        if (Base64.DecodeFromUtf8(utf8, destination, out var consumed, out var written) != System.Buffers.OperationStatus.Done
            || consumed != 44 || written != 32)
        {
            return false;
        }

        return (Base64Index((char)utf8[42]) & 0b11) == 0;
    }

    private static int Base64Index(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        _ => 63,
    };

    /// <summary>The JSON member-name grammar <c>[a-z][A-Za-z0-9]{0,31}</c>.</summary>
    public static bool IsMemberName(string value) =>
        value is { Length: > 0 and <= MaxMemberName } && char.IsAsciiLetterLower(value[0]) && value.All(char.IsAsciiLetterOrDigit);

    /// <summary>Package version: <c>num "." num "." num</c>, <c>num = "0" / [1-9][0-9]{0,8}</c>.</summary>
    public static bool IsPackageVersion([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: > 0 and <= 29 })
        {
            return false;
        }

        var parts = 0;
        foreach (var range in value.AsSpan().Split('.'))
        {
            var part = value.AsSpan()[range];
            if (part.Length is 0 or > 9 || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            foreach (var c in part)
            {
                if (!char.IsAsciiDigit(c))
                {
                    return false;
                }
            }

            parts++;
        }

        return parts == 3;
    }

    /// <summary>Printable ASCII, U+0020 to U+007E, 1 to <paramref name="max"/> characters.</summary>
    public static bool IsPrintableAscii([NotNullWhen(true)] string? value, int max) =>
        value is { Length: > 0 } && value.Length <= max && value.All(c => c is >= ' ' and <= '~');

    /// <summary>Whether every character matches <paramref name="allowed"/>, 1 to <paramref name="max"/> characters.</summary>
    public static bool IsTokenOf([NotNullWhen(true)] string? value, int max, Func<char, bool> allowed) =>
        value is { Length: > 0 } && value.Length <= max && value.All(allowed);

    /// <summary><c>[A-Za-z0-9@._/+-]{1,64}</c>: the informational client name in <c>hello</c>.</summary>
    public static bool IsClientName([NotNullWhen(true)] string? value) =>
        IsTokenOf(value, 64, c => char.IsAsciiLetterOrDigit(c) || c is '@' or '.' or '_' or '/' or '+' or '-');

    /// <summary><c>[0-9A-Za-z.+-]{1,32}</c>: a client or host version in the handshake.</summary>
    public static bool IsPeerVersion([NotNullWhen(true)] string? value) =>
        IsTokenOf(value, 32, c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-');

    /// <summary>A pipe-name edition: <c>1*32( [a-z0-9] / "-" )</c>.</summary>
    public static bool IsEdition([NotNullWhen(true)] string? value) =>
        IsTokenOf(value, 32, c => IsAsciiLowerOrDigit(c) || c == '-');

    /// <summary>16 lowercase hexadecimal digits.</summary>
    public static bool IsUserHash([NotNullWhen(true)] string? value) => value is { Length: 16 } && value.All(IsLowerHex);
}
