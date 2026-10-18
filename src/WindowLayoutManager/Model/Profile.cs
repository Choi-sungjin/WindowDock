using System.Text.Json.Serialization;

namespace WindowLayoutManager.Model;

public sealed class Profile
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("updatedUtc")] public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("entries")] public List<ProfileEntry> Entries { get; set; } = new();
}

public sealed class ProfileEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("exePath")] public string ExePath { get; set; } = "";
    [JsonPropertyName("processName")] public string ProcessName { get; set; } = "";
    [JsonPropertyName("className")] public string ClassName { get; set; } = "";
    [JsonPropertyName("titleContains")] public string? TitleContains { get; set; }
    [JsonPropertyName("rect")] public SavedRect Rect { get; set; } = new();
    [JsonPropertyName("maximized")] public bool Maximized { get; set; }

    public string ExeKey => ExePath.ToLowerInvariant();
}

public sealed class SavedRect
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int Width { get; set; }
    [JsonPropertyName("h")] public int Height { get; set; }

    public WindowRect ToWindowRect() => new(X, Y, Width, Height);
    public static SavedRect From(WindowRect r) => new() { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
}

public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("activeProfileId")] public string? ActiveProfileId { get; set; }
    [JsonPropertyName("monitoringEnabled")] public bool MonitoringEnabled { get; set; } = false;
}
