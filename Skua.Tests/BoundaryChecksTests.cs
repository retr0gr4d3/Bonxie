using Skua.Core.Scripts;
using Skua.Core.Utils;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Skua.Tests;

public class BoundaryChecksTests
{
    [Theory]
    [InlineData("town-Battleon-7Nov25.swf")]
    [InlineData("Yulgar_2.swf")]
    [InlineData("Map Name.SWF")]
    public void Ordinary_map_file_names_are_safe(string fileName)
    {
        Assert.True(ScriptMap.IsSafeMapFileName(fileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("town.swf\" & calc & \".swf")]
    [InlineData("town&calc.swf")]
    [InlineData("%COMSPEC%.swf")]
    [InlineData("a|b.swf")]
    [InlineData("..swf")]
    [InlineData("town.exe")]
    [InlineData("sub/town.swf")]
    public void Map_file_names_that_could_reach_the_shell_are_refused(string? fileName)
    {
        Assert.False(ScriptMap.IsSafeMapFileName(fileName));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/auqw/Scripts/commits", true)]
    [InlineData("https://raw.githubusercontent.com/auqw/Scripts/x", true)]
    [InlineData("https://github.com/x/y", true)]
    [InlineData("http://api.github.com/repos", false)]
    [InlineData("https://content.aq.com/game/api/data/servers", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://evilgithub.com/x", false)]
    public void Only_https_github_hosts_count_as_github(string address, bool expected)
    {
        Assert.Equal(expected, GitHubOnlyAuthorizationHandler.IsGitHub(new Uri(address)));
    }

    [Theory]
    [InlineData("https://api.github.com/rate_limit", true)]
    [InlineData("https://content.aq.com/game/api/data/servers", false)]
    public async Task Authorization_is_sent_to_github_only(string address, bool expectAuthorization)
    {
        RecordingHandler recorder = new();
        using HttpClient client = new(new GitHubOnlyAuthorizationHandler(recorder));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", "token");

        await client.GetAsync(address);

        Assert.Equal(expectAuthorization, recorder.SawAuthorization);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool SawAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SawAuthorization = request.Headers.Authorization is not null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
