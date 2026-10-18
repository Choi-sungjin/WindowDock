using System.Runtime.InteropServices;
using WindowLayoutManager.Services;

namespace WindowLayoutManager.Native;

/// <summary>
/// Dedicated message-only window (parent HWND_MESSAGE) that owns our RegisterHotKey registrations
/// and receives WM_HOTKEY. It is independent of MainForm's handle, so the form's handle being
/// recreated (e.g. ShowInTaskbar toggling in tray-start mode) or the form staying hidden never
/// drops or duplicates a registration. Registrations use MOD_NOREPEAT (set by HotkeyRules) so
/// holding a key does not repeat an apply. Dispose releases every registration.
/// </summary>
public sealed class GlobalHotkeyWindow : NativeWindow, IHotkeyRegistrar, IDisposable
{
    private const int ERROR_INVALID_WINDOW_HANDLE = 1400;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HashSet<int> _registered = new();
    private bool _disposed;

    /// <summary>Raised on the creating (UI) thread with the registration id from WM_HOTKEY.</summary>
    public event Action<int>? HotkeyMessage;

    public GlobalHotkeyWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "WindowDock.Hotkeys",
            Parent = HWND_MESSAGE
        });
    }

    public IReadOnlyCollection<int> RegisteredIds => _registered.ToArray();

    public bool TryRegister(int id, uint modifiers, uint virtualKey, out int win32Error)
    {
        win32Error = 0;
        if (_disposed || Handle == IntPtr.Zero)
        {
            win32Error = ERROR_INVALID_WINDOW_HANDLE;
            return false;
        }
        // RegisterHotKey keeps an existing (hwnd,id) registration alongside a new one, so always
        // release our own previous registration for this id first.
        if (_registered.Contains(id)) Unregister(id);
        if (NativeMethods.RegisterHotKey(Handle, id, modifiers, virtualKey))
        {
            _registered.Add(id);
            return true;
        }
        win32Error = Marshal.GetLastWin32Error();
        return false;
    }

    public void Unregister(int id)
    {
        if (!_registered.Remove(id)) return;
        if (Handle != IntPtr.Zero) NativeMethods.UnregisterHotKey(Handle, id);
    }

    /// <summary>
    /// QA ONLY — SYNTHETIC dispatch: posts WM_HOTKEY to this window. This exercises our message
    /// handling and apply path but NOT the operating system's keyboard routing. Callers must label
    /// results as synthetic. The application ignores ids that are not currently registered.
    /// </summary>
    public bool PostSyntheticHotkey(int id) =>
        !_disposed && Handle != IntPtr.Zero &&
        NativeMethods.PostMessageW(Handle, (uint)NativeMethods.WM_HOTKEY, new IntPtr(id), IntPtr.Zero);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            int id = unchecked((int)m.WParam.ToInt64());
            try { HotkeyMessage?.Invoke(id); } catch { }
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != IntPtr.Zero)
        {
            foreach (var id in _registered.ToArray()) NativeMethods.UnregisterHotKey(Handle, id);
        }
        _registered.Clear();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
