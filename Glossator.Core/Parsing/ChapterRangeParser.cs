namespace Glossator.Core.Parsing;

/// <summary>
/// Bölüm numarası aralık ifadelerini ("1-5, 8, 10-12") 1 tabanlı indeks kümesine çevirir.
/// </summary>
public static class ChapterRangeParser
{
    public static HashSet<int> Parse(string? input, int maxChapterCount)
    {
        var result = new HashSet<int>();
        if (maxChapterCount <= 0 || string.IsNullOrWhiteSpace(input))
            return result;

        var tokens = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (token.Contains('-'))
            {
                var parts = token.Split('-', StringSplitOptions.TrimEntries);
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var start) &&
                    int.TryParse(parts[1], out var end))
                {
                    if (start > end) (start, end) = (end, start);
                    if (end < 1 || start > maxChapterCount) continue;

                    var clampedStart = Math.Max(1, start);
                    var clampedEnd = Math.Min(maxChapterCount, end);
                    for (int n = clampedStart; n <= clampedEnd; n++)
                    {
                        result.Add(n);
                    }
                }
            }
            else if (int.TryParse(token, out var single) && single >= 1 && single <= maxChapterCount)
            {
                result.Add(single);
            }
        }

        return result;
    }
}
