// Clean-room C# port of the cross-reference reader from pdf.js
// `src/core/xref.js`. See NOTICE.

using System.Text;
using BlazorPdf.Core.Filters;
using BlazorPdf.Core.Security;

namespace BlazorPdf.Core;

/// <summary>
/// Reads a PDF cross-reference table or cross-reference stream (PDF 1.5+),
/// follows <c>/Prev</c> and hybrid <c>/XRefStm</c> chains, and resolves
/// indirect references — including objects packed inside object streams.
/// </summary>
public sealed class XRef : IXRef
{
    private enum EntryType { Free, Uncompressed, Compressed }

    private readonly struct Entry
    {
        public EntryType Type { get; init; }
        public int Field2 { get; init; } // offset, or containing ObjStm number
        public int Field3 { get; init; } // generation, or index within ObjStm
    }

    private readonly byte[] _buffer;
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly Dictionary<int, object?> _cache = new();
    private readonly Dictionary<int, List<object?>> _objStmCache = new();
    private readonly HashSet<int> _pending = new();

    private StandardSecurityHandler? _security;
    private int _encryptRefNum = -1;

    /// <summary>The combined trailer dictionary (newest section wins).</summary>
    public Dict? Trailer { get; private set; }

    public XRef(byte[] buffer) => _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

    /// <summary>The document catalog (<c>/Root</c>), resolved through the trailer.</summary>
    public Dict? Root => Trailer?.Get("Root") as Dict;

    /// <summary>Parses the cross-reference data starting from the file's <c>startxref</c>.</summary>
    public void Parse()
    {
        int start = FindStartXRef();
        if (start < 0)
        {
            throw new PdfFormatException("Could not locate 'startxref'.");
        }

        var queue = new Queue<int>();
        var visited = new HashSet<int>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            int offset = queue.Dequeue();
            if (offset < 0 || offset >= _buffer.Length || !visited.Add(offset))
            {
                continue;
            }

            Dict? sectionTrailer = ReadSection(offset);
            if (sectionTrailer is null)
            {
                continue;
            }

            MergeTrailer(sectionTrailer);

            if (sectionTrailer.GetRaw("XRefStm") is double xrefStm)
            {
                queue.Enqueue((int)xrefStm);
            }
            if (sectionTrailer.GetRaw("Prev") is double prev)
            {
                queue.Enqueue((int)prev);
            }
        }

        if (Trailer is null)
        {
            throw new PdfFormatException("No trailer found.");
        }

