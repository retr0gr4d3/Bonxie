using System.IO;

namespace Skua.App.Avalonia.Services;

/// <summary>
/// Window messages between the tab host (<c>TabbedHostWindow</c>) and each tab's client process (<c>MainWindow</c>).
/// Both sides read the numbers from here; the hotkey messages (460 to 463) live in <c>HotKeys</c>.
/// </summary>
internal static class TabHostMessages
{
    private const int WM_USER = 0x0400;

    // Client to host
    public const int WM_SKUA_CHILD_READY = WM_USER + 440;
    public const int WM_SKUA_CHILD_DPICHANGED = WM_USER + 441;

    // Host to client
    public const int WM_SKUA_GRIDVIEW = WM_USER + 444;
    public const int WM_SKUA_START_SCRIPT = WM_USER + 445;
    public const int WM_SKUA_STOP_SCRIPT = WM_USER + 446;
    public const int WM_SKUA_LOGIN = WM_USER + 447;
    public const int WM_SKUA_LOGOUT = WM_USER + 448;
    public const int WM_SKUA_JUMP_MAP = WM_USER + 449;
    public const int WM_SKUA_SET_OPTION = WM_USER + 450;
    public const int WM_SKUA_THROTTLE = WM_USER + 452;
    public const int WM_SKUA_LOAD_SCRIPT = WM_USER + 453;
    public const int WM_SKUA_ARMY_SCHEDULER = WM_USER + 454;
    public const int WM_SKUA_ARMY_SCHEDULER_STOP = WM_USER + 455;
    public const int WM_SKUA_CHECK_LOGIN = WM_USER + 456;
    public const int WM_SKUA_JUMP_PLAYER = WM_USER + 457;
    public const int WM_SKUA_ACCEPT_QUEST = WM_USER + 458;
    public const int WM_SKUA_AUTO_ATTACK = WM_USER + 459;
    public const int WM_SKUA_JUMP_HOUSE = WM_USER + 464;
    public const int WM_SKUA_CHANGE_SERVER = WM_USER + 465;
}

/// <summary>
/// Files in the user's temp folder that carry what a window message can't: the host writes one, then posts the
/// matching message, and every tab reads it.
/// </summary>
internal static class TabHostFiles
{
    /// <summary>Script path for <see cref="TabHostMessages.WM_SKUA_LOAD_SCRIPT"/>.</summary>
    public static string Script { get; } = InTemp("skua_global_script.txt");

    /// <summary>Map, cell and pad lines for <see cref="TabHostMessages.WM_SKUA_JUMP_MAP"/>.</summary>
    public static string Jump { get; } = InTemp("skua_global_jump.txt");

    /// <summary>Player name for <see cref="TabHostMessages.WM_SKUA_JUMP_PLAYER"/>.</summary>
    public static string JumpPlayer { get; } = InTemp("skua_global_jump_player.txt");

    /// <summary>Server name for <see cref="TabHostMessages.WM_SKUA_CHANGE_SERVER"/>.</summary>
    public static string ChangeServer { get; } = InTemp("skua_global_change_server.txt");

    /// <summary>Quest id and reward id lines for <see cref="TabHostMessages.WM_SKUA_ACCEPT_QUEST"/>.</summary>
    public static string AcceptQuest { get; } = InTemp("skua_global_accept_quest.txt");

    /// <summary>Queue of scripts (JSON) for <see cref="TabHostMessages.WM_SKUA_ARMY_SCHEDULER"/>.</summary>
    public static string Playlist { get; } = InTemp("skua_global_playlist.json");

    /// <summary>Folder where each tab reports its account name and location to the host.</summary>
    public static string TabsDirectory { get; } = InTemp("BonxieTabs");

    /// <summary>File holding the account name logged in by the tab process <paramref name="processId"/>.</summary>
    public static string UsernameOf(int processId) => Path.Combine(TabsDirectory, $"{processId}.txt");

    /// <summary>File holding the map location (JSON) of the tab process <paramref name="processId"/>.</summary>
    public static string LocationOf(int processId) => Path.Combine(TabsDirectory, $"{processId}.location.json");

    private static string InTemp(string name) => Path.Combine(Path.GetTempPath(), name);
}
