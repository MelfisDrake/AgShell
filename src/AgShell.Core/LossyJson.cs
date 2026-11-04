using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's lossy decode of a snapshot's optional fields: a value of the wrong type or shape (a bad id, a string for a
    /// number, a hand edit) reads as unset instead of failing the whole file, so one field never costs every workspace.
    /// </summary>
    internal class LossyConverter<T> : JsonConverter<T>
    {
        /// <summary>A null for a plain bool reaches the converter and reads as false; for the rest it is the usual unset.</summary>
        public override bool HandleNull => typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null;

        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Utf8JsonReader start = reader;
            try
            {
                return JsonSerializer.Deserialize(ref reader, Info(options));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                reader = start;
                reader.Skip();
                return Fallback();
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, Info(options));
        }

        protected virtual T? Fallback()
        {
            return default;
        }

        private static JsonTypeInfo<T> Info(JsonSerializerOptions options)
        {
            return (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        }
    }

    /// <summary>A lossy list that is never null: a bad one reads as empty.</summary>
    internal sealed class LossyListConverter<T> : LossyConverter<List<T>>
    {
        protected override List<T>? Fallback()
        {
            return new List<T>();
        }
    }
}
