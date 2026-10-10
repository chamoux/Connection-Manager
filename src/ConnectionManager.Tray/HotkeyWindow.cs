using System.Runtime.InteropServices;
using ConnectionManager.Core.Modes;

namespace ConnectionManager.Tray;

/// A hidden window that receives one global hotkey and Explorer's "TaskbarCreated" broadcast.
/// It is a normal top-level window on purpose: message-only windows never get broadcasts.
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint MsgfltAllow = 1;
    private const int HotkeyId = 1;
    private static readonly int WmTaskbarCreated = (int)RegisterWindowMessage("TaskbarCreated");
    private bool _registered;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
        // Explorer runs at medium integrity, so Windows drops its broadcast to an elevated window unless allowed. The tray
        // runs unelevated, but "Run as administrator" would still start it elevated.
        if (WmTaskbarCreated != 0) ChangeWindowMessageFilterEx(Handle, (uint)WmTaskbarCreated, MsgfltAllow, IntPtr.Zero);
    }

    public event Action? Pressed;

    /// Explorer restarted and the taskbar is new: tray icons must be added again.
    public event Action? TaskbarCreated;

    /// False when the text is invalid or another app already owns that key combination.
    public bool Register(string text)
    {
        Unregister();
        if (!Hotkey.TryParse(text, out var hotkey) || hotkey is null) return false;
        _registered = RegisterHotKey(Handle, HotkeyId, (uint)hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey);
        return _registered;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam == HotkeyId) Pressed?.Invoke();
        else if (m.Msg == WmTaskbarCreated && WmTaskbarCreated != 0) TaskbarCreated?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    private void Unregister()
    {
        if (_registered) UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr changeFilterStruct);
}
