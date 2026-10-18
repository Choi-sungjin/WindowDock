using System.Runtime.InteropServices;
using WindowLayoutManager.Model;
using static WindowLayoutManager.Native.NativeMethods;

namespace WindowLayoutManager.Native;

public enum MoveOutcome
{
    Success,
    IdentityChanged,
    WindowGone,
    NativeFailed,
    Unresponsive,
    Rejected
}

public sealed record MoveResult(MoveOutcome Outcome, WindowRect VerifiedRect, string? Error);

public sealed class WindowMover
{
    private const int PositionTolerance = 8;
    private const int SizeTolerance = 24;

    /// <summary>
    /// Move a window to a screen-coordinate target rect. Performs strict identity verification
    /// (HWND + PID + process start FILETIME + exe + class) BOTH before the responsiveness probe
    /// AND again immediately before each mutation, to minimize the window where a dying/reused handle
    /// could be mutated. Reports honest per-call outcomes (Rejected when size diverges from target).
    /// </summary>
    public MoveResult Move(WindowIdentity id, WindowRect target, bool maximize, string? expectedExeKey = null, string? expectedClassName = null)
    {
        if (id.IsEmpty) return new MoveResult(MoveOutcome.WindowGone, default, "empty_identity");

        var preCheck = VerifyIdentity(id, expectedExeKey, expectedClassName);
        if (preCheck is not null) return preCheck;

        // Responsiveness probe with short timeout. If the window is hung, bail.
        if (SendMessageTimeout(id.Hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out _) == IntPtr.Zero)
        {
            return new MoveResult(MoveOutcome.Unresponsive, default, "window_hung");
        }

        // Re-verify identity immediately after the probe (the window could have died / handle reused).
        var postProbe = VerifyIdentity(id, expectedExeKey, expectedClassName);
        if (postProbe is not null) return postProbe;

        if (maximize)
        {
            return DoMaximize(id, target);
        }

        // Normal (restored) target. If currently maximized/minimized, restore via SetWindowPlacement
        // (non-activating) rather than ShowWindow to avoid focus theft.
        bool neededRestore = false;
        if (IsZoomed(id.Hwnd) || IsIconic(id.Hwnd))
        {
            neededRestore = true;
            var wpRestore = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(id.Hwnd, ref wpRestore))
            {
                wpRestore.showCmd = SW_SHOWNOACTIVATE;
                wpRestore.flags |= WPF_ASYNCWINDOWPLACEMENT;
                if (!SetWindowPlacement(id.Hwnd, ref wpRestore))
                {
                    return new MoveResult(MoveOutcome.NativeFailed, default, $"restore_set_placement_failed err={Marshal.GetLastWin32Error()}");
                }
            }
            else
            {
                return new MoveResult(MoveOutcome.NativeFailed, default, "restore_get_placement_failed");
            }
        }

        // Final identity re-check before the actual mutation.
        var postRestore = VerifyIdentity(id, expectedExeKey, expectedClassName);
        if (postRestore is not null) return postRestore;

