using System.Text.Json;
namespace Aegis.Agent.Infrastructure;
public static class TelemetryJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, Options);
}