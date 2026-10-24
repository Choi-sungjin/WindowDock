namespace WindowLayoutManager.App;

/// <summary>Display name and version shown in the window title, tray tooltip and messages.</summary>
public static class AppInfo
{
    public const string ProductName = "창자리 (WindowDock)";

    /// <summary>Major.Minor.Build of this assembly (from &lt;Version&gt; in the csproj).</summary>
    public static string DisplayVersion
    {
        get
        {
            var v = typeof(AppInfo).Assembly.GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static string ProductTitle => $"{ProductName} v{DisplayVersion}";
}

/// <summary>
/// The embedded WindowDock icon (Assets\WindowDock.ico, LogicalName "WindowDock.ico"). Loaded once
/// for the process lifetime and never disposed (Form.Icon does not own it). If loading fails the
/// caller gets the generic system icon AND a non-null <see cref="LoadError"/> that the UI reports.
/// </summary>
public static class AppIcon
{
    public const string ResourceName = "WindowDock.ico";

    private static readonly Lazy<(Icon? icon, byte[]? bytes, string? error)> Loaded = new(LoadCore);

    public static Icon? Icon => Loaded.Value.icon;
    public static string? LoadError => Loaded.Value.error;
    public static bool IsLoaded => Loaded.Value.icon is not null;

    /// <summary>Icon for Form.Icon (shared; caller must not dispose).</summary>
    public static Icon ForForm => Loaded.Value.icon ?? SystemIcons.Application;

    /// <summary>
    /// Small icon for the tray. Returns a NEW icon the caller owns (owned=true) when the embedded
    /// icon is available; otherwise the shared generic icon (owned=false, must not be disposed).
    /// </summary>
    public static Icon CreateTrayIcon(out bool owned)
    {
        var icon = Loaded.Value.icon;
        if (icon is null)
        {
            owned = false;
            return SystemIcons.Application;
        }
        try
        {
            owned = true;
            return new Icon(icon, SystemInformation.SmallIconSize);
        }
        catch
        {
            owned = false;
            return icon;
        }
    }

    /// <summary>Frame sizes declared in the embedded .ico directory (0 in the file means 256).</summary>
    public static IReadOnlyList<int> FrameSizes()
    {
        var bytes = Loaded.Value.bytes;
        var sizes = new List<int>();
        if (bytes is null || bytes.Length < 6) return sizes;
        int count = BitConverter.ToUInt16(bytes, 4);
        for (int i = 0; i < count; i++)
        {
            int at = 6 + i * 16;
            if (at + 16 > bytes.Length) break;
            int w = bytes[at] == 0 ? 256 : bytes[at];
            int h = bytes[at + 1] == 0 ? 256 : bytes[at + 1];
            if (w == h) sizes.Add(w);
        }
        return sizes;
    }

    private static (Icon? icon, byte[]? bytes, string? error) LoadCore()
    {
        try
        {
            using var s = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (s is null) return (null, null, "embedded_icon_missing");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            using var iconStream = new MemoryStream(bytes);
            var icon = new Icon(iconStream);
            return (icon, bytes, null);
        }
        catch (Exception ex)
        {
            return (null, null, ex.GetType().Name);
        }
    }
}