        SetupEncryption();
    }

    private void SetupEncryption()
    {
        object? encRaw = Trailer!.GetRaw("Encrypt");
        if (encRaw is null)
        {
            return;
        }
        if (encRaw is Ref er)
        {
            _encryptRefNum = er.Num;
        }

        // The Encrypt dictionary itself is never encrypted; security is still
        // null here, so fetching it returns the plaintext dictionary.
        if (FetchIfRef(encRaw) is not Dict encDict)
        {
            return;
        }

        byte[]? id0 = null;
        if (Trailer.Get("ID") is List<object?> idArr && idArr.Count > 0 && idArr[0] is PdfString s)
        {
            id0 = s.Bytes;
        }

        _security = StandardSecurityHandler.TryCreate(encDict, id0);
    }

    private Dict? ReadSection(int offset)
    {
        var probe = new Lexer(new PdfStream(_buffer, offset));
        object first = probe.GetObj();

        if (first is Cmd { Value: "xref" })
        {
            return ReadXRefTable(new Lexer(new PdfStream(_buffer, probe.Pos - 1)));
        }

        // Otherwise it must be an indirect xref stream object: "n g obj << >> stream".
        return ReadXRefStream(offset);
    }

    private Dict ReadXRefTable(Lexer lexer)
    {
        while (true)
        {
            object token = lexer.GetObj();
            if (token is Cmd { Value: "trailer" })
            {
                break;
            }
            if (ReferenceEquals(token, Primitives.EOF))
            {
                throw new PdfFormatException("Unexpected end of xref table.");
            }
            if (token is not double startObj)
            {
                throw new PdfFormatException("Malformed xref subsection header.");
            }

            object countObj = lexer.GetObj();
            if (countObj is not double countD)
            {
                throw new PdfFormatException("Malformed xref subsection count.");
            }

            int subStart = (int)startObj;
            int count = (int)countD;
            for (int i = 0; i < count; i++)
            {
                object offsetTok = lexer.GetObj();
                object genTok = lexer.GetObj();
                object typeTok = lexer.GetObj();

                if (offsetTok is not double off || genTok is not double gen)
                {
                    throw new PdfFormatException("Malformed xref entry.");
                }

                int num = subStart + i;
                bool free = typeTok is Cmd { Value: "f" };
                if (!_entries.ContainsKey(num))
                {
                    _entries[num] = new Entry
                    {
                        Type = free ? EntryType.Free : EntryType.Uncompressed,
                        Field2 = (int)off,
                        Field3 = (int)gen,
                    };
                }
            }
        }

        // After the "trailer" keyword the dictionary follows.
        var parser = new Parser(lexer, this, allowStreams: false);
        return parser.GetObj() as Dict
            ?? throw new PdfFormatException("Trailer is not a dictionary.");
    }

    private Dict ReadXRefStream(int offset)
    {
        var parser = new Parser(new Lexer(new PdfStream(_buffer, offset)), this);
        if (parser.GetObj() is not PdfStream stream || stream.Dict is null)
        {
            throw new PdfFormatException("Expected an xref stream object.");
        }

        Dict dict = stream.Dict;
        byte[] data = StreamDecoder.Decode(stream);

        if (dict.Get("W") is not List<object?> w || w.Count < 3)
        {
            throw new PdfFormatException("xref stream missing /W.");
        }
        int w0 = ToInt(w[0]);
        int w1 = ToInt(w[1]);
        int w2 = ToInt(w[2]);
        int entryLen = w0 + w1 + w2;
        if (entryLen == 0)
        {
            throw new PdfFormatException("Invalid /W in xref stream.");
        }

        // /Index pairs default to [0, Size].
        var index = new List<int>();
        if (dict.Get("Index") is List<object?> idx)
        {
            foreach (var v in idx)
            {
                index.Add(ToInt(v));
            }
        }
        else
        {
            index.Add(0);
            index.Add(ToInt(dict.Get("Size")));
        }

        int pos = 0;
        for (int section = 0; section + 1 < index.Count; section += 2)
        {
            int objStart = index[section];
            int objCount = index[section + 1];
            for (int i = 0; i < objCount && pos + entryLen <= data.Length; i++)
            {
                long f1 = w0 == 0 ? 1 : ReadField(data, pos, w0);
                long f2 = ReadField(data, pos + w0, w1);
                long f3 = ReadField(data, pos + w0 + w1, w2);
                pos += entryLen;

                int num = objStart + i;
                if (_entries.ContainsKey(num))
                {
                    continue;
                }

                _entries[num] = f1 switch
                {
                    0 => new Entry { Type = EntryType.Free, Field2 = (int)f2, Field3 = (int)f3 },
                    1 => new Entry { Type = EntryType.Uncompressed, Field2 = (int)f2, Field3 = (int)f3 },
                    2 => new Entry { Type = EntryType.Compressed, Field2 = (int)f2, Field3 = (int)f3 },
                    _ => new Entry { Type = EntryType.Free },
                };
            }
        }

        return dict;
    }

    private void MergeTrailer(Dict section)
    {
        if (Trailer is null)
        {
            Trailer = section;
            return;
        }
        foreach (var key in section.Keys)
        {
            if (!Trailer.Has(key))
            {
                Trailer.Set(key, section.GetRaw(key));
            }
        }
    }

    /// <inheritdoc/>
    public object? FetchIfRef(object? value, bool suppressEncryption = false)
        => value is Ref r ? Fetch(r, suppressEncryption) : value;

    /// <inheritdoc/>
    public object? Fetch(Ref reference, bool suppressEncryption = false)
    {
        if (_cache.TryGetValue(reference.Num, out var cached))
        {
            return cached;
        }
        if (!_entries.TryGetValue(reference.Num, out var entry) || entry.Type == EntryType.Free)
        {
            return null;
        }
        if (!_pending.Add(reference.Num))
        {
            return null; // cycle
        }

        try
        {
            object? result = entry.Type == EntryType.Compressed
                ? FetchCompressed(entry)
                : FetchUncompressed(reference, entry);
            _cache[reference.Num] = result;
            return result;
        }
        finally
        {
            _pending.Remove(reference.Num);
        }
    }

    private object? FetchUncompressed(Ref reference, Entry entry)
    {
        if (entry.Field2 < 0 || entry.Field2 >= _buffer.Length)
        {
            return null;
        }
        var parser = new Parser(new Lexer(new PdfStream(_buffer, entry.Field2)), this);
        object? obj = parser.GetObj();

        if (_security is not null && reference.Num != _encryptRefNum)
        {
            obj = DecryptObject(obj, reference.Num, reference.Gen);
        }
        return obj;
    }

    private object? DecryptObject(object? obj, int num, int gen)
    {
        switch (obj)
        {
            case PdfString s:
                return new PdfString(_security!.DecryptString(s.Bytes, num, gen));

            case List<object?> list:
                for (int i = 0; i < list.Count; i++)
                {
                    list[i] = DecryptObject(list[i], num, gen);
                }
                return list;

            case Dict dict:
                DecryptDictStrings(dict, num, gen);
                return dict;

            case PdfStream stream when stream.Dict is not null:
                DecryptDictStrings(stream.Dict, num, gen);
                // Cross-reference streams are never encrypted.
                if (Primitives.IsName(stream.Dict.Get("Type"), "XRef"))
                {
                    return stream;
                }
                stream.Reset();
                byte[] raw = stream.GetBytes();
                byte[] decrypted = _security!.DecryptStream(raw, num, gen);
                return new PdfStream(decrypted, 0, decrypted.Length, stream.Dict);

            default:
                return obj;
        }
    }

    private void DecryptDictStrings(Dict dict, int num, int gen)
    {
        foreach (var key in dict.Keys.ToList())
        {
            object? raw = dict.GetRaw(key);
            // Indirect references are not decrypted; their targets are when fetched.
            if (raw is Ref)
            {
                continue;
            }
            dict.Set(key, DecryptObject(raw, num, gen));
        }
    }

    private object? FetchCompressed(Entry entry)
    {
        var objects = GetObjectStream(entry.Field2);
        return entry.Field3 >= 0 && entry.Field3 < objects.Count ? objects[entry.Field3] : null;
    }

    private List<object?> GetObjectStream(int streamNum)
    {
        if (_objStmCache.TryGetValue(streamNum, out var cached))
        {
            return cached;
        }

        var result = new List<object?>();
        _objStmCache[streamNum] = result; // guard re-entrancy

        if (Fetch(new Ref(streamNum, 0)) is not PdfStream stream || stream.Dict is null)
        {
            return result;
        }

        Dict dict = stream.Dict;
        int n = ToInt(dict.Get("N"));
        int first = ToInt(dict.Get("First"));
        byte[] data = StreamDecoder.Decode(stream);

        // Header: N pairs of "objNum offset".
        var headerLexer = new Lexer(new PdfStream(data));
        var offsets = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            _ = headerLexer.GetObj();          // object number (unused for positional access)
            object offTok = headerLexer.GetObj();
            offsets.Add(offTok is double d ? (int)d : 0);
        }

        for (int i = 0; i < n; i++)
        {
            int objStart = first + offsets[i];
            if (objStart < 0 || objStart >= data.Length)
            {
                result.Add(null);
                continue;
            }
            var parser = new Parser(new Lexer(new PdfStream(data, objStart)), this);
            result.Add(parser.GetObj());
        }

        return result;
    }

    private int FindStartXRef()
    {
        var keyword = "startxref"u8.ToArray();
        int searchStart = Math.Max(0, _buffer.Length - 2048);
        for (int i = _buffer.Length - keyword.Length; i >= searchStart; i--)
        {
            if (MatchesAt(i, keyword))
            {
                var lexer = new Lexer(new PdfStream(_buffer, i + keyword.Length));
                return lexer.GetObj() is double d ? (int)d : -1;
            }
        }
        return -1;
    }

    private bool MatchesAt(int at, byte[] keyword)
    {
        if (at < 0 || at + keyword.Length > _buffer.Length)
        {
            return false;
        }
        for (int k = 0; k < keyword.Length; k++)
        {
            if (_buffer[at + k] != keyword[k])
            {
                return false;
            }
        }
        return true;
    }

    private static long ReadField(byte[] data, int pos, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++)
        {
            value = (value << 8) | data[pos + i];
        }
        return value;
    }

    private static int ToInt(object? value) => value switch
    {
        double d => (int)d,
        _ => 0,
    };
}
