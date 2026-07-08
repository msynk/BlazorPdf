using System.Security.Cryptography;
using System.Text;
using BlazorPdf.Core.Security;

namespace BlazorPdf.Tests;

/// <summary>
/// Phase 4.14: the managed MD5/AES used for WebAssembly must produce byte-for-byte
/// the same output as the platform implementations (verified here on the server).
/// </summary>
public class ManagedCryptoTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("abc")]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    [InlineData("message digest 0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz")]
    public void Managed_md5_matches_platform(string text)
    {
        byte[] input = Encoding.ASCII.GetBytes(text);
        Assert.Equal(MD5.HashData(input), ManagedMd5.Hash(input));
    }

    [Theory]
    [InlineData(16)] // AES-128
    [InlineData(32)] // AES-256
    public void Managed_aes_cbc_roundtrips_against_platform(int keyLen)
    {
        var key = new byte[keyLen];
        var iv = new byte[16];
        var plain = new byte[64];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 7 + 1);
        for (int i = 0; i < iv.Length; i++) iv[i] = (byte)(i * 3 + 5);
        for (int i = 0; i < plain.Length; i++) plain[i] = (byte)(i * 11 + 2);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        byte[] cipher = aes.EncryptCbc(plain, iv, PaddingMode.None);

        // Managed encrypt matches platform, and managed decrypt recovers plaintext.
        Assert.Equal(cipher, ManagedAes.CbcEncrypt(key, iv, plain));
        Assert.Equal(plain, ManagedAes.CbcDecrypt(key, iv, cipher));
    }
}
