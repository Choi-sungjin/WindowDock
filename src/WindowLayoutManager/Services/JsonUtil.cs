using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace WindowLayoutManager.Services;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static string Serialize<T>(T obj) => JsonSerializer.Serialize(obj, Options);
}
