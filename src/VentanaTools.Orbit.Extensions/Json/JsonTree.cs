// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace VentanaTools.Orbit.Extensions;

/// <summary>The kind of a <see cref="JsonNode"/>.</summary>
internal enum JsonKind
{
    Object = 1,
    Array = 2,
    String = 3,
    Number = 4,
    True = 5,
    False = 6,
    Null = 7,

    /// <summary>An enumeration value with no token, from a model built in code.</summary>
    Undefined = 8,
}

/// <summary>
/// One value of a strictly parsed JSON document, or of a model converted for validation.
/// Every node read from bytes records the byte offset of its token, so a diagnostic can
/// carry a line and column.
/// </summary>
internal sealed class JsonNode
{
    public required JsonKind Kind { get; init; }

    /// <summary>The token's byte offset in the parsed document; -1 for a node built in code.</summary>
    public int Offset { get; init; } = -1;

    /// <summary>A string's value, or a number's raw text. Null for a raw string (see <see cref="IsRaw"/>).</summary>
    public string? Text { get; init; }

    /// <summary>Whether a string kept only its token position because it may hold a secret.</summary>
    public bool IsRaw { get; init; }

    /// <summary>A raw string's token length in bytes, quotes included.</summary>
    public int RawLength { get; init; }

    /// <summary>A null that stands for a null member or element of a model built in code.</summary>
    public bool FromCode { get; init; }

    public List<JsonMember>? Members { get; init; }

    public List<JsonNode>? Items { get; init; }

    public static JsonNode String(string value) => new() { Kind = JsonKind.String, Text = value };

    public static JsonNode Integer(long value) =>
        new() { Kind = JsonKind.Number, Text = value.ToString(CultureInfo.InvariantCulture) };

    public static JsonNode CodeNull() => new() { Kind = JsonKind.Null, FromCode = true };

    public static JsonNode Undefined() => new() { Kind = JsonKind.Undefined };

    public static JsonNode NewObject() => new() { Kind = JsonKind.Object, Members = [] };

    public static JsonNode NewArray() => new() { Kind = JsonKind.Array, Items = [] };

    public JsonNode Add(string name, JsonNode value)
    {
        Members!.Add(new JsonMember(name, value));
        return this;
    }

    /// <summary>A number token without a fraction or exponent that fits in a long.</summary>
    public bool TryGetInteger(out long value)
    {
        value = 0;
        if (Kind != JsonKind.Number || Text is not { Length: > 0 } text)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!(char.IsAsciiDigit(c) || c == '-'))
            {
                return false;
            }
        }

        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>One member of an object node.</summary>
internal sealed class JsonMember(string name, JsonNode value)
{
    public string Name { get; } = name;

    public JsonNode Value { get; } = value;
}

/// <summary>Why a document could not be parsed.</summary>
internal enum JsonFailure
{
    None = 0,
    Utf8 = 1,
    Syntax = 2,
    Depth = 3,
}

/// <summary>The outcome of <see cref="JsonTree.Parse"/>.</summary>
internal sealed class JsonParse
{
    public JsonNode? Root { get; init; }

    public JsonFailure Failure { get; init; }

    /// <summary>The JSON Pointer of the value being read when parsing failed.</summary>
    public string FailurePath { get; init; } = string.Empty;

    /// <summary>1-based line of the failure, when known.</summary>
    public long? FailureLine { get; init; }

    /// <summary>1-based UTF-8 byte column of the failure, when known.</summary>
    public long? FailureColumn { get; init; }

    /// <summary>Repeated members: the pointer of each repeat and its value. The first occurrence is kept.</summary>
    public List<(string Path, JsonNode Value)> Duplicates { get; init; } = [];
}

/// <summary>
/// A strict JSON reader on <see cref="Utf8JsonReader"/>: one value, no comments or trailing
/// commas, valid UTF-8, no unpaired surrogates after unescaping, at most 8 levels of nesting,
/// and member names compared ordinally after unescaping.
/// </summary>
internal static class JsonTree
{
    public const int MaxDepth = 8;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Parses <paramref name="document"/>. A UTF-8 byte order mark must already be removed.
    /// String members of the root object named in <paramref name="rawRootMembers"/> are not
    /// materialized; read them with <see cref="CopyRawUtf8"/>.
    /// </summary>
    public static JsonParse Parse(ReadOnlySpan<byte> document, IReadOnlySet<string>? rawRootMembers = null)
    {
        if (!Utf8.IsValid(document))
        {
            var offset = FirstInvalidUtf8(document);
            var (line, column) = Position(document, offset);
            return new JsonParse { Failure = JsonFailure.Utf8, FailureLine = line, FailureColumn = column };
        }

        var reader = new Utf8JsonReader(document, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxDepth + 4,
        });

