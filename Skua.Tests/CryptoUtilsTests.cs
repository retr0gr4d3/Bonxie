using Skua.Core.Models;
using Skua.Core.Utils;
using System.Text.Json;
using Xunit;

namespace Skua.Tests;

/// <summary>Each test gets its own key file, so nothing touches the real Bonxie folder.</summary>
[Collection("Secrets key")]
public sealed class CryptoUtilsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bonxie-tests-").FullName;
    private readonly string _previousKeyFile = CryptoUtils.KeyFilePath;

    public CryptoUtilsTests()
    {
        CryptoUtils.KeyFilePath = Path.Combine(_directory, "secrets.key");
    }

    public void Dispose()
    {
        CryptoUtils.KeyFilePath = _previousKeyFile;
        Directory.Delete(_directory, true);
    }

    [Fact]
    public void Encrypt_then_decrypt_gives_the_text_back()
    {
        string sealedText = CryptoUtils.Encrypt("hunter2 ünïcode");

        Assert.True(CryptoUtils.IsEncrypted(sealedText));
        Assert.DoesNotContain("hunter2", sealedText);
        Assert.Equal("hunter2 ünïcode", CryptoUtils.Decrypt(sealedText));
    }

    [Fact]
    public void Same_text_encrypts_differently_each_time()
    {
        Assert.NotEqual(CryptoUtils.Encrypt("same"), CryptoUtils.Encrypt("same"));
    }

    [Fact]
    public void Plain_text_is_returned_as_it_is()
    {
        Assert.Equal("not encrypted", CryptoUtils.Decrypt("not encrypted"));
        Assert.Equal(string.Empty, CryptoUtils.Decrypt(string.Empty));
    }

    [Fact]
    public void Text_sealed_with_another_key_decrypts_to_nothing()
    {
        string sealedText = CryptoUtils.Encrypt("secret");
        CryptoUtils.KeyFilePath = Path.Combine(_directory, "other.key");

        Assert.Equal(string.Empty, CryptoUtils.Decrypt(sealedText));
    }

    [Fact]
    public void Tampered_text_decrypts_to_nothing()
    {
        string sealedText = CryptoUtils.Encrypt("secret");
        // Change a character well inside the text: the last ones before the padding carry unused bits.
        int at = sealedText.Length / 2;
        char swapped = sealedText[at] == 'A' ? 'B' : 'A';
        string tampered = sealedText[..at] + swapped + sealedText[(at + 1)..];

        Assert.Equal(string.Empty, CryptoUtils.Decrypt(tampered));
    }

    [Fact]
    public void The_key_is_kept_between_loads()
    {
        string sealedText = CryptoUtils.Encrypt("secret");
        CryptoUtils.KeyFilePath = CryptoUtils.KeyFilePath; // drops the key held in memory

        Assert.Equal("secret", CryptoUtils.Decrypt(sealedText));
    }

    [Fact]
    public void Saved_accounts_hold_no_plain_password()
    {
        ManagerSettings settings = new();
        settings.ManagedAccounts["hero"] = new AccountData { DisplayName = "Hero", Password = "hunter2" };

        string json = JsonSerializer.Serialize(settings);
        ManagerSettings loaded = JsonSerializer.Deserialize<ManagerSettings>(json)!;

        Assert.DoesNotContain("hunter2", json);
        Assert.Equal("hunter2", loaded.ManagedAccounts["hero"].Password);
    }

    [Fact]
    public void Accounts_saved_in_plain_text_by_older_versions_still_load()
    {
        ManagerSettings loaded = JsonSerializer.Deserialize<ManagerSettings>(
            """{"ManagedAccounts":{"hero":{"DisplayName":"Hero","Password":"hunter2"}}}""")!;

        Assert.Equal("hunter2", loaded.ManagedAccounts["hero"].Password);
    }
}
