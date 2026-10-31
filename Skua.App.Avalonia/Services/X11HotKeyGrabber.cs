using Avalonia.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Skua.App.Avalonia.Services;

/// <summary>
/// Linux hotkeys. The game runs in the player's own X11 window inside Bonxie's, and while it has the keyboard
/// focus Avalonia never sees a key press. XGrabKey on Bonxie's window makes the X server send the hotkeys to us
/// whenever the focus is in that window or in any window inside it (the game included), and to nobody else;
/// other programs keep their keys.
/// </summary>
internal sealed class X11HotKeyGrabber : IDisposable
{
    public readonly record struct Gesture(Key Key, KeyModifiers Modifiers);

    private readonly Action<Gesture> _onPressed;
    private readonly object _lock = new();
    private IntPtr _window;
    private Gesture[] _gestures = [];
    private bool _changed;
    private volatile bool _disposed;
    private Thread? _thread;

    public X11HotKeyGrabber(Action<Gesture> onPressed)
    {
        _onPressed = onPressed;
    }

    /// <summary>Grabs <paramref name="gestures"/> on the X11 window <paramref name="window"/>, replacing the previous grabs.</summary>
    public void Update(IntPtr window, IEnumerable<Gesture> gestures)
    {
        lock (_lock)
        {
            _window = window;
            _gestures = gestures.ToArray();
            _changed = true;

            if (_thread is null && !_disposed)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "X11 hotkeys" };
                _thread.Start();
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    // Every X11 call happens on this thread, on a connection of its own (Avalonia's isn't shared). When the display
    // can't be opened or the connection fails, it tries again after a pause that grows up to half a minute.
    private void Run()
    {
        try
        {
            InstallErrorHandler();
            int retryDelay = 1000;
            while (!_disposed)
            {
                RunConnection();
                if (_disposed)
                    break;

                Thread.Sleep(retryDelay);
                retryDelay = Math.Min(retryDelay * 2, 30_000);
            }
        }
        catch (Exception ex)
        {
            // No libX11.
            System.Diagnostics.Trace.WriteLine($"X11 hotkeys unavailable: {ex.Message}");
        }
        finally
        {
            // The next Update starts a new worker.
            lock (_lock)
                _thread = null;
        }
    }

    private void RunConnection()
    {
        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            System.Diagnostics.Trace.WriteLine("X11 hotkeys unavailable: can't open the display.");
            return;
        }

        lock (s_ownDisplays)
            s_ownDisplays.Add(display);

        // A new connection has no grabs yet.
        lock (_lock)
            _changed = true;

        List<(int Keycode, uint Modifiers, Gesture Gesture)> grabs = [];
        HashSet<int> heldKeycodes = [];
        IntPtr grabWindow = IntPtr.Zero;
        uint altMask = Mod1Mask;
        uint[] lockCombinations = [];
        IntPtr xevent = Marshal.AllocHGlobal(XEventSize);
        try
        {
            // A held key repeats as KeyPress events alone, without the KeyRelease X11 normally sends before each
            // one, so the repeats can be told apart from a new press.
            XkbSetDetectableAutoRepeat(display, true, out bool detectableAutoRepeat);
            if (!detectableAutoRepeat)
                System.Diagnostics.Trace.WriteLine("X11 hotkeys: no detectable autorepeat, a held hotkey will repeat.");

            while (!_disposed)
            {
                bool changed;
                IntPtr window;
                Gesture[] gestures;
                lock (_lock)
                {
                    changed = _changed;
                    _changed = false;
                    window = _window;
                    gestures = _gestures;
                }

                if (changed)
                {
                    Ungrab(display, grabWindow, grabs, lockCombinations);
                    heldKeycodes.Clear();

                    // Which of Mod1-Mod5 are Alt and Num Lock is up to the X server's modifier map.
                    altMask = FindModifierMask(display, XK_Alt_L, XK_Alt_R) ?? FindModifierMask(display, XK_Meta_L, XK_Meta_R) ?? Mod1Mask;
                    uint numLockMask = FindModifierMask(display, XK_Num_Lock) ?? Mod2Mask;
                    lockCombinations = [0, LockMask, numLockMask, LockMask | numLockMask];

                    grabWindow = window;
                    if (window != IntPtr.Zero)
                    {
                        foreach (Gesture gesture in gestures)
                        {
                            nuint keysym = ToKeysym(gesture.Key);
                            if (keysym == 0)
                                continue;

                            int keycode = XKeysymToKeycode(display, keysym);
                            if (keycode == 0)
                                continue;

                            uint modifiers = ToX11Modifiers(gesture.Modifiers, altMask);
                            // Caps Lock and Num Lock change the state too; grab every combination so they don't matter.
                            s_lastErrorCode = 0;
                            foreach (uint locks in lockCombinations)
                                XGrabKey(display, keycode, modifiers | locks, window, true, GrabModeAsync, GrabModeAsync);
                            XSync(display, false);
                            if (s_lastErrorCode == BadAccess)
                            {
                                // Another program (often the desktop, e.g. Cinnamon's F2) already holds this key.
                                foreach (uint locks in lockCombinations)
                                    XUngrabKey(display, keycode, modifiers | locks, window);
                                System.Diagnostics.Trace.WriteLine($"Hotkey {gesture.Modifiers}+{gesture.Key} isn't available while the game is focused: another program already uses that key.");
                                continue;
                            }
                            grabs.Add((keycode, modifiers, gesture));
                        }
                    }
                    XFlush(display);
                }

                while (XPending(display) > 0)
                {
                    XNextEvent(display, xevent);
                    int type = Marshal.ReadInt32(xevent);
                    if (type == MappingNotify)
                    {
                        // The keymap changed (another layout, a keyboard plugged in): keycodes may differ now.
                        XRefreshKeyboardMapping(xevent);
                        lock (_lock)
                            _changed = true;
                        continue;
                    }

                    if (type == KeyRelease)
                    {
                        heldKeycodes.Remove(Marshal.ReadInt32(xevent, XKeyEventKeycodeOffset));
                        continue;
                    }

                    if (type != KeyPress)
                        continue;

                    int keycode = Marshal.ReadInt32(xevent, XKeyEventKeycodeOffset);
                    // Still held since its last press: an autorepeat, which would flip a toggle straight back.
                    if (!heldKeycodes.Add(keycode))
                        continue;

                    uint state = (uint)Marshal.ReadInt32(xevent, XKeyEventStateOffset) & (ShiftMask | ControlMask | altMask);
                    foreach ((int Keycode, uint Modifiers, Gesture Gesture) grab in grabs)
                    {
                        if (grab.Keycode == keycode && grab.Modifiers == state)
                        {
                            _onPressed(grab.Gesture);
                            break;
                        }
                    }
                }

                Thread.Sleep(20);
            }

            Ungrab(display, grabWindow, grabs, lockCombinations);
            XFlush(display);
        }
        catch (Exception ex)
        {
            // An exception on this thread would end the process; lose the hotkeys until Run reconnects instead.
            System.Diagnostics.Trace.WriteLine($"X11 hotkeys stopped: {ex}");
        }
        finally
        {
            Marshal.FreeHGlobal(xevent);
            // Close first: closing syncs, and errors it reports (e.g. BadWindow if Bonxie's window is already
            // gone) must still be recognized as ours.
            XCloseDisplay(display);
            lock (s_ownDisplays)
                s_ownDisplays.Remove(display);
        }
    }

