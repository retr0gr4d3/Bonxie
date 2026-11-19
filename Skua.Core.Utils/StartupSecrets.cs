namespace Skua.Core.Utils;

/// <summary>
/// Carries the login password and GitHub token between Bonxie processes in environment variables instead of command
/// line arguments, which show up in process lists and system audit logs.
/// </summary>
public static class StartupSecrets
{
    /// <summary>Environment variable holding the account password for the client being started.</summary>
    public const string PasswordVariable = "BONXIE_LOGIN_PASSWORD";

    /// <summary>Environment variable holding the user's GitHub token.</summary>
    public const string GitHubTokenVariable = "BONXIE_GITHUB_TOKEN";

    // The first flag of each entry is the one handed to the option parser.
    private static readonly (string Variable, string[] Flags)[] _secrets =
    {
        (PasswordVariable, new[] { "-p", "--password" }),
        (GitHubTokenVariable, new[] { "--gh-token" }),
    };

    private static readonly Lazy<string[]> _commandLine = new(() => TakeFromEnvironment(Environment.GetCommandLineArgs()));

    /// <summary>
    /// This process's command line with the secrets it was started with added as ordinary options. The variables are
    /// cleared on first use, so processes started later do not inherit another account's password.
    /// </summary>
    public static string[] CommandLine => _commandLine.Value;

    /// <summary>
    /// Removes the secret options from <paramref name="args"/> and puts their values in <paramref name="environment"/>
    /// (the environment of a process about to be started). Returns the remaining arguments.
    /// </summary>
    public static string[] MoveToEnvironment(IEnumerable<string> args, IDictionary<string, string?> environment)
    {
        List<string> remaining = new();
        using IEnumerator<string> e = args.GetEnumerator();
        while (e.MoveNext())
        {
            string? variable = _secrets.FirstOrDefault(s => s.Flags.Contains(e.Current)).Variable;
            if (variable is null)
                remaining.Add(e.Current);
            else if (e.MoveNext())
                environment[variable] = e.Current;
        }
        return remaining.ToArray();
    }

    private static string[] TakeFromEnvironment(string[] args)
    {
        List<string> withSecrets = new(args);
        foreach ((string variable, string[] flags) in _secrets)
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(value))
                continue;

            Environment.SetEnvironmentVariable(variable, null);
            withSecrets.Add(flags[0]);
            withSecrets.Add(value);
        }
        return withSecrets.ToArray();
    }
}
