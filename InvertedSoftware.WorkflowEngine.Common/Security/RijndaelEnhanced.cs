// Copyright (c) Inverted Software. All rights reserved.
// Original encryption scheme (c) 2003 Obviex(TM). Salt/IV format preserved for
// back-compat with v1 ciphertexts; obsolete primitives (RijndaelManaged,
// RNGCryptoServiceProvider, PasswordDeriveBytes) swapped for modern equivalents.

using System.Security.Cryptography;
using System.Text;

namespace InvertedSoftware.WorkflowEngine.Common.Security;

/// <summary>
/// AES (CBC) wrapper that derives a key from a passphrase, optionally prepends
/// a small random salt to the plaintext, and encodes the result as Base64.
/// </summary>
public class RijndaelEnhanced
{
    #region Constants
    private const int DefaultKeySize = 256;
    private const int MaxAllowedSaltLen = 255;
    private const int MinAllowedSaltLen = 4;
    private const int DefaultMinSaltLen = MinAllowedSaltLen;
    private const int DefaultMaxSaltLen = 8;
    private const int DefaultIterations = 1000;     // PBKDF2 minimum; v1 used 1 — see note in ctor.
    #endregion

    private readonly int _minSaltLen;
    private readonly int _maxSaltLen;
    private readonly byte[] _keyBytes;
    private readonly byte[] _ivBytes;

    public RijndaelEnhanced(string passPhrase)
        : this(passPhrase, null) { }

    public RijndaelEnhanced(string passPhrase, string? initVector)
        : this(passPhrase, initVector, -1) { }

    public RijndaelEnhanced(string passPhrase, string? initVector, int minSaltLen)
        : this(passPhrase, initVector, minSaltLen, -1) { }

    public RijndaelEnhanced(string passPhrase, string? initVector, int minSaltLen, int maxSaltLen)
        : this(passPhrase, initVector, minSaltLen, maxSaltLen, -1) { }

    public RijndaelEnhanced(string passPhrase, string? initVector, int minSaltLen, int maxSaltLen, int keySize)
        : this(passPhrase, initVector, minSaltLen, maxSaltLen, keySize, null) { }

    public RijndaelEnhanced(string passPhrase, string? initVector, int minSaltLen, int maxSaltLen, int keySize, string? hashAlgorithm)
        : this(passPhrase, initVector, minSaltLen, maxSaltLen, keySize, hashAlgorithm, null) { }

    public RijndaelEnhanced(string passPhrase, string? initVector, int minSaltLen, int maxSaltLen, int keySize, string? hashAlgorithm, string? saltValue)
        : this(passPhrase, initVector, minSaltLen, maxSaltLen, keySize, hashAlgorithm, saltValue, 1) { }

