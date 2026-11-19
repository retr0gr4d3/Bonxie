using CommunityToolkit.Mvvm.ComponentModel;
using Skua.Core.Interfaces.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skua.Manager.Avalonia.ViewModels;

public partial class AccountEditorViewModel : ObservableObject
{
    public AccountEditorViewModel(IAccountItemViewModel account)
    {
        Account = account;
        _username = account.Username ?? string.Empty;
        _displayName = account.DisplayName ?? string.Empty;
        _password = Skua.Core.Utils.CryptoUtils.Decrypt(account.Password ?? string.Empty);
        _tags = account.Tags != null ? string.Join(", ", account.Tags) : string.Empty;
    }

    public IAccountItemViewModel Account { get; }

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _tags = string.Empty;

    [ObservableProperty]
    private bool _showPassword = false;

    public List<string> ParsedTags => (Tags ?? string.Empty)
        .Split(',')
        .Select(t => t.Trim())
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}