using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coflnet.Payments.Models;

/// <summary>
/// JSON converter that handles deserialization of decimal values from JSON strings
/// </summary>
public class StringToDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                {
                    string stringValue = reader.GetString();
                    if (decimal.TryParse(stringValue, out decimal decimalValue))
                    {
                        return decimalValue;
                    }
                    throw new JsonException($"Cannot convert string '{stringValue}' to decimal");
                }
            case JsonTokenType.Number:
                return reader.GetDecimal();
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} when parsing decimal");
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// JSON converter that handles deserialization of nullable decimal values from JSON strings
/// </summary>
public class StringToNullableDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                {
                    string stringValue = reader.GetString();
                    if (string.IsNullOrWhiteSpace(stringValue))
                    {
                        return null;
                    }
                    if (decimal.TryParse(stringValue, out decimal decimalValue))
                    {
                        return decimalValue;
                    }
                    throw new JsonException($"Cannot convert string '{stringValue}' to decimal");
                }
            case JsonTokenType.Number:
                return reader.GetDecimal();
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} when parsing decimal");
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
