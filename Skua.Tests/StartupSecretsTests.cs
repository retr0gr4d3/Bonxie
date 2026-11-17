using Skua.Core.Utils;
using Xunit;

namespace Skua.Tests;

public class StartupSecretsTests
{
    [Fact]
    public void Password_and_token_leave_the_arguments_for_the_environment()
    {
        Dictionary<string, string?> environment = new();

        string[] remaining = StartupSecrets.MoveToEnvironment(
            ["-u", "hero", "-p", "hunter2", "-s", "Twilly", "--gh-token", "ghp_x", "--exit-on-script-stop"],
            environment);

        Assert.Equal(["-u", "hero", "-s", "Twilly", "--exit-on-script-stop"], remaining);
        Assert.Equal("hunter2", environment[StartupSecrets.PasswordVariable]);
        Assert.Equal("ghp_x", environment[StartupSecrets.GitHubTokenVariable]);
    }

    [Fact]
    public void Long_password_option_is_moved_too()
    {
        Dictionary<string, string?> environment = new();

        string[] remaining = StartupSecrets.MoveToEnvironment(["--password", "hunter2"], environment);

        Assert.Empty(remaining);
        Assert.Equal("hunter2", environment[StartupSecrets.PasswordVariable]);
    }

    [Fact]
    public void Arguments_without_secrets_pass_through()
    {
        Dictionary<string, string?> environment = new();

        string[] remaining = StartupSecrets.MoveToEnvironment(["-u", "hero", "--run-script", @"C:\a b\s.cs"], environment);

        Assert.Equal(["-u", "hero", "--run-script", @"C:\a b\s.cs"], remaining);
        Assert.Empty(environment);
    }

    [Fact]
    public void Secret_option_without_a_value_is_dropped()
    {
        Dictionary<string, string?> environment = new();

        string[] remaining = StartupSecrets.MoveToEnvironment(["-u", "hero", "-p"], environment);

        Assert.Equal(["-u", "hero"], remaining);
        Assert.Empty(environment);
    }
}