        // If we had to restore, verify the restored state is actually Normal (not stuck minimized/maximized).
        if (neededRestore)
        {
            bool restoredOk = false;
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(30);
                if (!IsWindow(id.Hwnd)) return new MoveResult(MoveOutcome.WindowGone, default, "destroyed_during_restore");
                if (!IsZoomed(id.Hwnd) && !IsIconic(id.Hwnd)) { restoredOk = true; break; }
            }
            if (!restoredOk)
            {
                return new MoveResult(MoveOutcome.Rejected, default, "restore_state_not_normal");
            }
            // After the restore-wait, re-verify identity immediately before the final mutation.
            // The restore step could have raced with window destruction / handle reuse.
            var postRestoreWait = VerifyIdentity(id, expectedExeKey, expectedClassName);
            if (postRestoreWait is not null) return postRestoreWait;
        }

        uint flags = SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS;
        if (!SetWindowPos(id.Hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, flags))
        {
            return new MoveResult(MoveOutcome.NativeFailed, default, $"SetWindowPos err={Marshal.GetLastWin32Error()}");
        }

        // Bounded verification. Normal success REQUIRES both the rect to be within tolerance AND
        // the final state to be normal (not minimized/maximized) — even when we did not have to
        // restore. Otherwise a window that happened to re-minimize/zoom itself during the move
        // would be reported Success.
        WindowRect lastGot = default;
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(40);
            if (!IsWindow(id.Hwnd)) return new MoveResult(MoveOutcome.WindowGone, default, "destroyed_during_move");
            if (!GetWindowRect(id.Hwnd, out var r)) continue;
            lastGot = new WindowRect(r.Left, r.Top, r.Width, r.Height);
            if (Within(lastGot, target, PositionTolerance, SizeTolerance))
            {
                if (IsIconic(id.Hwnd)) return new MoveResult(MoveOutcome.Rejected, lastGot, "unexpected_minimized_after_normal_move");
                if (IsZoomed(id.Hwnd)) return new MoveResult(MoveOutcome.Rejected, lastGot, "unexpected_maximized_after_normal_move");
                return new MoveResult(MoveOutcome.Success, lastGot, null);
            }
        }

        if (lastGot.Width <= 0 && lastGot.Height <= 0)
        {
            return new MoveResult(MoveOutcome.NativeFailed, default, "verify_failed");
        }
        bool posClose = Math.Abs(lastGot.X - target.X) <= PositionTolerance * 2 &&
                        Math.Abs(lastGot.Y - target.Y) <= PositionTolerance * 2;
        bool sizeClose = Math.Abs(lastGot.Width - target.Width) <= SizeTolerance * 2 &&
                         Math.Abs(lastGot.Height - target.Height) <= SizeTolerance * 2;
        var note = $"pos_close={posClose} size_close={sizeClose} got={lastGot.X},{lastGot.Y} {lastGot.Width}x{lastGot.Height}";
        return new MoveResult(MoveOutcome.Rejected, lastGot, note);
    }

    private static MoveResult DoMaximize(WindowIdentity id, WindowRect target)
    {
        // Store desired restore rect first as workspace coords for the target monitor; then show maximized asynchronously.
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(id.Hwnd, ref wp))
        {
            return new MoveResult(MoveOutcome.NativeFailed, default, "get_placement_failed");
        }
        // Pass the target hwnd so the conversion is aware of WS_EX_TOOLWINDOW (per MSDN,
        // WINDOWPLACEMENT.rcNormalPosition is in screen coords for tool windows, workspace coords
        // otherwise).
        var workspaceTarget = WindowEnumerator.ScreenToWorkspace(id.Hwnd, target);
        wp.rcNormalPosition = new RECT
        {
            Left = workspaceTarget.X,
            Top = workspaceTarget.Y,
            Right = workspaceTarget.X + workspaceTarget.Width,
            Bottom = workspaceTarget.Y + workspaceTarget.Height
        };
        wp.showCmd = SW_SHOWMAXIMIZED;
        wp.flags |= WPF_ASYNCWINDOWPLACEMENT; // Posts to the target's input queue; non-blocking.
        if (!SetWindowPlacement(id.Hwnd, ref wp))
        {
            return new MoveResult(MoveOutcome.NativeFailed, default, $"set_placement_failed err={Marshal.GetLastWin32Error()}");
        }

        // Determine the target monitor identifier up front so we can verify the maximized window
        // actually landed on the intended monitor.
        var centerPt = new POINT { X = target.X + target.Width / 2, Y = target.Y + target.Height / 2 };
        IntPtr targetMon = MonitorFromPoint(centerPt, MONITOR_DEFAULTTONEAREST);

        // Verify maximized state settled AND the stored restore rect matches target (within tolerance).
        for (int i = 0; i < 12; i++)
        {
            Thread.Sleep(50);
            if (!IsWindow(id.Hwnd)) return new MoveResult(MoveOutcome.WindowGone, default, "destroyed_during_maximize");
            if (!IsZoomed(id.Hwnd)) continue;
            if (!GetWindowRect(id.Hwnd, out var mr)) continue;

            // Confirm the restored placement persisted — readback failure is NOT silent success.
            var wpCheck = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(id.Hwnd, ref wpCheck))
            {
                return new MoveResult(MoveOutcome.NativeFailed, new WindowRect(mr.Left, mr.Top, mr.Width, mr.Height),
                    "placement_readback_failed_after_maximize");
            }
            var normStored = new WindowRect(
                wpCheck.rcNormalPosition.Left, wpCheck.rcNormalPosition.Top,
                wpCheck.rcNormalPosition.Right - wpCheck.rcNormalPosition.Left,
                wpCheck.rcNormalPosition.Bottom - wpCheck.rcNormalPosition.Top);
            bool xyOk = Math.Abs(normStored.X - workspaceTarget.X) <= PositionTolerance &&
                        Math.Abs(normStored.Y - workspaceTarget.Y) <= PositionTolerance;
            bool whOk = Math.Abs(normStored.Width - workspaceTarget.Width) <= SizeTolerance &&
                        Math.Abs(normStored.Height - workspaceTarget.Height) <= SizeTolerance;
            if (!xyOk || !whOk)
            {
                return new MoveResult(MoveOutcome.Rejected, new WindowRect(mr.Left, mr.Top, mr.Width, mr.Height),
                    $"normal_rect_rewritten stored={normStored.X},{normStored.Y} {normStored.Width}x{normStored.Height} wanted={workspaceTarget.X},{workspaceTarget.Y} {workspaceTarget.Width}x{workspaceTarget.Height}");
            }
            // Verify maximized state (we're in the loop only if IsZoomed became true) and target monitor.
            if (IsIconic(id.Hwnd))
            {
                return new MoveResult(MoveOutcome.Rejected, new WindowRect(mr.Left, mr.Top, mr.Width, mr.Height),
                    "unexpected_minimized_after_maximize");
            }
            var curMon = MonitorFromWindow(id.Hwnd, MONITOR_DEFAULTTONEAREST);
            if (targetMon != IntPtr.Zero && curMon != IntPtr.Zero && curMon != targetMon)
            {
                return new MoveResult(MoveOutcome.Rejected, new WindowRect(mr.Left, mr.Top, mr.Width, mr.Height),
                    $"maximized_on_wrong_monitor target={targetMon.ToInt64()} got={curMon.ToInt64()}");
            }
            return new MoveResult(MoveOutcome.Success, new WindowRect(mr.Left, mr.Top, mr.Width, mr.Height), null);
        }
        return new MoveResult(MoveOutcome.Rejected, default, "maximize_not_applied");
    }

    /// <summary>Returns null when identity matches recorded fields, otherwise a MoveResult describing the mismatch.</summary>
    private static MoveResult? VerifyIdentity(WindowIdentity id, string? expectedExeKey, string? expectedClassName)
    {
        if (!IsWindow(id.Hwnd)) return new MoveResult(MoveOutcome.WindowGone, default, "hwnd_dead");

        GetWindowThreadProcessId(id.Hwnd, out uint pidRaw);
        if ((int)pidRaw != id.Pid)
            return new MoveResult(MoveOutcome.IdentityChanged, default, "pid_mismatch");

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pidRaw);
        if (handle == IntPtr.Zero)
            return new MoveResult(MoveOutcome.IdentityChanged, default, "process_open_denied");
        try
        {
            if (!GetProcessTimes(handle, out long created, out _, out _, out _))
                return new MoveResult(MoveOutcome.IdentityChanged, default, "process_time_unreadable");
            if (created != id.ProcessStartTicks)
                return new MoveResult(MoveOutcome.IdentityChanged, default, $"start_time_mismatch want={id.ProcessStartTicks} got={created}");
            if (!string.IsNullOrEmpty(expectedExeKey))
            {
                var sb = new System.Text.StringBuilder(32768);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(handle, 0, sb, ref size) == 0)
                    return new MoveResult(MoveOutcome.IdentityChanged, default, "exe_unreadable");
                var exe = sb.ToString(0, size).ToLowerInvariant();
                if (!string.Equals(exe, expectedExeKey, StringComparison.OrdinalIgnoreCase))
                    return new MoveResult(MoveOutcome.IdentityChanged, default, "exe_mismatch");
            }
        }
        finally { CloseHandle(handle); }

        if (!string.IsNullOrEmpty(expectedClassName))
        {
            var cls = WindowEnumerator.ReadClassName(id.Hwnd);
            if (!string.Equals(cls, expectedClassName, StringComparison.Ordinal))
                return new MoveResult(MoveOutcome.IdentityChanged, default, $"class_mismatch want='{expectedClassName}' got='{cls}'");
        }
        return null;
    }

    private static bool Within(WindowRect got, WindowRect want, int posTol, int sizeTol)
    {
        return Math.Abs(got.X - want.X) <= posTol &&
               Math.Abs(got.Y - want.Y) <= posTol &&
               Math.Abs(got.Width - want.Width) <= sizeTol &&
               Math.Abs(got.Height - want.Height) <= sizeTol;
    }
}
