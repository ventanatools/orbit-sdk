// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;
using System.Text;

namespace VentanaTools.Orbit.Extensions.Packaging;

/// <summary>
/// Propagates the zone of origin (Mark-of-the-Web) from a package file to the files a host
/// extracts from it (contract §5.8 item 5), through the NTFS alternate data stream
/// <c>Zone.Identifier</c>. On other platforms the members do nothing.
/// </summary>
public static class ZoneOfOrigin
{
    private const string StreamSuffix = ":Zone.Identifier";
    private const int MaxStreamBytes = 4096;
    private const int MaxHostUrlLength = 2048;
    private const int Internet = 3;

    /// <summary>
    /// The package file's zone: null when it has no <c>Zone.Identifier</c> stream (or the platform
    /// has none); 3 (Internet) when the stream is larger than 4,096 bytes, cannot be read or parsed,
    /// has no <c>ZoneId</c>, or has a <c>ZoneId</c> outside 0 to 4.
    /// </summary>
    /// <param name="packagePath">The package file's path.</param>
    /// <returns>The zone, 0 to 4, or null.</returns>
    public static int? Read(string packagePath)
    {
        var text = ReadStream(packagePath, out var exists);
        if (!exists)
        {
            return null;
        }

        if (text is null)
        {
            return Internet;
        }

        var value = Value(text, "ZoneId");
        return value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var zone) && zone is >= 0 and <= 4
            ? zone
            : Internet;
    }

    /// <summary>
    /// The package file's <c>HostUrl</c>, when its <c>Zone.Identifier</c> stream has one that starts
    /// with <c>http://</c> or <c>https://</c>, is at most 2,048 characters and is printable ASCII
    /// (U+0021 to U+007E); otherwise null.
    /// </summary>
    /// <param name="packagePath">The package file's path.</param>
    /// <returns>The host URL, or null.</returns>
    public static string? ReadHostUrl(string packagePath)
    {
        var text = ReadStream(packagePath, out _);
        return text is null ? null : Value(text, "HostUrl") is { } url && IsSafeUrl(url) ? url : null;
    }

    /// <summary>
    /// Writes the zone to an extracted file's <c>Zone.Identifier</c> stream when
    /// <paramref name="zone"/> is 1 or higher: <c>[ZoneTransfer]</c>, <c>ZoneId</c>,
    /// <c>ReferrerUrl</c> (the package file's <c>file:</c> URI), and <c>HostUrl</c> only when it is safe
    /// (see <see cref="ReadHostUrl"/>). Does nothing for zone 0, or where there are no alternate data streams.
    /// </summary>
    /// <param name="extractedFilePath">The extracted file.</param>
    /// <param name="zone">The zone, 0 to 4.</param>
    /// <param name="packagePath">The package file it came from.</param>
    /// <param name="hostUrl">The package's host URL, or null.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="zone"/> is outside 0 to 4.</exception>
    /// <exception cref="IOException">The stream cannot be written.</exception>
    public static void Apply(string extractedFilePath, int zone, string packagePath, string? hostUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedFilePath);
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(zone, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zone, 4);
        if (zone < 1 || !OperatingSystem.IsWindows())
        {
            return;
        }

        var text = new StringBuilder();
        text.Append("[ZoneTransfer]\r\n");
        text.Append("ZoneId=").Append(zone.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        text.Append("ReferrerUrl=").Append(new Uri(Path.GetFullPath(packagePath)).AbsoluteUri).Append("\r\n");
        if (hostUrl is not null && IsSafeUrl(hostUrl))
        {
            text.Append("HostUrl=").Append(hostUrl).Append("\r\n");
        }

        File.WriteAllText(extractedFilePath + StreamSuffix, text.ToString(), new UTF8Encoding(false));
    }

    private static bool IsSafeUrl(string url) =>
        (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        && url.Length <= MaxHostUrlLength && url.All(c => c is >= '!' and <= '~');

    private static string? ReadStream(string packagePath, out bool exists)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        exists = false;
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(packagePath + StreamSuffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            exists = true;
            bytes = new byte[MaxStreamBytes + 1];
            var total = 0;
            while (total < bytes.Length)
            {
                var read = stream.Read(bytes, total, bytes.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > MaxStreamBytes)
            {
                return null;
            }

            Array.Resize(ref bytes, total);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The stream exists but cannot be read: the zone is Internet.
            exists = true;
            return null;
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? Value(string text, string key)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(equals + 1)..].Trim();
            }
        }

        return null;
    }
}
