using System.Text.Json;

namespace WindowLayoutManager.Cli;

public static class CliCommon
{
    public static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
        return null;
    }

    public static bool HasFlag(string[] args, string name)
    {
        foreach (var a in args) if (a == name) return true;
        return false;
    }

    public static void WriteJson(string path, object obj)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(obj, Services.JsonUtil.Options);
        File.WriteAllText(path, json);
    }
}