    private static void Ungrab(IntPtr display, IntPtr window, List<(int Keycode, uint Modifiers, Gesture Gesture)> grabs, uint[] lockCombinations)
    {
        if (window != IntPtr.Zero)
        {
            foreach ((int Keycode, uint Modifiers, Gesture Gesture) grab in grabs)
            {
                foreach (uint locks in lockCombinations)
                    XUngrabKey(display, grab.Keycode, grab.Modifiers | locks, window);
            }
        }
        grabs.Clear();
    }

    // The mask of the first of Mod1-Mod5 a key with one of the keysyms is mapped to, or null if none is.
    private static uint? FindModifierMask(IntPtr display, params nuint[] keysyms)
    {
        byte[] keycodes = keysyms.Select(keysym => XKeysymToKeycode(display, keysym)).Where(keycode => keycode != 0).ToArray();
        if (keycodes.Length == 0)
            return null;

        IntPtr mapPointer = XGetModifierMapping(display);
        if (mapPointer == IntPtr.Zero)
            return null;

        try
        {
            // modifiermap is 8 rows (Shift, Lock, Control, Mod1-Mod5) of max_keypermod keycodes, 0 where unused.
            XModifierKeymap map = Marshal.PtrToStructure<XModifierKeymap>(mapPointer);
            for (int modifier = 3; modifier < 8; modifier++)
            {
                for (int i = 0; i < map.MaxKeyPerMod; i++)
                {
                    if (keycodes.Contains(Marshal.ReadByte(map.ModifierMap, modifier * map.MaxKeyPerMod + i)))
                        return 1u << modifier;
                }
            }
            return null;
        }
        finally
        {
            XFreeModifiermap(mapPointer);
        }
    }

