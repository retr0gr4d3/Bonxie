using Skua.Core.Utils;
using System.Diagnostics;

namespace Skua.Manager.Avalonia.Services;

/// <summary>How to start one Bonxie client. Every launch in the manager goes through here.</summary>
internal sealed record ClientLaunch
{
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Server { get; init; }
    public string? GitHubToken { get; init; }
    public string? HostInstanceId { get; init; }

    /// <summary>Theme to apply in the client; null keeps the theme the client already has.</summary>
    public string? Theme { get; init; }

    public string? ScriptPath { get; init; }

    /// <summary>Start <see cref="ScriptPath"/> after login instead of only loading it.</summary>
    public bool RunScript { get; init; }

    public string? ScriptOptionsStorage { get; init; }
    public bool ExitOnScriptStop { get; init; }

    public Process? Start()
    {
        return Process.Start(ToStartInfo());
    }

    public ProcessStartInfo ToStartInfo()
    {
        ProcessStartInfo info = new(SkuaExecutableLocator.ExecutablePath)
        {
            WorkingDirectory = SkuaExecutableLocator.WorkingDirectory
        };

        void Option(string flag, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            info.ArgumentList.Add(flag);
            info.ArgumentList.Add(value);
        }

        Option("-u", Username);
        Option("-s", Server);
        Option("--host-instance", HostInstanceId);
        Option("--use-theme", Theme);
        Option(RunScript ? "--run-script" : "--load-script", ScriptPath);
        if (RunScript)
            Option("--script-options-storage", ScriptOptionsStorage);
        if (ExitOnScriptStop)
            info.ArgumentList.Add("--exit-on-script-stop");

        // Secrets travel in the environment, never on the command line.
        if (!string.IsNullOrEmpty(Password))
            info.Environment[StartupSecrets.PasswordVariable] = Password;
        if (!string.IsNullOrEmpty(GitHubToken))
            info.Environment[StartupSecrets.GitHubTokenVariable] = GitHubToken;

        return info;
    }
}
