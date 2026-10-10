using System.Text.Json.Serialization;

namespace Glossator.AI.Models;

public sealed class ReviewerPatchResult
{
    [JsonPropertyName("isApproved")]
    public bool IsApproved { get; set; } = true;

    [JsonPropertyName("feedback")]
    public string? Feedback { get; set; }

    [JsonPropertyName("patches")]
    public List<BlockPatch>? Patches { get; set; } = [];
}

public sealed class BlockPatch
{
    [JsonPropertyName("blockId")]
    public string BlockId { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("fixedText")]
    public string FixedText { get; set; } = string.Empty;
}
