namespace Glossator.Core.Rules;

using System.Text.RegularExpressions;

public static class TurkishTermBoundaryMatcher
{
    /// <summary>
    /// Bir terim için Türkçe çekim eklerini, standart (') ve tipografik (’) kesme işaretlerini destekleyen esnek Regex üretir.
    /// </summary>
    public static Regex BuildMatcher(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return new Regex("$^"); // Eşleşmeyen regex
        }

        var escaped = Regex.Escape(term);

        // Sol Sınır: Harf veya rakam ile başlıyorsa önünde harf/rakam olmamalıdır
        var startsWithWord = char.IsLetterOrDigit(term[0]);
        var leftBoundary = startsWithWord ? @"(?<![\p{L}\p{N}_])" : string.Empty;

        // Sağ Sınır:
        // Harf/rakam ile bitiyorsa Türkçe çekim eklerini (kesmeli veya kesmesiz) kabul eder.
        // Sembol ile bitiyorsa (C#, C++ vb.) kesme işareti ile gelen çekim eklerini kabul eder.
        var endsWithWord = char.IsLetterOrDigit(term[^1]);
        var rightBoundary = endsWithWord
            ? @"(?:['’´′][\p{L}\p{N}]+|(?:ler|lar|e|a|i|ı|u|ü|de|da|te|ta|den|dan|ten|tan|in|ın|un|ün|nin|nın|nun|nün|le|la|yle|yla))?(?![\p{L}\p{N}_])"
            : @"(?:['’´′][\p{L}\p{N}]+)?(?![\p{L}\p{N}_])";

        return new Regex($@"{leftBoundary}{escaped}{rightBoundary}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsTermPresent(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var regex = BuildMatcher(term);
        return regex.IsMatch(text);
    }
}
