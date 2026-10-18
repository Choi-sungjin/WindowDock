namespace WindowLayoutManager.Model;

public sealed record WindowIdentity(IntPtr Hwnd, int Pid, long ProcessStartTicks)
{
    public static readonly WindowIdentity Empty = new(IntPtr.Zero, 0, 0);
    public bool IsEmpty => Hwnd == IntPtr.Zero;
}

public sealed class WindowInfo
{
    public required WindowIdentity Identity { get; init; }
    public required string ExePath { get; init; }
    public required string ProcessName { get; init; }
    public required string ClassName { get; init; }
    public required string Title { get; init; }
    public required WindowRect CurrentRect { get; init; }
    public required WindowRect NormalRect { get; init; }
    public required WindowShowState ShowState { get; init; }
    public required string MonitorDevice { get; init; }

    public string ExeKey => ExePath.ToLowerInvariant();
}

public sealed class ExcludedWindow
{
    public required IntPtr Hwnd { get; init; }
    public required string Reason { get; init; }
    public required string ClassName { get; init; }
    public required string Title { get; init; }
}

public sealed class EnumerationResult
{
    public required IReadOnlyList<WindowInfo> Windows { get; init; }
    public required IReadOnlyList<ExcludedWindow> Excluded { get; init; }
    public required IReadOnlyList<MonitorInfo> Monitors { get; init; }
}
