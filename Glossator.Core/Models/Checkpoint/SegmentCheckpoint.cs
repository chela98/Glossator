using System.Security.Cryptography;
using System.Text;

namespace Glossator.Core.Models.Checkpoint;

public class SegmentCheckpoint
{
    public string BookId { get; set; } = "default";
    public string ChapterFile { get; set; } = string.Empty;
    public int SegmentIndex { get; set; }
    public string OriginalHash { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string OriginalText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public string StageStatus { get; set; } = "Completed";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Metnin SHA-256 özetini (hash) hesaplar. Kaynak metin değiştiğinde tespiti sağlar.
    /// </summary>
    public static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }
}
