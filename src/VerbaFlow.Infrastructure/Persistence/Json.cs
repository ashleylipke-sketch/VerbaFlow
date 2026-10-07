using System.Text.Json;
using System.Text.Json.Serialization;

namespace VerbaFlow.Infrastructure.Persistence;

public static class Json
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web) { IncludeFields = false };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}
