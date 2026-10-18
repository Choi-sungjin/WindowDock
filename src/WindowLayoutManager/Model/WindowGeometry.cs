namespace WindowLayoutManager.Model;

public readonly record struct WindowRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool IsOffscreenSentinel()
    {
        // Minimized windows report (-32000,-32000) via GetWindowRect. Treat as invalid.
        return X <= -32000 || Y <= -32000;
    }

    public override string ToString() => $"({X},{Y},{Width}x{Height})";
}

public readonly record struct MonitorInfo(string DeviceName, WindowRect Bounds, WindowRect WorkArea, bool IsPrimary);

public enum WindowShowState
{
    Normal,
    Minimized,
    Maximized
}
