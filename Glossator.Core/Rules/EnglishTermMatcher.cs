namespace Glossator.Core.Rules;

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

public static class EnglishTermMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> MatcherCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly HashSet<string> ExactCaseShortTerms = new(StringComparer.Ordinal)
    {
        "Go", "IT", "AI", "ML", "UI", "UX", "CI", "CD", "IP", "OS", "DB"
    };

    /// <summary>
    /// Bir terimin İngilizce kaynak metin içinde yalancı eşleşme (false-positive) üretmeden
    /// geçip geçmediğini denetleyen boundary-safe ve büyük/küçük harf duyarlı Regex üretir.
    /// </summary>
    public static Regex BuildMatcher(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return new Regex("(?!)", RegexOptions.None, DefaultTimeout);
        }

        return MatcherCache.GetOrAdd(term, static t =>
        {
            var trimmed = t.Trim();
            var escaped = Regex.Escape(trimmed);

            // Sol Sınır: Harf veya rakam ile başlıyorsa önünde harf, rakam, alt çizgi veya TİRE (-) olmamalıdır.
            // Nokta ile başlıyorsa (.NET vb.) önünde harf, rakam, alt çizgi, tire veya nokta olmamalıdır (örn. asp.NET eşleşmez).
            var startsWithWord = char.IsLetterOrDigit(trimmed[0]);
            var leftBoundary = startsWithWord
                ? @"(?<![\p{L}\p{N}_-])"
                : (trimmed[0] == '.' ? @"(?<![\p{L}\p{N}_.-])" : string.Empty);

            // Sağ Sınır: Harf veya rakam ile bitiyorsa arkasında harf, rakam, alt çizgi veya TİRE (-) olmamalıdır.
            // Sembol ile bitiyorsa (C#, C++ vb.) arkasında harf, rakam, alt çizgi veya sembol olmamalıdır (örn. C#abc eşleşmez).
            var endsWithWord = char.IsLetterOrDigit(trimmed[^1]);
            var rightBoundary = endsWithWord
                ? @"(?![\p{L}\p{N}_-])"
                : (trimmed[^1] == '#' || trimmed[^1] == '+' ? @"(?![\p{L}\p{N}_#+])" : string.Empty);

            // Kısaltma ve Akronim Kuralı:
            // 4 karakter veya daha kısa olan ya da tümüyle büyük harflerden oluşan teknik kısaltmalar
            // (REST, API, LLM, SQL, DAG, URL, GPU vb.) büyük/küçük harf duyarlı eşleşmelidir.
            var isAcronym = IsAcronymOrShortCode(trimmed);
            var options = isAcronym
                ? RegexOptions.CultureInvariant
                : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

            return new Regex($@"{leftBoundary}{escaped}{rightBoundary}", options, DefaultTimeout);
        });
    }

    /// <summary>
    /// Terimin kaynak metin içinde geçip geçmediğini güvenle doğrular.
    /// </summary>
    public static bool IsTermPresent(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var regex = BuildMatcher(term);
        return regex.IsMatch(text);
    }

    private static bool IsAcronymOrShortCode(string term)
    {
        if (ExactCaseShortTerms.Contains(term))
        {
            return true;
        }

        // Yalnızca harflerden oluşan ve tümü büyük harf olan kısa akronimler (REST, API, LLM, GPU, DAG, SQL)
        if (term.Length <= 5 && term.Length >= 2 && term.All(char.IsAsciiLetterUpper))
        {
            return true;
        }

        return false;
    }
}
