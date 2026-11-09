using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glossator.AI.Models;

public sealed class ReviewerPatchResult
{
    [JsonPropertyName("isApproved")]
    public bool IsApproved { get; set; } = false;

    [JsonPropertyName("is_approved")]
    public bool? IsApprovedSnakeCase
    {
        get => IsApproved;
        set { if (value.HasValue) IsApproved = value.Value; }
    }

    [JsonPropertyName("feedback")]
    public string? Feedback { get; set; }

    [JsonPropertyName("patches")]
    public List<BlockPatch>? Patches { get; set; } = [];
}

public sealed class BlockPatch
{
    [JsonPropertyName("blockId")]
    [JsonConverter(typeof(NumberOrStringConverter))]
    public string BlockId { get; set; } = string.Empty;

    [JsonPropertyName("block_id")]
    [JsonConverter(typeof(NumberOrStringConverter))]
    public string? BlockIdSnakeCase
    {
        get => BlockId;
        set { if (value != null) BlockId = value; }
    }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("fixedText")]
    public string FixedText { get; set; } = string.Empty;

    [JsonPropertyName("fixed_text")]
    public string? FixedTextSnakeCase
    {
        get => FixedText;
        set { if (value != null) FixedText = value; }
    }

    [JsonPropertyName("revised_text")]
    public string? RevisedTextSnakeCase
    {
        get => FixedText;
        set { if (value != null) FixedText = value; }
    }
}

/// <summary>
/// Modelin sayısal ("blockId": 25) veya metinsel ("blockId": "025") ürettiği blok kimliklerini
/// güvenle karşılayan dayanıklı System.Text.Json dönüştürücüsü.
/// </summary>
public sealed class NumberOrStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt64(out var longVal))
            {
                return longVal.ToString();
            }
            return reader.GetDouble().ToString();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? string.Empty;
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.ToString();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
