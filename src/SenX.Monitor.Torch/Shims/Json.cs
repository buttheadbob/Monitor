using Newtonsoft.Json;

// What the shared services take from System.Text.Json, over the Newtonsoft that Torch already loads. Only
// this plugin reads these files, so they don't have to match what Quasar's build writes.
namespace System.Text.Json;

internal sealed class JsonException(string message, Exception inner) : Exception(message, inner);

internal static class JsonSerializer
{
    public static string Serialize<T>(T value) => JsonConvert.SerializeObject(value);

    public static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonConvert.DeserializeObject<T>(json);
        }
        catch (Newtonsoft.Json.JsonException exception)
        {
            throw new JsonException(exception.Message, exception);
        }
    }
}