    private static uint ToX11Modifiers(KeyModifiers modifiers, uint altMask)
    {
        uint result = 0;
        if (modifiers.HasFlag(KeyModifiers.Shift))
            result |= ShiftMask;
        if (modifiers.HasFlag(KeyModifiers.Control))
            result |= ControlMask;
        if (modifiers.HasFlag(KeyModifiers.Alt))
            result |= altMask;
        return result;
    }

    // X11 keysyms (X11/keysymdef.h) for the keys a hotkey can use.
    private static nuint ToKeysym(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
            return (nuint)('a' + (key - Key.A));
        if (key is >= Key.D0 and <= Key.D9)
            return (nuint)('0' + (key - Key.D0));
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return (nuint)(0xFFB0 + (key - Key.NumPad0));
        if (key is >= Key.F1 and <= Key.F24)
            return (nuint)(0xFFBE + (key - Key.F1));

        // Missing on purpose: Shift, Ctrl and Alt alone (the hotkey dialog takes them as modifiers), and the IME,
        // DBE and other OEM-specific keys, which have no keysym of their own.
        return key switch
        {
            Key.Enter => 0xFF0D,
            Key.Tab => 0xFF09,
            Key.Escape => 0xFF1B,
            Key.Space => 0x20,
            Key.Back => 0xFF08,
            Key.Delete => 0xFFFF,
            Key.Insert => 0xFF63,
            Key.Home => 0xFF50,
            Key.End => 0xFF57,
            Key.PageUp => 0xFF55,
            Key.PageDown => 0xFF56,
            Key.Left => 0xFF51,
            Key.Up => 0xFF52,
            Key.Right => 0xFF53,
            Key.Down => 0xFF54,
            Key.Cancel => 0xFF69,
            Key.LineFeed => 0xFF0A,
            Key.Clear => 0xFF0B,
            Key.Pause => 0xFF13,
            Key.CapsLock => 0xFFE5,
            Key.Select => 0xFF60,
            Key.Print => 0xFF61,
            Key.Snapshot => 0xFF61,
            Key.Execute => 0xFF62,
            Key.Help => 0xFF6A,
            Key.LWin => 0xFFEB,
            Key.RWin => 0xFFEC,
            Key.Apps => 0xFF67,
            Key.NumLock => 0xFF7F,
            Key.Scroll => 0xFF14,
            Key.Multiply => 0xFFAA,
            Key.Add => 0xFFAB,
            Key.Separator => 0xFFAC,
            Key.Subtract => 0xFFAD,
            Key.Decimal => 0xFFAE,
            Key.Divide => 0xFFAF,

            // X11/XF86keysym.h
            Key.Sleep => 0x1008FF2F,
            Key.VolumeDown => 0x1008FF11,
            Key.VolumeMute => 0x1008FF12,
            Key.VolumeUp => 0x1008FF13,
            Key.MediaPlayPause => 0x1008FF14,
            Key.MediaStop => 0x1008FF15,
            Key.MediaPreviousTrack => 0x1008FF16,
            Key.MediaNextTrack => 0x1008FF17,
            Key.BrowserHome => 0x1008FF18,
            Key.LaunchMail => 0x1008FF19,
            Key.BrowserSearch => 0x1008FF1B,
            Key.BrowserBack => 0x1008FF26,
            Key.BrowserForward => 0x1008FF27,
            Key.BrowserStop => 0x1008FF28,
            Key.BrowserRefresh => 0x1008FF29,
            Key.BrowserFavorites => 0x1008FF30,
            Key.SelectMedia => 0x1008FF32,

            Key.OemTilde => 0x60,
            Key.OemMinus => 0x2D,
            Key.OemPlus => 0x3D,
            Key.OemOpenBrackets => 0x5B,
            Key.OemCloseBrackets => 0x5D,
            Key.OemSemicolon => 0x3B,
            Key.OemQuotes => 0x27,
            Key.OemComma => 0x2C,
            Key.OemPeriod => 0x2E,
            Key.OemQuestion => 0x2F,
            Key.OemPipe => 0x5C,
            Key.OemBackslash => 0x3C,
            _ => 0
        };
    }

