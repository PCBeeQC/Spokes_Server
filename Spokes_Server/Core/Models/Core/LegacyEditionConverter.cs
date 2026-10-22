namespace Spokes_Server.Core.Models.Core;

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

public class LegacyEditionConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            int value = reader.GetInt32();
            return value == 0 ? "Family" : "Business";
        }
        else if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? "Business";
        }

        return "Business";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
