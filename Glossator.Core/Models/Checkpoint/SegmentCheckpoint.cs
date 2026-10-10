namespace Glossator.Core.Models.Checkpoint;

public class SegmentCheckpoint
{
    public string ChapterFile { get; set; } = string.Empty;
    public int SegmentIndex { get; set; }
    public string OriginalHash { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string OriginalText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public string StageStatus { get; set; } = "Completed";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
