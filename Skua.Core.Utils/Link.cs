using System.Diagnostics;

namespace Skua.Core.Utils;

/// <summary>Opens web pages in the user's browser.</summary>
public static class Link
{
    /// <summary>
    /// Opens <paramref name="link"/> in the default browser when it is an absolute http or https address. Anything
    /// else (a file path, a program, another URL scheme) is refused, because the text often comes from the game or
    /// from downloaded content and the shell would otherwise run it.
    /// </summary>
    /// <returns>False when the link was refused.</returns>
    public static bool OpenBrowser(string? link)
    {
        if (!TryGetWebAddress(link, out string address))
        {
            Trace.WriteLine($"Refused to open '{link}': not an http(s) address.");
            return false;
        }

        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
        return true;
    }

    /// <summary>Gives the escaped form of <paramref name="link"/> when it is an absolute http or https address.</summary>
    public static bool TryGetWebAddress(string? link, out string address)
    {
        address = string.Empty;
        if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        address = uri.AbsoluteUri;
        return true;
    }
}
