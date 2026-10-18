using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowLayoutManager.Model;
using static WindowLayoutManager.Native.NativeMethods;

namespace WindowLayoutManager.Native;

/// <summary>
/// Enumerates eligible top-level program windows and provides identity liveness checks.
/// All WindowIdentity.ProcessStartTicks are Win32 FILETIME values (100-ns since 1601-01-01 UTC)
/// so the fake and production paths share a consistent epoch.
/// </summary>
public sealed class WindowEnumerator
{
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Button",
        "DV2ControlHost",
        "MsgrIMEWindowClass",
        "SystemTray_Main",
        "NotifyIconOverflowWindow",
        "TaskListThumbnailWnd",
        "Windows.UI.Core.CoreWindow",
    };

    private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MonitorLayoutKeeper"
    };

    public EnumerationResult Enumerate(int currentProcessId)
    {
        var windows = new List<WindowInfo>();
        var excluded = new List<ExcludedWindow>();
        var shellHwnd = GetShellWindow();

        EnumWindows((hwnd, _) =>
        {
            try
            {
                // Own-process windows are rejected FIRST, before any title read. GetWindowText on a
                // window owned by the current process SENDS WM_GETTEXT synchronously to the owning
                // thread; if that thread (e.g. Microsoft.Win32.SystemEvents' hidden window thread) is
                // itself waiting on our UI thread, the UI thread deadlocks ("응답 없음"). The excluded
                // record keeps the reason and class (GetClassName sends no message) with a blank title.
                GetWindowThreadProcessId(hwnd, out uint ownerPid);
                if (IsOwnProcess((int)ownerPid, currentProcessId))
                {
                    excluded.Add(new ExcludedWindow
                    {
                        Hwnd = hwnd,
                        Reason = "own_process",
                        ClassName = TryClass(hwnd),
                        Title = ""
                    });
                    return true;
                }

                var (included, info, reason) = Inspect(hwnd, shellHwnd, currentProcessId);
                if (included && info is not null)
                {
                    windows.Add(info);
                }
                else if (reason is not null)
                {
                    excluded.Add(new ExcludedWindow
                    {
                        Hwnd = hwnd,
                        Reason = reason,
                        ClassName = TryClass(hwnd),
                        // Foreign-process window: GetWindowText returns the stored caption without
                        // waiting for that process's message loop.
                        Title = TryTitle(hwnd)
                    });
                }
            }
            catch (Exception ex)
            {
                excluded.Add(new ExcludedWindow
                {
                    Hwnd = hwnd,
                    Reason = $"exception:{ex.GetType().Name}",
                    ClassName = "",
                    Title = ""
                });
            }
            return true;
        }, IntPtr.Zero);

        return new EnumerationResult
        {
            Windows = windows,
            Excluded = excluded,
            Monitors = EnumerateMonitors()
        };
    }

    /// <summary>
    /// Wide top-level inspection that returns FULL metadata (class, title, exe, placement) for every
    /// top-level window currently in existence, INCLUDING hidden/cloaked ones. Used to reserve profile
    /// slots at cold startup for identifiable hidden windows (no movement). Shell/excluded-system
    /// classes are still filtered. Entries with unreadable identity are skipped.
    /// </summary>
    public IReadOnlyList<WindowInfo> EnumerateAnyTopLevelWithMetadata(int currentPid)
    {
        var list = new List<WindowInfo>();
        var shellHwnd = GetShellWindow();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (hwnd == shellHwnd) return true;
                var root = GetAncestor(hwnd, GA_ROOT);
                if (root != hwnd) return true;
                var rootOwner = GetAncestor(hwnd, GA_ROOTOWNER);
                if (rootOwner != hwnd) return true;

                var cls = TryClass(hwnd);
                if (ExcludedClasses.Contains(cls)) return true;

                GetWindowThreadProcessId(hwnd, out uint pidRaw);
                int pid = (int)pidRaw;
                // Own-process check (incl. Environment.ProcessId) BEFORE the title read below.
                if (pid == 0 || IsOwnProcess(pid, currentPid)) return true;

                var (exePath, procName, startTicks, idErr) = GetProcessIdentity(pid);
                if (idErr is not null) return true;
                if (ExcludedProcessNames.Contains(procName)) return true;

                var title = TryTitle(hwnd);

                var showState = WindowShowState.Normal;
                if (IsZoomed(hwnd)) showState = WindowShowState.Maximized;
                else if (IsIconic(hwnd)) showState = WindowShowState.Minimized;

                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                WindowRect normalRectScreen;
                WindowRect currentRect;
                if (GetWindowPlacement(hwnd, ref wp))
                {
                    var workspaceNormal = new WindowRect(
                        wp.rcNormalPosition.Left, wp.rcNormalPosition.Top,
                        wp.rcNormalPosition.Width, wp.rcNormalPosition.Height);
                    normalRectScreen = WorkspaceToScreen(hwnd, workspaceNormal);
                }
                else
                {
                    normalRectScreen = new WindowRect(0, 0, 0, 0);
                }
                if (GetWindowRect(hwnd, out var r))
                {
                    currentRect = new WindowRect(r.Left, r.Top, r.Width, r.Height);
                    if (currentRect.IsOffscreenSentinel()) currentRect = normalRectScreen;
                }
                else
                {
                    currentRect = normalRectScreen;
                }

                list.Add(new WindowInfo
                {
                    Identity = new WindowIdentity(hwnd, pid, startTicks),
                    ExePath = exePath,
                    ProcessName = procName,
                    ClassName = cls,
                    Title = title,
                    CurrentRect = currentRect,
                    NormalRect = normalRectScreen,
                    ShowState = showState,
                    MonitorDevice = TryMonitorDevice(hwnd)
                });
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Wide top-level identity probe: returns the identity of every top-level window currently in
    /// existence, regardless of visibility/cloaked/title/eligibility. Used by the monitoring service
    /// to prime "seen" at startup/resume so a hidden-at-startup window shown later is not treated as new.
    /// Only identity (hwnd + pid + start FILETIME) is returned; filtering happens later for eligibility.
    /// </summary>
    public IReadOnlyCollection<WindowIdentity> EnumerateAnyTopLevelIdentities(int currentPid)
    {
        var set = new HashSet<WindowIdentity>();
        EnumWindows((hwnd, lparam) =>
        {
            try
            {
                var root = GetAncestor(hwnd, GA_ROOT);
                if (root != hwnd) return true;
                var rootOwner = GetAncestor(hwnd, GA_ROOTOWNER);
                if (rootOwner != hwnd) return true;
                GetWindowThreadProcessId(hwnd, out uint pidRaw);
                int pid = (int)pidRaw;
                if (pid == 0 || pid == currentPid) return true;
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pidRaw);
                if (h == IntPtr.Zero) return true;
                try
                {
                    if (!GetProcessTimes(h, out long created, out _, out _, out _)) return true;
                    set.Add(new WindowIdentity(hwnd, pid, created));
                }
                finally { CloseHandle(h); }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return set;
    }

    public static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        foreach (var scr in System.Windows.Forms.Screen.AllScreens)
        {
            var b = scr.Bounds;
            var w = scr.WorkingArea;
            list.Add(new MonitorInfo(
                scr.DeviceName,
                new WindowRect(b.X, b.Y, b.Width, b.Height),
                new WindowRect(w.X, w.Y, w.Width, w.Height),
                scr.Primary));
        }
        return list;
    }

    /// <summary>
    /// Lifetime probe: returns true only when the window still exists AND its process is still
    /// alive AND the process start FILETIME matches the recorded identity. Visibility/cloaked state
    /// does NOT affect liveness. Fails CLOSED (returns false) on access failures.
    /// </summary>
    public static bool IsAliveNative(WindowIdentity id)
    {
        if (id.IsEmpty) return false;
        if (!IsWindow(id.Hwnd)) return false;
        GetWindowThreadProcessId(id.Hwnd, out uint pidRaw);
        if ((int)pidRaw != id.Pid) return false;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pidRaw);
        if (handle == IntPtr.Zero) return false;
        try
        {
            if (!GetProcessTimes(handle, out long created, out _, out _, out _)) return false;
            return created == id.ProcessStartTicks;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Returns identity details for a hwnd, consistent with the enumeration epoch.
    /// Returns null if the identity cannot be read.
    /// </summary>
    public static WindowIdentity? ReadIdentity(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out uint pidRaw);
        int pid = (int)pidRaw;
        if (pid == 0) return null;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pidRaw);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessTimes(handle, out long created, out _, out _, out _)) return null;
            return new WindowIdentity(hwnd, pid, created);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string? ReadExeKey(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(32768);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(handle, 0, sb, ref size) == 0) return null;
            return sb.ToString(0, size).ToLowerInvariant();
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string ReadClassName(IntPtr hwnd) => TryClass(hwnd);

    /// <summary>
    /// True for the current process, whatever currentPid a caller passes. Windows owned by this
    /// process must never have their title read (synchronous WM_GETTEXT, see Enumerate).
    /// </summary>
    public static bool IsOwnProcess(int pid, int currentPid) => pid == currentPid || pid == Environment.ProcessId;

    private (bool included, WindowInfo? info, string? reason) Inspect(IntPtr hwnd, IntPtr shellHwnd, int currentPid)
    {
        if (hwnd == shellHwnd) return (false, null, "shell_root");
        // Defense in depth: Enumerate already filters own-process windows before calling Inspect,
        // but this method must never reach TryTitle for one either.
        GetWindowThreadProcessId(hwnd, out uint earlyPid);
        if (IsOwnProcess((int)earlyPid, currentPid)) return (false, null, "own_process");
        if (!IsWindowVisible(hwnd)) return (false, null, "not_visible");
        var root = GetAncestor(hwnd, GA_ROOT);
        if (root != hwnd) return (false, null, "not_root");
        var rootOwner = GetAncestor(hwnd, GA_ROOTOWNER);
        if (rootOwner != hwnd) return (false, null, "owned_window");

        var ex = GetWindowLongPtrSafe(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0)
        {
            return (false, null, "tool_window");
        }

        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return (false, null, "cloaked");
        }

        var cls = TryClass(hwnd);
        if (ExcludedClasses.Contains(cls)) return (false, null, "excluded_class");

        var title = TryTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title)) return (false, null, "empty_title");

        GetWindowThreadProcessId(hwnd, out uint pidRaw);
        var pid = (int)pidRaw;
        if (pid == 0) return (false, null, "no_pid");
        if (IsOwnProcess(pid, currentPid)) return (false, null, "own_process");

        var (exePath, procName, startTicks, idErr) = GetProcessIdentity(pid);
        if (idErr is not null) return (false, null, idErr);

        if (ExcludedProcessNames.Contains(procName)) return (false, null, "excluded_process");

        var showState = WindowShowState.Normal;
        if (IsZoomed(hwnd)) showState = WindowShowState.Maximized;
        else if (IsIconic(hwnd)) showState = WindowShowState.Minimized;

        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return (false, null, "no_placement");

        var workspaceNormal = new WindowRect(
            wp.rcNormalPosition.Left,
            wp.rcNormalPosition.Top,
            wp.rcNormalPosition.Width,
            wp.rcNormalPosition.Height);

        // Convert WINDOWPLACEMENT workspace coordinates to screen coordinates by
        // adding the containing monitor's rcWork top-left minus rcMonitor top-left.
        // (https://devblogs.microsoft.com/oldnewthing/20040707-00/)
        var normalRectScreen = WorkspaceToScreen(hwnd, workspaceNormal);

        WindowRect currentRect;
        if (GetWindowRect(hwnd, out var r))
        {
            currentRect = new WindowRect(r.Left, r.Top, r.Width, r.Height);
            if (currentRect.IsOffscreenSentinel())
            {
                currentRect = normalRectScreen;
            }
        }
        else
        {
            currentRect = normalRectScreen;
        }

        var monName = TryMonitorDevice(hwnd);

        return (true, new WindowInfo
        {
            Identity = new WindowIdentity(hwnd, pid, startTicks),
            ExePath = exePath,
            ProcessName = procName,
            ClassName = cls,
            Title = title,
            CurrentRect = currentRect,
            NormalRect = normalRectScreen,
            ShowState = showState,
            MonitorDevice = monName
        }, null);
    }

    /// <summary>
    /// Convert WINDOWPLACEMENT.rcNormalPosition to screen coordinates.
    /// Per MSDN: For a window WITH WS_EX_TOOLWINDOW the field is already screen coordinates; for
    /// other windows it is workspace (rcWork) coordinates and must be offset by (rcWork - rcMonitor)
    /// of the containing monitor.
    /// Reference: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-windowplacement
    /// </summary>
    public static WindowRect WorkspaceToScreen(IntPtr hwnd, WindowRect workspace)
    {
        long exStyle = GetWindowLongPtrSafe(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return workspace;
        var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return workspace;
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMon, ref mi)) return workspace;
        int dx = mi.rcWork.Left - mi.rcMonitor.Left;
        int dy = mi.rcWork.Top - mi.rcMonitor.Top;
        return new WindowRect(workspace.X + dx, workspace.Y + dy, workspace.Width, workspace.Height);
    }

    /// <summary>
    /// Inverse: convert a screen rect to WINDOWPLACEMENT.rcNormalPosition for the monitor
    /// containing the target point. If the target hwnd has WS_EX_TOOLWINDOW the field is in screen
    /// coordinates and no conversion is applied.
    /// </summary>
    public static WindowRect ScreenToWorkspace(IntPtr hwnd, WindowRect screen)
    {
        if (hwnd != IntPtr.Zero)
        {
            long exStyle = GetWindowLongPtrSafe(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return screen;
        }
        var centerPt = new POINT { X = screen.X + screen.Width / 2, Y = screen.Y + screen.Height / 2 };
        var hMon = MonitorFromPoint(centerPt, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return screen;
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMon, ref mi)) return screen;
        int dx = mi.rcWork.Left - mi.rcMonitor.Left;
        int dy = mi.rcWork.Top - mi.rcMonitor.Top;
        return new WindowRect(screen.X - dx, screen.Y - dy, screen.Width, screen.Height);
    }

    private static string TryTitle(IntPtr hwnd)
    {
        // Last line of defense: never send a synchronous WM_GETTEXT(LENGTH) to one of our own
        // threads' windows, whatever the caller did.
        GetWindowThreadProcessId(hwnd, out uint pid);
        if ((int)pid == Environment.ProcessId) return "";
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 2);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string TryClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string TryMonitorDevice(IntPtr hwnd)
    {
        var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return "";
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMon, ref mi)) return "";
        return mi.szDevice;
    }

    private static (string exePath, string procName, long startTicks, string? error) GetProcessIdentity(int pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            // Fail CLOSED when native identity is unreadable. The fallback via Process.GetProcessById
            // uses .NET DateTime.Ticks which has a DIFFERENT epoch than Win32 FILETIME, so comparisons
            // would silently diverge. Report an explained skip instead.
            return ("", "", 0, "process_access_denied");
        }
        try
        {
            var sb = new StringBuilder(32768);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(handle, 0, sb, ref size) == 0)
            {
                return ("", "", 0, "process_name_failed");
            }
            var exePath = sb.ToString(0, size);
            var procName = Path.GetFileNameWithoutExtension(exePath);
            if (!GetProcessTimes(handle, out long created, out _, out _, out _))
            {
                return ("", "", 0, "process_time_failed");
            }
            return (exePath, procName, created, null);
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
