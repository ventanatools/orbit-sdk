// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VentanaTools.Orbit.Extensions.Tests;

/// <summary>Finds the repository and reads the shared fixtures in <c>fixtures/</c>.</summary>
internal static class Fixtures
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The repository root: the nearest folder above the test binaries that holds <c>fixtures/hosts.json</c>.</summary>
    public static string Root => RootPath.Value;

    public static string Directory => Path.Combine(Root, "fixtures");

    /// <summary>Whether tests should rewrite generated fixtures instead of comparing them (maintainers only).</summary>
    public static bool Update => Environment.GetEnvironmentVariable("VENTANA_UPDATE_FIXTURES") == "1";

    /// <summary>The full path of a fixture, from a path relative to <c>fixtures/</c> with <c>/</c> separators.</summary>
    public static string PathOf(string relative) => Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A text fixture's bytes with every CRLF turned into LF, as the pins are taken.</summary>
    public static byte[] Text(string relative) => Lf(File.ReadAllBytes(PathOf(relative)));

    /// <summary>A fixture's exact bytes.</summary>
    public static byte[] Bytes(string relative) => File.ReadAllBytes(PathOf(relative));

    public static JsonDocument Json(string relative) => JsonDocument.Parse(Text(relative));

    public static byte[] Lf(byte[] bytes)
    {
        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
            {
                continue;
            }

            output.Add(bytes[i]);
        }

        return [.. output];
    }

    /// <summary>The relative paths of every file under <c>fixtures/</c>, with <c>/</c> separators, sorted ordinally.</summary>
    public static IReadOnlyList<string> All() =>
        System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Directory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Options for writing generated fixtures: two-space indentation, LF line endings, minimal escaping.</summary>
    public static JsonWriterOptions WriterOptions { get; } = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Compares generated fixture bytes with the checked-in file, or writes them when <see cref="Update"/> is set.</summary>
    public static void AssertGenerated(string relative, byte[] generated)
    {
        var path = PathOf(relative);
        if (Update)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, generated);
            return;
        }

        Xunit.Assert.True(File.Exists(path), "The generated fixture " + relative + " is missing.");
        var expected = File.ReadAllBytes(path);
        if (!expected.AsSpan().SequenceEqual(generated) && !Lf(expected).AsSpan().SequenceEqual(generated))
        {
            Xunit.Assert.Fail("The checked-in fixture " + relative + " differs from what the C# catalog generates. "
                + "Set VENTANA_UPDATE_FIXTURES=1 and run the tests to regenerate it.");
        }
    }

    /// <summary>Writes JSON with <see cref="WriterOptions"/> and a final line feed.</summary>
    public static byte[] WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            write(writer);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "fixtures", "hosts.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository's fixtures folder was not found above the test binaries.");
    }

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}

/// <summary>A clock the test advances by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks;
    private readonly List<(long Due, TimerCallback Callback, object? State)> _timers = [];

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    public void Advance(TimeSpan by)
    {
        List<(long Due, TimerCallback Callback, object? State)> due;
        lock (_timers)
        {
            _ticks += by.Ticks;
            due = _timers.Where(timer => timer.Due <= _ticks).ToList();
            _timers.RemoveAll(timer => timer.Due <= _ticks);
        }

        foreach (var timer in due)
        {
            timer.Callback(timer.State);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this);
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_timers)
            {
                _timers.Add((_ticks + dueTime.Ticks, callback, state));
            }
        }

        return timer;
    }

    private sealed class ManualTimer(ManualTime owner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => owner is not null;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>The test host of every fixture, <c>example-host</c>, which is never a registry entry.</summary>
internal static class TestHosts
{
    public const string Id = "example-host";

    public static HostInfo Example { get; } = new() { Id = Id, DisplayName = "Example host", Status = HostStatus.Active };

    public static IReadOnlyList<HostInfo> Known { get; } = [Example];

    public static ManifestReadOptions Options { get; } = new() { KnownHosts = Known };
}

/// <summary>Reads JSON strings that may hold unpaired surrogates, which System.Text.Json refuses to materialize.</summary>
internal static class LooseJson
{
    public static string? String(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return null;
        }

        var raw = element.GetRawText();
        var text = new System.Text.StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length - 1; i++)
        {
            var c = raw[i];
            if (c != '\\')
            {
                text.Append(c);
                continue;
            }

            var e = raw[++i];
            switch (e)
            {
                case 'u':
                    text.Append((char)Convert.ToInt32(raw.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                case 'n':
                    text.Append('\n');
                    break;
                case 'r':
                    text.Append('\r');
                    break;
                case 't':
                    text.Append('\t');
                    break;
                case 'b':
                    text.Append('\b');
                    break;
                case 'f':
                    text.Append('\f');
                    break;
                default:
                    text.Append(e);
                    break;
            }
        }

        return text.ToString();
    }
}