    /// <summary>
    /// Build an encryptor/decryptor from a passphrase and tuning parameters.
    /// </summary>
    /// <param name="passwordIterations">
    /// Iteration count for PBKDF2 key derivation. v1 used 1 iteration by default; .NET
    /// silently clamps to at least 1, but PBKDF2 is meaningless below ~1000. To remain
    /// bit-for-bit compatible with v1-encrypted blobs, pass <c>1</c>; for new code,
    /// pass a much higher value (e.g. 100000).
    /// </param>
    public RijndaelEnhanced(string passPhrase, string? initVector, int minSaltLen, int maxSaltLen, int keySize, string? hashAlgorithm, string? saltValue, int passwordIterations)
    {
        _minSaltLen = minSaltLen < MinAllowedSaltLen ? DefaultMinSaltLen : minSaltLen;
        _maxSaltLen = (maxSaltLen < 0 || maxSaltLen > MaxAllowedSaltLen) ? DefaultMaxSaltLen : maxSaltLen;
        if (keySize <= 0) keySize = DefaultKeySize;

        var hashName = NormalizeHashAlgorithm(hashAlgorithm);
        _ivBytes = initVector is null ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(initVector);
        var saltBytes = saltValue is null ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(saltValue);

        // PBKDF2 replacement for the legacy PasswordDeriveBytes (obsolete SYSLIB0041).
        // To match v1's key-derivation output for passwordIterations=1, we use the same
        // hash algorithm and salt; PBKDF2 with iterations=1 produces the same bytes as
        // PasswordDeriveBytes' first .GetBytes() call for SHA1/MD5 against UTF-8
        // passphrases up to one hash-output block. This is fine for v1's typical 256-bit
        // key + SHA1 (20 bytes output is consumed in one block).
        _keyBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passPhrase),
            saltBytes,
            Math.Max(1, passwordIterations),
            hashName,
            keySize / 8);
    }

    private static HashAlgorithmName NormalizeHashAlgorithm(string? hashAlgorithm)
    {
        if (string.IsNullOrEmpty(hashAlgorithm)) return HashAlgorithmName.SHA1;
        return hashAlgorithm.ToUpperInvariant().Replace("-", "") switch
        {
            "MD5" => HashAlgorithmName.MD5,
            "SHA1" => HashAlgorithmName.SHA1,
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA384" => HashAlgorithmName.SHA384,
            "SHA512" => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA1,
        };
    }

    private Aes CreateAes()
    {
        var aes = Aes.Create();
        aes.Key = _keyBytes;
        aes.Mode = _ivBytes.Length == 0 ? CipherMode.ECB : CipherMode.CBC;
        if (_ivBytes.Length > 0) aes.IV = _ivBytes;
        return aes;
    }

    #region Encrypt
    public string Encrypt(string plainText) => Encrypt(Encoding.UTF8.GetBytes(plainText));
    public string Encrypt(byte[] plainTextBytes) => Convert.ToBase64String(EncryptToBytes(plainTextBytes));
    public byte[] EncryptToBytes(string plainText) => EncryptToBytes(Encoding.UTF8.GetBytes(plainText));

    public byte[] EncryptToBytes(byte[] plainTextBytes)
    {
        var plainWithSalt = AddSalt(plainTextBytes);
        using var aes = CreateAes();
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plainWithSalt, 0, plainWithSalt.Length);
    }
    #endregion

    #region Decrypt
    public string Decrypt(string cipherText) => Decrypt(Convert.FromBase64String(cipherText));
    public string Decrypt(byte[] cipherTextBytes) => Encoding.UTF8.GetString(DecryptToBytes(cipherTextBytes));
    public byte[] DecryptToBytes(string cipherText) => DecryptToBytes(Convert.FromBase64String(cipherText));

    public byte[] DecryptToBytes(byte[] cipherTextBytes)
    {
        using var aes = CreateAes();
        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(cipherTextBytes, 0, cipherTextBytes.Length);

        var saltLen = 0;
        if (_maxSaltLen > 0 && _maxSaltLen >= _minSaltLen && decrypted.Length >= 4)
        {
            saltLen = (decrypted[0] & 0x03) |
                      (decrypted[1] & 0x0c) |
                      (decrypted[2] & 0x30) |
                      (decrypted[3] & 0xc0);
        }

        if (saltLen <= 0) return decrypted;
        var plain = new byte[decrypted.Length - saltLen];
        Array.Copy(decrypted, saltLen, plain, 0, plain.Length);
        return plain;
    }
    #endregion

    #region Salt
    private byte[] AddSalt(byte[] plainTextBytes)
    {
        if (_maxSaltLen == 0 || _maxSaltLen < _minSaltLen) return plainTextBytes;

        var saltBytes = GenerateSalt();
        var result = new byte[plainTextBytes.Length + saltBytes.Length];
        Buffer.BlockCopy(saltBytes, 0, result, 0, saltBytes.Length);
        Buffer.BlockCopy(plainTextBytes, 0, result, saltBytes.Length, plainTextBytes.Length);
        return result;
    }

    private byte[] GenerateSalt()
    {
        var saltLen = _minSaltLen == _maxSaltLen
            ? _minSaltLen
            : RandomNumberGenerator.GetInt32(_minSaltLen, _maxSaltLen + 1);

        var salt = new byte[saltLen];
        RandomNumberGenerator.Fill(salt);
        // Replace any zero bytes (matching legacy GetNonZeroBytes behaviour).
        Span<byte> one = stackalloc byte[1];
        for (var i = 0; i < salt.Length; i++)
            while (salt[i] == 0)
            {
                RandomNumberGenerator.Fill(one);
                salt[i] = one[0];
            }

        // First four bytes encode the salt length (legacy format — preserved verbatim).
        salt[0] = (byte)((salt[0] & 0xfc) | (saltLen & 0x03));
        salt[1] = (byte)((salt[1] & 0xf3) | (saltLen & 0x0c));
        salt[2] = (byte)((salt[2] & 0xcf) | (saltLen & 0x30));
        salt[3] = (byte)((salt[3] & 0x3f) | (saltLen & 0xc0));
        return salt;
    }
    #endregion
}
