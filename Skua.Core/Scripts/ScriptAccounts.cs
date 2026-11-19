using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Core.Scripts;

public class ScriptAccounts : IScriptAccounts
{
    private readonly Lazy<IScriptPlayer> _lazyPlayer;
    private readonly ISettingsService _settingsService;
    private IScriptPlayer Player => _lazyPlayer.Value;

    public ScriptAccounts(Lazy<IScriptPlayer> player, ISettingsService settingsService)
    {
        _lazyPlayer = player;
        _settingsService = settingsService;
    }

    public List<string> GetTags()
    {
        string? username = Player.Username;
        return string.IsNullOrEmpty(username) ? new List<string>() : GetTags(username);
    }

    public List<string> GetTags(string username)
    {
        Dictionary<string, AccountData>? accounts = GetAccountsDictionary();
        return accounts == null
            ? new List<string>()
            : accounts.TryGetValue(username, out AccountData? accountData) ? accountData.Tags.ToList() : new List<string>();
    }

    public bool HasTag(string tag)
    {
        string? username = Player.Username;
        return !string.IsNullOrEmpty(username) && HasTag(username, tag);
    }

    public bool HasTag(string username, string tag)
    {
        List<string> tags = GetTags(username);
        return tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    public bool AddTag(string tag)
    {
        string? username = Player.Username;
        return !string.IsNullOrEmpty(username) && AddTag(username, tag);
    }

    public bool AddTag(string username, string tag)
    {
        Dictionary<string, AccountData>? accounts = GetAccountsDictionary();
        if (accounts == null)
            return false;

        if (!accounts.TryGetValue(username, out AccountData? accountData))
            return false;

        if (accountData.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            return false;

        accountData.Tags.Add(tag);
        _settingsService.Set("ManagedAccounts", accounts);
        return true;
    }

    public bool RemoveTag(string tag)
    {
        string? username = Player.Username;
        return !string.IsNullOrEmpty(username) && RemoveTag(username, tag);
    }

    public bool RemoveTag(string username, string tag)
    {
        Dictionary<string, AccountData>? accounts = GetAccountsDictionary();
        if (accounts == null)
            return false;

        if (!accounts.TryGetValue(username, out AccountData? accountData))
            return false;

        string? existingTag = accountData.Tags.FirstOrDefault(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (existingTag == null)
            return false;

        accountData.Tags.Remove(existingTag);
        _settingsService.Set("ManagedAccounts", accounts);
        return true;
    }

    public void AddTags(params string[] tags)
    {
        string? username = Player.Username;
        if (string.IsNullOrEmpty(username))
            return;

        AddTags(username, tags);
    }

    public bool AddTags(string username, params string[] tags)
    {
        Dictionary<string, AccountData>? accounts = GetAccountsDictionary();
        if (accounts == null)
            return false;

        if (!accounts.TryGetValue(username, out AccountData? accountData))
            return false;

        bool anyAdded = false;
        foreach (string tag in tags)
        {
            if (!accountData.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                accountData.Tags.Add(tag);
                anyAdded = true;
            }
        }

        if (anyAdded)
            _settingsService.Set("ManagedAccounts", accounts);

        return anyAdded;
    }

    public void RemoveTags(params string[] tags)
    {
        string? username = Player.Username;
        if (string.IsNullOrEmpty(username))
            return;

        RemoveTags(username, tags);
    }

    public bool RemoveTags(string username, params string[] tags)
    {
        Dictionary<string, AccountData>? accounts = GetAccountsDictionary();
        if (accounts == null)
            return false;

        if (!accounts.TryGetValue(username, out AccountData? accountData))
            return false;

        bool anyRemoved = false;
        foreach (string tag in tags)
        {
            string? existingTag = accountData.Tags.FirstOrDefault(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (existingTag != null)
            {
                accountData.Tags.Remove(existingTag);
                anyRemoved = true;
            }
        }

        if (anyRemoved)
            _settingsService.Set("ManagedAccounts", accounts);

        return anyRemoved;
    }

    public void SetTags(params string[] tags)
    {
        string? username = Player.Username;
        if (string.IsNullOrEmpty(username))
            return;

        SetTags(username, tags);
    }

    public bool SetTags(string username, params string[] tags)
    {
        Dictionary<string, AccountData>? accounts = GetAccountsDictionary();
        if (accounts == null)
            return false;

        if (!accounts.TryGetValue(username, out AccountData? accountData))
            return false;

        accountData.Tags = tags.ToList();
        _settingsService.Set("ManagedAccounts", accounts);
        return true;
    }

    public void ClearTags()
    {
        string? username = Player.Username;
        if (string.IsNullOrEmpty(username))
            return;

        ClearTags(username);
    }

    public bool ClearTags(string username)
    {
        Dictionary<string, AccountData>? accounts = GetAccountsDictionary();
        if (accounts == null)
            return false;

        if (!accounts.TryGetValue(username, out AccountData? accountData))
            return false;

        accountData.Tags.Clear();
        _settingsService.Set("ManagedAccounts", accounts);
        return true;
    }

    public List<ManagedAccount> GetAllAccounts()
    {
        Dictionary<string, AccountData>? accounts = GetAccountsDictionary();
        return accounts == null
            ? new List<ManagedAccount>()
            : accounts.Select(kvp => new ManagedAccount(
            kvp.Key,
            string.Empty, // Scripts are third-party code; they get the account list, never the passwords.
            kvp.Value.DisplayName,
            kvp.Value.Tags.ToList()
        )).ToList();
    }

    private Dictionary<string, AccountData>? GetAccountsDictionary()
    {
        Dictionary<string, AccountData>? accounts = _settingsService.Get<Dictionary<string, AccountData>>("ManagedAccounts");
        if (accounts == null)
            return null;

        if (accounts.Comparer == StringComparer.OrdinalIgnoreCase)
            return accounts;

        Dictionary<string, AccountData> caseInsensitiveDict = new(accounts, StringComparer.OrdinalIgnoreCase);
        return caseInsensitiveDict;
    }
}
