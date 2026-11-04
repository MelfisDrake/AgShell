using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgShell.Protocol
{
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ControlRequest))]
    [JsonSerializable(typeof(ControlResponse))]
    [JsonSerializable(typeof(ControlEvent))]
    [JsonSerializable(typeof(ControlArgs))]
    [JsonSerializable(typeof(List<ControlPickItem>))]
    [JsonSerializable(typeof(ControlPresentationFrame))]
    [JsonSerializable(typeof(ControlPresentation))]
    [JsonSerializable(typeof(ControlPresentationStatus))]
    [JsonSerializable(typeof(ControlPresentationHud))]
    [JsonSerializable(typeof(ControlPresentationAsk))]
    [JsonSerializable(typeof(ControlPresentationOverlay))]
    [JsonSerializable(typeof(ControlPresentationPage))]
    [JsonSerializable(typeof(ControlPresentationPageCommand))]
    [JsonSerializable(typeof(ControlPresentationPageState))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(ControlPageServeFrame))]
    [JsonSerializable(typeof(ControlOverlayJobFrame))]
    internal sealed partial class ControlJsonContext : JsonSerializerContext
    {
    }

    public static class ControlJson
    {
        // Relaxed escaping keeps Cyrillic names and paths readable in --json output.
        private static readonly ControlJsonContext Context = new ControlJsonContext(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        public static string Serialize(ControlRequest request)
        {
            return JsonSerializer.Serialize(request, Context.ControlRequest);
        }

        public static string Serialize(ControlResponse response)
        {
            return JsonSerializer.Serialize(response, Context.ControlResponse);
        }

        public static string Serialize(ControlEvent controlEvent)
        {
            return JsonSerializer.Serialize(controlEvent, Context.ControlEvent);
        }

        public static string Serialize(ControlPickResult result)
        {
            return JsonSerializer.Serialize(result, Context.ControlPickResult);
        }

        public static string Serialize(ControlAskResult result)
        {
            return JsonSerializer.Serialize(result, Context.ControlAskResult);
        }

        public static string Serialize(ControlPresentationFrame frame)
        {
            return JsonSerializer.Serialize(frame, Context.ControlPresentationFrame);
        }

        public static string Serialize(ControlPageServeFrame frame)
        {
            return JsonSerializer.Serialize(frame, Context.ControlPageServeFrame);
        }

        public static string Serialize(ControlOverlayJobFrame frame)
        {
            return JsonSerializer.Serialize(frame, Context.ControlOverlayJobFrame);
        }

        /// <exception cref="JsonException">The line is not an overlay job frame.</exception>
        public static ControlOverlayJobFrame DeserializeJobFrame(string line)
        {
            return JsonSerializer.Deserialize(line, Context.ControlOverlayJobFrame) ?? throw new JsonException("empty overlay job frame");
        }

        /// <exception cref="JsonException">The line is not a page channel frame.</exception>
        public static ControlPageServeFrame DeserializePageServeFrame(string line)
        {
            return JsonSerializer.Deserialize(line, Context.ControlPageServeFrame) ?? throw new JsonException("empty page channel frame");
        }

        /// <exception cref="JsonException">The line is not a presentation frame.</exception>
        public static ControlPresentationFrame DeserializeFrame(string line)
        {
            return JsonSerializer.Deserialize(line, Context.ControlPresentationFrame) ?? throw new JsonException("empty frame");
        }

        /// <exception cref="JsonException">The text is not a JSON array of pick items.</exception>
        public static List<ControlPickItem>? DeserializePickItems(string json)
        {
            return JsonSerializer.Deserialize(json, Context.ListControlPickItem);
        }

        /// <exception cref="JsonException">The text is not a valid argument object.</exception>
        public static ControlArgs? DeserializeArgs(string json)
        {
            return JsonSerializer.Deserialize(json, Context.ControlArgs);
        }

        /// <exception cref="JsonException">The line is not a valid request.</exception>
        public static ControlRequest DeserializeRequest(string line)
        {
            return JsonSerializer.Deserialize(line, Context.ControlRequest) ?? throw new JsonException("empty request");
        }

        /// <exception cref="JsonException">The line is not a valid response.</exception>
        public static ControlResponse DeserializeResponse(string line)
        {
            return JsonSerializer.Deserialize(line, Context.ControlResponse) ?? throw new JsonException("empty response");
        }
    }
}