    // Xlib's default error handler ends the process, and a grab another program already holds fails with BadAccess.
    // Errors are consumed here so asynchronous X11 teardown errors cannot terminate the process.
    private static readonly HashSet<IntPtr> s_ownDisplays = [];
    private static XErrorHandler? s_errorHandler;

    private static void InstallErrorHandler()
    {
        lock (s_ownDisplays)
        {
            if (s_errorHandler is not null)
                return;

            s_errorHandler = OnXError;
            XSetErrorHandler(s_errorHandler);
        }
    }

    // The last error on our connection, read after XSync on the grabber thread (errors are delivered on that thread).
    [ThreadStatic]
    private static int s_lastErrorCode;

    private static int OnXError(IntPtr display, IntPtr errorEvent)
    {
        lock (s_ownDisplays)
        {
            if (s_ownDisplays.Contains(display))
            {
                s_lastErrorCode = Marshal.ReadByte(errorEvent, XErrorEventErrorCodeOffset);
                return 0;
            }
        }

        // XSetErrorHandler returns a function pointer owned by Avalonia. It may point to a
        // managed delegate with a different runtime delegate type, so converting it to our
        // XErrorHandler can throw InvalidCastException during X11 teardown. Xlib error callbacks
        // cannot safely be forwarded across those managed delegate types; consume the error.
        return 0;
    }

    private const int BadAccess = 10;
    // XErrorEvent (LP64): type, display, resourceid, serial, then error_code at byte 32.
    private const int XErrorEventErrorCodeOffset = 32;
    private const int KeyPress = 2;
    private const int KeyRelease = 3;
    private const int MappingNotify = 34;
    private const int GrabModeAsync = 1;
    private const uint ShiftMask = 1 << 0;
    private const uint LockMask = 1 << 1;
    private const uint ControlMask = 1 << 2;
    private const uint Mod1Mask = 1 << 3;
    private const uint Mod2Mask = 1 << 4;
    private const nuint XK_Alt_L = 0xFFE9;
    private const nuint XK_Alt_R = 0xFFEA;
    private const nuint XK_Meta_L = 0xFFE7;
    private const nuint XK_Meta_R = 0xFFE8;
    private const nuint XK_Num_Lock = 0xFF7F;

    // XEvent is a union of 24 longs; in XKeyEvent (LP64) state and keycode sit at bytes 80 and 84.
    private const int XEventSize = 24 * 8;
    private const int XKeyEventStateOffset = 80;
    private const int XKeyEventKeycodeOffset = 84;

    // XModifierKeymap: int max_keypermod; KeyCode *modifiermap (the pointer at byte 8 on LP64).
    [StructLayout(LayoutKind.Sequential)]
    private struct XModifierKeymap
    {
        public int MaxKeyPerMod;
        public IntPtr ModifierMap;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    private const string LibX11 = "libX11.so.6";

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern IntPtr XSetErrorHandler(XErrorHandler handler);

    [DllImport(LibX11)]
    private static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);

    [DllImport(LibX11)]
    private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, bool ownerEvents, int pointerMode, int keyboardMode);

    [DllImport(LibX11)]
    private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);

    [DllImport(LibX11)]
    private static extern int XPending(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XNextEvent(IntPtr display, IntPtr xevent);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XSync(IntPtr display, bool discard);

    [DllImport(LibX11)]
    private static extern int XRefreshKeyboardMapping(IntPtr mappingEvent);

    [DllImport(LibX11)]
    private static extern IntPtr XGetModifierMapping(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XFreeModifiermap(IntPtr modmap);

    // Bool is an int; bool marshals as a 4-byte BOOL.
    [DllImport(LibX11)]
    private static extern bool XkbSetDetectableAutoRepeat(IntPtr display, bool detectable, out bool supported);
}
