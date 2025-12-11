using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Primitives;

namespace IdempotencyShield.Redis.Formatting;

/// <summary>
/// Custom JSON converter for <see cref="StringValues"/>.
/// Handles serialization as a string array to ensure compatibility across different platforms (Linux vs Windows)
/// and avoiding "System.NotSupportedException" in System.Text.Json for structs with IEnumerable.
/// </summary>
public class StringValuesJsonConverter : JsonConverter<StringValues>
{
    public override StringValues Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return StringValues.Empty;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            return new StringValues(s);
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    list.Add(reader.GetString() ?? string.Empty);
                }
            }
            return new StringValues(list.ToArray());
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} when parsing StringValues.");
    }

    public override void Write(Utf8JsonWriter writer, StringValues value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }
        writer.WriteEndArray();
    }
}
