// Clean-room C# implementation of the PDF standard security handler, following
// the algorithms in the PDF specification (ISO 32000, §7.6) as also implemented
// by pdf.js `src/core/crypto.js`. Supports empty-user-password decryption for
// revisions 2–6 (RC4, AESV2/AES-128, AESV3/AES-256). See NOTICE.

using System.Security.Cryptography;

// MD5/AES are used only for optional encrypted-PDF support and are available on
// server/desktop hosting. They are not present in the browser (WASM) sandbox,
// where encrypted documents are simply unsupported; suppress the platform
// analyzer rather than fail the build for all consumers.
#pragma warning disable CA1416

namespace BlazorPdf.Core.Security;

internal enum CipherKind { None, Rc4, Aes128, Aes256 }

/// <summary>
/// Derives the document encryption key from an empty user password and decrypts
/// strings and streams for the standard security handler.
/// </summary>
public sealed class StandardSecurityHandler
{
    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private readonly byte[] _fileKey;
    private readonly int _revision;
    private readonly CipherKind _streamCipher;
    private readonly CipherKind _stringCipher;

    private StandardSecurityHandler(byte[] fileKey, int revision, CipherKind stream, CipherKind str)
    {
        _fileKey = fileKey;
        _revision = revision;
        _streamCipher = stream;
        _stringCipher = str;
    }

