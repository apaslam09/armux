using System.Runtime.InteropServices;
using Windows.System;

namespace Armux.App;

/// <summary>
/// Shortcuts at the message queue (D-41): a message hook on the UI thread sees each key as the thread takes it from its
/// queue, whichever XAML element or child window has the keyboard, and even when no element does. XAML's key events only
/// reach an element's ancestors, so a shortcut handler there went deaf whenever focus was anywhere else. The hook is local
/// to this thread: it adds no process or DLL, and it sees no other program's keys. A browser page's keys belong to
/// WebView2's process and never pass through here; those come back through <see cref="BrowserHost.KeyScript"/>.
/// </summary>
static unsafe partial class KeyHook
{
    const int WH_GETMESSAGE = 3, HC_ACTION = 0, PM_REMOVE = 1, GA_ROOT = 2;
    const uint WM_NULL = 0, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;

    /// <summary>The start of Win32's MSG, which is all this reads.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Message
    {
        public nint Window;
        public uint Id;
        public nint Key;
    }

    static readonly Dictionary<nint, Func<VirtualKey, bool>> windows = [];
    static nint hook;

    /// <summary>
    /// Sends the keys typed in <paramref name="hwnd"/> to <paramref name="onKey"/>, which returns true when it used the
    /// key. False when the hook can't be installed.
    /// </summary>
    public static bool Add(nint hwnd, Func<VirtualKey, bool> onKey)
    {
        if (hook == 0) hook = SetWindowsHookExW(WH_GETMESSAGE, &Proc, 0, GetCurrentThreadId());
        if (hook == 0) return false;
        windows[hwnd] = onKey;
        return true;
    }

    public static void Remove(nint hwnd)
    {
        windows.Remove(hwnd);
        if (windows.Count > 0 || hook == 0) return;
        UnhookWindowsHookEx(hook);
        hook = 0;
    }

    /// <summary>The state of a modifier as of the key being handled.</summary>
    public static bool Down(VirtualKey key) => GetKeyState((int)key) < 0;

    [UnmanagedCallersOnly]
    static nint Proc(int code, nint removal, nint message)
    {
        var m = (Message*)message;
        // Every message of the thread comes through here, so anything but a key leaves at once. Other windows on this
        // thread (the folder picker) keep their keys. A used key becomes a message nothing acts on.
        if (code == HC_ACTION && removal == PM_REMOVE && m->Id is WM_KEYDOWN or WM_SYSKEYDOWN
            && windows.TryGetValue(GetAncestor(m->Window, GA_ROOT), out var onKey))
        {
            try { if (onKey((VirtualKey)(int)m->Key)) m->Id = WM_NULL; }
            catch (Exception e) { App.Log(nameof(KeyHook), e); }
        }
        return CallNextHookEx(0, code, removal, message);
    }

    [LibraryImport("user32.dll")] private static partial nint SetWindowsHookExW(int id, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint thread);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool UnhookWindowsHookEx(nint hook);
    [LibraryImport("user32.dll")] private static partial nint CallNextHookEx(nint hook, int code, nint removal, nint message);
    [LibraryImport("user32.dll")] private static partial nint GetAncestor(nint window, uint flags);
    [LibraryImport("user32.dll")] private static partial short GetKeyState(int key);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
}