        var frames = new List<Frame>();
        var duplicates = new List<(string Path, JsonNode Value)>();
        JsonNode? root = null;
        try
        {
            while (reader.Read())
            {
                var offset = checked((int)reader.TokenStartIndex);
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                    {
                        var path = PathOfNext(frames);
                        if (frames.Count + 1 > MaxDepth)
                        {
                            var (line, column) = Position(document, offset);
                            return new JsonParse
                            {
                                Failure = JsonFailure.Depth,
                                FailurePath = path,
                                FailureLine = line,
                                FailureColumn = column,
                            };
                        }

                        var node = reader.TokenType == JsonTokenType.StartObject
                            ? new JsonNode { Kind = JsonKind.Object, Offset = offset, Members = [] }
                            : new JsonNode { Kind = JsonKind.Array, Offset = offset, Items = [] };
                        Attach(frames, node, ref root, duplicates);
                        frames.Add(new Frame(node, path));
                        break;
                    }

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        frames.RemoveAt(frames.Count - 1);
                        break;

                    case JsonTokenType.PropertyName:
                    {
                        var frame = frames[^1];
                        var name = reader.GetString()!;
                        frame.PendingName = name;
                        frame.PendingDuplicate = !frame.Seen.Add(name);
                        break;
                    }

                    case JsonTokenType.String:
                    {
                        JsonNode node;
                        if (rawRootMembers is not null && frames.Count == 1 && frames[0].Node.Kind == JsonKind.Object
                            && frames[0].PendingName is { } member && rawRootMembers.Contains(member))
                        {
                            CheckRaw(ref reader);
                            node = new JsonNode
                            {
                                Kind = JsonKind.String,
                                Offset = offset,
                                IsRaw = true,
                                RawLength = checked((int)(reader.BytesConsumed - reader.TokenStartIndex)),
                            };
                        }
                        else
                        {
                            node = new JsonNode { Kind = JsonKind.String, Offset = offset, Text = reader.GetString() };
                        }

                        Attach(frames, node, ref root, duplicates);
                        break;
                    }

                    case JsonTokenType.Number:
                        Attach(frames, new JsonNode
                        {
                            Kind = JsonKind.Number,
                            Offset = offset,
                            Text = Encoding.UTF8.GetString(reader.ValueSpan),
                        }, ref root, duplicates);
                        break;

                    case JsonTokenType.True:
                        Attach(frames, new JsonNode { Kind = JsonKind.True, Offset = offset }, ref root, duplicates);
                        break;

                    case JsonTokenType.False:
                        Attach(frames, new JsonNode { Kind = JsonKind.False, Offset = offset }, ref root, duplicates);
                        break;

                    case JsonTokenType.Null:
                        Attach(frames, new JsonNode { Kind = JsonKind.Null, Offset = offset }, ref root, duplicates);
                        break;

                    default:
                        throw new JsonException();
                }
            }
        }
        catch (JsonException ex)
        {
            return new JsonParse
            {
                Failure = JsonFailure.Syntax,
                FailurePath = PathOfNext(frames),
                FailureLine = (ex.LineNumber ?? 0) + 1,
                FailureColumn = (ex.BytePositionInLine ?? 0) + 1,
            };
        }
        catch (InvalidOperationException)
        {
            // An escape that forms an unpaired surrogate, or text that does not decode.
            var (line, column) = Position(document, checked((int)reader.TokenStartIndex));
            return new JsonParse
            {
                Failure = JsonFailure.Syntax,
                FailurePath = PathOfNext(frames),
                FailureLine = line,
                FailureColumn = column,
            };
        }

        if (root is null || frames.Count != 0)
        {
            return new JsonParse { Failure = JsonFailure.Syntax, FailureLine = 1, FailureColumn = 1 };
        }

        return new JsonParse { Root = root, Duplicates = duplicates };
    }

    /// <summary>
    /// Unescapes a raw string node's value as UTF-8 into <paramref name="destination"/>. Returns
    /// the number of bytes written, or -1 when it does not fit.
    /// </summary>
    public static int CopyRawUtf8(ReadOnlySpan<byte> document, JsonNode node, Span<byte> destination)
    {
        var token = document.Slice(node.Offset, node.RawLength);
        var reader = new Utf8JsonReader(token);
        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
        {
            return -1;
        }

        var length = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        if (length > destination.Length)
        {
            return -1;
        }

        return reader.CopyString(destination);
    }

    /// <summary>1-based line and UTF-8 byte column of <paramref name="offset"/>.</summary>
    public static (long Line, long Column) Position(ReadOnlySpan<byte> document, int offset)
    {
        var before = document[..Math.Clamp(offset, 0, document.Length)];
        var lastNewline = before.LastIndexOf((byte)'\n');
        return (before.Count((byte)'\n') + 1, before.Length - lastNewline);
    }

    /// <summary>Whether <paramref name="text"/> decodes strictly as UTF-8.</summary>
    public static bool IsStrictUtf8(ReadOnlySpan<byte> text)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(text);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static int FirstInvalidUtf8(ReadOnlySpan<byte> document)
    {
        var offset = 0;
        while (offset < document.Length)
        {
            if (System.Text.Rune.DecodeFromUtf8(document[offset..], out _, out var consumed) != OperationStatus.Done)
            {
                return offset;
            }

            offset += consumed;
        }

        return offset;
    }

    private static void CheckRaw(ref Utf8JsonReader reader)
    {
        if (!reader.ValueIsEscaped)
        {
            return;
        }

        var length = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            reader.CopyString(buffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void Attach(List<Frame> frames, JsonNode node, ref JsonNode? root, List<(string Path, JsonNode Value)> duplicates)
    {
        if (frames.Count == 0)
        {
            root = node;
            return;
        }

        var frame = frames[^1];
        if (frame.Node.Kind == JsonKind.Object)
        {
            var name = frame.PendingName!;
            if (frame.PendingDuplicate)
            {
                duplicates.Add((frame.Path + "/" + JsonPointer.Escape(name), node));
            }
            else
            {
                frame.Node.Members!.Add(new JsonMember(name, node));
            }

            frame.PendingName = null;
            frame.PendingDuplicate = false;
        }
        else
        {
            frame.Node.Items!.Add(node);
            frame.Index++;
        }
    }

    private static string PathOfNext(List<Frame> frames)
    {
        if (frames.Count == 0)
        {
            return string.Empty;
        }

        var frame = frames[^1];
        if (frame.Node.Kind == JsonKind.Object)
        {
            return frame.PendingName is null ? frame.Path : frame.Path + "/" + JsonPointer.Escape(frame.PendingName);
        }

        return frame.Path + "/" + frame.Index.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class Frame(JsonNode node, string path)
    {
        public JsonNode Node { get; } = node;

        public string Path { get; } = path;

        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

        public string? PendingName { get; set; }

        public bool PendingDuplicate { get; set; }

        public int Index { get; set; }
    }
}

/// <summary>RFC 6901 JSON Pointer escaping, and the path rule of contract §4.1.</summary>
internal static class JsonPointer
{
    /// <summary>Escapes one reference token: <c>~</c> as <c>~0</c>, <c>/</c> as <c>~1</c>.</summary>
    public static string Escape(string token) =>
        token.Contains('~', StringComparison.Ordinal) || token.Contains('/', StringComparison.Ordinal)
            ? token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)
            : token;

    public static string Append(string path, string token) => path + "/" + Escape(token);

    public static string Append(string path, int index) => path + "/" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The pointer of an unknown member: the parent's path when the name is longer than 64
    /// characters or holds anything but printable ASCII, so hostile files cannot make a host
    /// display arbitrary text.
    /// </summary>
    public static string ForUnknown(string parent, string name, out bool truncated)
    {
        truncated = name.Length > 64 || !name.All(c => c is >= ' ' and <= '~');
        return truncated ? parent : Append(parent, name);
    }
}
