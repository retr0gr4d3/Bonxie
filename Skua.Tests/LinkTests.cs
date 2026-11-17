using Skua.Core.Utils;
using Xunit;

namespace Skua.Tests;

public class LinkTests
{
    [Theory]
    [InlineData("https://aqwwiki.wikidot.com/Some Item", "https://aqwwiki.wikidot.com/Some%20Item")]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("  https://example.com/a?b=c  ", "https://example.com/a?b=c")]
    public void Web_addresses_are_accepted(string link, string expected)
    {
        Assert.True(Link.TryGetWebAddress(link, out string address));
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("calc.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"\attacker\share\run.bat")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("example.com")]
    public void Everything_else_is_refused(string? link)
    {
        Assert.False(Link.TryGetWebAddress(link, out _));
        Assert.False(Link.OpenBrowser(link));
    }
}
