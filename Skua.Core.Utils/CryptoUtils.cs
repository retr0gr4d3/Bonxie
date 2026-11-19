using System.Security.Cryptography;
using System.Text;

namespace Skua.Core.Utils;

/// <summary>
/// Encrypts secrets kept in the settings file (account passwords).
/// <para>
/// Values are sealed with AES-256-GCM under a random key kept in <see cref="KeyFilePath"/>. On Windows that key file is
/// wrapped with DPAPI for the current user, so a copy of the Bonxie folder is useless on another account or machine;
/// elsewhere the file is readable by its owner only.
/// </para>
/// </summary>
public static class CryptoUtils
{
    private const string Prefix = "enc2:";
    private const string LegacyPrefix = "enc:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly object _keyLock = new();
    private static byte[]? _key;
    private static string _keyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bonxie", "secrets.key");

    /// <summary>Where the key is kept. It sits beside the settings file it protects.</summary>
    public static string KeyFilePath
    {
        get => _keyFilePath;
        set
        {
            lock (_keyLock)
            {
                _keyFilePath = value;
                _key = null;
            }
        }
    }

    /// <summary>True when <paramref name="value"/> is text made by <see cref="Encrypt"/>, in this or the old format.</summary>
    public static bool IsEncrypted(string? value)
    {
        return value is not null
            && (value.StartsWith(Prefix, StringComparison.Ordinal) || value.StartsWith(LegacyPrefix, StringComparison.Ordinal));
    }

    /// <summary>Encrypts <paramref name="plainText"/>. Throws when the key can't be read or made, so a secret is never written in the clear.</summary>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        byte[] plain = Encoding.UTF8.GetBytes(plainText);
        byte[] sealedBytes = new byte[NonceSize + TagSize + plain.Length];
        Span<byte> nonce = sealedBytes.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using AesGcm aes = new(GetKey(), TagSize);
        aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceSize + TagSize), sealedBytes.AsSpan(NonceSize, TagSize));
        return Prefix + Convert.ToBase64String(sealedBytes);
    }

    /// <summary>
    /// Decrypts text made by <see cref="Encrypt"/>; other text is returned as it is. Text that can't be decrypted
    /// (lost key, settings copied from another machine) gives an empty string, never the ciphertext.
    /// </summary>
    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return cipherText;

        try
        {
            if (cipherText.StartsWith(Prefix, StringComparison.Ordinal))
            {
                byte[] sealedBytes = Convert.FromBase64String(cipherText[Prefix.Length..]);
                byte[] plain = new byte[sealedBytes.Length - NonceSize - TagSize];
                using AesGcm aes = new(GetKey(), TagSize);
                aes.Decrypt(sealedBytes.AsSpan(0, NonceSize), sealedBytes.AsSpan(NonceSize + TagSize), sealedBytes.AsSpan(NonceSize, TagSize), plain);
                return Encoding.UTF8.GetString(plain);
            }

            if (cipherText.StartsWith(LegacyPrefix, StringComparison.Ordinal))
                return DecryptLegacy(cipherText[LegacyPrefix.Length..]);

            return cipherText;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[CryptoUtils.Decrypt] Failed: {ex.Message}");
            return string.Empty;
        }
    }

    private static byte[] GetKey()
    {
        lock (_keyLock)
        {
            return _key ??= LoadOrCreateKey(_keyFilePath);
        }
    }

    // The manager and every client share this file, so the first one to finish writing it wins and the rest read it.
    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
            return Unwrap(File.ReadAllBytes(path));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (FileStream stream = new(temporary, options))
            {
                stream.Write(Wrap(key));
                stream.Flush(true);
            }

            try
            {
                File.Move(temporary, path, overwrite: false);
                return key;
            }
            catch (IOException) when (File.Exists(path))
            {
                return Unwrap(File.ReadAllBytes(path));
            }
        }
        finally
        {
            try { File.Delete(temporary); } catch { /* moved, or never written */ }
        }
    }

    private static byte[] Wrap(byte[] key)
    {
        return OperatingSystem.IsWindows() ? ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser) : key;
    }

    private static byte[] Unwrap(byte[] stored)
    {
        byte[] key = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser) : stored;
        return key.Length == KeySize ? key : throw new CryptographicException("The secrets key file is damaged.");
    }

    // Reads values written by older versions: AES-CBC with a key and IV derived from the machine and user name.
    private static string DecryptLegacy(string base64)
    {
        byte[] salt = { 0x49, 0x76, 0x61, 0x6e, 0x20, 0x4d, 0x65, 0x64, 0x76, 0x65, 0x64, 0x65, 0x76 };
        string passPhrase = $"{Environment.MachineName}-{Environment.UserName}-SkuaSecureKey";

        using Aes aes = Aes.Create();
        aes.Key = Rfc2898DeriveBytes.Pbkdf2(passPhrase, salt, 10000, HashAlgorithmName.SHA256, 32);
        aes.IV = Rfc2898DeriveBytes.Pbkdf2("SkuaStaticIV-DoNotChange", salt, 10000, HashAlgorithmName.SHA256, 16);
        return Encoding.UTF8.GetString(aes.DecryptCbc(Convert.FromBase64String(base64), aes.IV));
    }
}