    /// <summary>Builds a handler from the <c>/Encrypt</c> dictionary, or <c>null</c> if unsupported.</summary>
    public static StandardSecurityHandler? TryCreate(Dict encrypt, byte[]? id0)
    {
        if ((encrypt.Get("Filter") as Name)?.Value is not "Standard")
        {
            return null;
        }

        int v = GetInt(encrypt.Get("V"), 0);
        int r = GetInt(encrypt.Get("R"), 0);
        int p = GetInt(encrypt.Get("P"), 0);
        byte[] o = GetStringBytes(encrypt.Get("O"));
        byte[] u = GetStringBytes(encrypt.Get("U"));
        int length = GetInt(encrypt.Get("Length"), 40);
        bool encryptMetadata = encrypt.Get("EncryptMetadata") is not bool em || em;

        (CipherKind stream, CipherKind str) = ResolveCiphers(encrypt, v);

        byte[] fileKey;
        if (r is >= 2 and <= 4)
        {
            fileKey = ComputeKeyLegacy(o, p, id0 ?? [], r, length, encryptMetadata);
        }
        else if (r is 5 or 6)
        {
            byte[] ue = GetStringBytes(encrypt.Get("UE"));
            fileKey = ComputeKeyR6(u, ue);
            if (fileKey.Length == 0)
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        return new StandardSecurityHandler(fileKey, r, stream, str);
    }

    /// <summary>Decrypts the raw bytes of a stream belonging to object <paramref name="num"/> <paramref name="gen"/>.</summary>
    public byte[] DecryptStream(byte[] data, int num, int gen) => Decrypt(data, num, gen, _streamCipher);

    /// <summary>Decrypts a string belonging to object <paramref name="num"/> <paramref name="gen"/>.</summary>
    public byte[] DecryptString(byte[] data, int num, int gen) => Decrypt(data, num, gen, _stringCipher);

    private byte[] Decrypt(byte[] data, int num, int gen, CipherKind cipher)
    {
        switch (cipher)
        {
            case CipherKind.None:
                return data;
            case CipherKind.Rc4:
                return Rc4.Transform(ObjectKey(num, gen, aes: false), data);
            case CipherKind.Aes128:
                return AesCbcDecrypt(ObjectKey(num, gen, aes: true), data);
            case CipherKind.Aes256:
                return AesCbcDecrypt(_fileKey, data); // V5 uses the file key directly
            default:
                return data;
        }
    }

    private byte[] ObjectKey(int num, int gen, bool aes)
    {
        // Algorithm 1: per-object key from the file key, object and generation.
        int n = _fileKey.Length;
        var input = new byte[n + 5 + (aes ? 4 : 0)];
        Array.Copy(_fileKey, input, n);
        input[n] = (byte)num;
        input[n + 1] = (byte)(num >> 8);
        input[n + 2] = (byte)(num >> 16);
        input[n + 3] = (byte)gen;
        input[n + 4] = (byte)(gen >> 8);
        if (aes)
        {
            input[n + 5] = 0x73; // 's'
            input[n + 6] = 0x41; // 'A'
            input[n + 7] = 0x6C; // 'l'
            input[n + 8] = 0x54; // 'T'
        }

        byte[] hash = MD5.HashData(input);
        int keyLen = Math.Min(n + 5, 16);
        return hash[..keyLen];
    }

    private static byte[] ComputeKeyLegacy(byte[] o, int p, byte[] id0, int r, int length, bool encryptMetadata)
    {
        // Algorithm 2 with an empty user password.
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(Padding); // padded empty password
        md5.AppendData(o.Length >= 32 ? o[..32] : Pad(o));
        md5.AppendData([(byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24)]);
        md5.AppendData(id0);
        if (r >= 4 && !encryptMetadata)
        {
            md5.AppendData([0xFF, 0xFF, 0xFF, 0xFF]);
        }
        byte[] hash = md5.GetHashAndReset();

        int n = r == 2 ? 5 : Math.Max(5, length / 8);
        if (r >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash[..n]);
            }
        }
        return hash[..n];
    }

    private static byte[] ComputeKeyR6(byte[] u, byte[] ue)
    {
        // Algorithm 2.A / 2.B with an empty user password.
        if (u.Length < 48 || ue.Length < 32)
        {
            return [];
        }
        byte[] keySalt = u[40..48];
        byte[] intermediate = Hash2B([], keySalt, []);
        // Decrypt UE with AES-256-CBC, no padding, zero IV.
        return AesCbcNoPadding(intermediate, new byte[16], ue);
    }

    private static byte[] Hash2B(byte[] password, byte[] salt, byte[] userData)
    {
        // Algorithm 2.B (revision 6 hardened hash).
        byte[] input = Concat(password, salt, userData);
        byte[] k = SHA256.HashData(input);

        int round = 0;
        while (true)
        {
            byte[] block = Concat(password, k, userData);
            var k1 = new byte[block.Length * 64];
            for (int i = 0; i < 64; i++)
            {
                Array.Copy(block, 0, k1, i * block.Length, block.Length);
            }

            byte[] key = k[..16];
            byte[] iv = k[16..32];
            byte[] e = AesCbcEncryptNoPadding(key, iv, k1);

            int mod = 0;
            for (int i = 0; i < 16; i++)
            {
                mod += e[i];
            }
            mod %= 3;

            k = mod switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };

            round++;
            if (round >= 64 && e[^1] <= round - 32)
            {
                break;
            }
        }
        return k[..32];
    }

    private static (CipherKind Stream, CipherKind Str) ResolveCiphers(Dict encrypt, int v)
    {
        if (v >= 5)
        {
            return (CipherKind.Aes256, CipherKind.Aes256);
        }
        if (v == 4 && encrypt.Get("CF") is Dict cf)
        {
            CipherKind Lookup(string filterKey)
            {
                string fname = (encrypt.Get(filterKey) as Name)?.Value ?? "Identity";
                if (fname == "Identity")
                {
                    return CipherKind.None;
                }
                if (cf.Get(fname) is Dict cfDict)
                {
                    string cfm = (cfDict.Get("CFM") as Name)?.Value ?? "V2";
                    return cfm switch
                    {
                        "AESV2" => CipherKind.Aes128,
                        "AESV3" => CipherKind.Aes256,
                        "Identity" => CipherKind.None,
                        _ => CipherKind.Rc4,
                    };
                }
                return CipherKind.Rc4;
            }
            return (Lookup("StmF"), Lookup("StrF"));
        }
        return (CipherKind.Rc4, CipherKind.Rc4);
    }

    private static byte[] AesCbcDecrypt(byte[] key, byte[] data)
    {
        if (data.Length < 16)
        {
            return [];
        }
        byte[] iv = data[..16];
        byte[] cipher = data[16..];
        if (cipher.Length == 0 || cipher.Length % 16 != 0)
        {
            return [];
        }
        byte[] plain = AesCbcNoPadding(key, iv, cipher);
        return StripPkcs7(plain);
    }

    private static byte[] AesCbcNoPadding(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AesCbcEncryptNoPadding(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] StripPkcs7(byte[] data)
    {
        if (data.Length == 0)
        {
            return data;
        }
        int pad = data[^1];
        if (pad is >= 1 and <= 16 && pad <= data.Length)
        {
            return data[..^pad];
        }
        return data;
    }

    private static byte[] Pad(byte[] value)
    {
        var result = new byte[32];
        int len = Math.Min(value.Length, 32);
        Array.Copy(value, result, len);
        Array.Copy(Padding, 0, result, len, 32 - len);
        return result;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = parts.Sum(p => p.Length);
        var result = new byte[total];
        int offset = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }

    private static int GetInt(object? value, int fallback) => value is double d ? (int)d : fallback;

    private static byte[] GetStringBytes(object? value) => value is PdfString s ? s.Bytes : [];
}
