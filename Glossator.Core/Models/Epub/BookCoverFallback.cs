namespace Glossator.Core.Models.Epub;

/// <summary>
/// Gömülü kapağı bulunmayan EPUB kitapları için baş harf monogramı ve tutarlı arka plan rengi üretir.
/// </summary>
public readonly record struct BookCoverFallback(string Monogram, string BackgroundHex, string AccentHex)
{
    private static readonly (string Background, string Accent)[] Palette =
    [
        ("#1E3A8A", "#60A5FA"),
        ("#0F766E", "#2DD4BF"),
        ("#4338CA", "#818CF8"),
        ("#7C2D12", "#FB923C"),
        ("#334155", "#94A3B8"),
        ("#4C1D95", "#A78BFA"),
        ("#14532D", "#4ADE80"),
        ("#831843", "#F472B6")
    ];

    public static BookCoverFallback ForBook(string? title, string? author = null)
    {
        var source = string.IsNullOrWhiteSpace(title) ? author : title;
        var monogram = BuildMonogram(source);

        var hashInput = $"{title?.Trim()}|{author?.Trim()}".ToUpperInvariant();
        uint hash = 2166136261;
        foreach (var ch in hashInput)
        {
            hash ^= ch;
            hash *= 16777619;
        }

        var (bg, accent) = Palette[hash % (uint)Palette.Length];
        return new BookCoverFallback(monogram, bg, accent);
    }

    private static string BuildMonogram(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "EP";

        var letters = new List<char>(2);
        foreach (var word in text.Split([' ', '\t', '-', '_', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ch in word)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    letters.Add(char.ToUpperInvariant(ch));
                    break;
                }
            }

            if (letters.Count == 2)
                break;
        }

        if (letters.Count == 0)
            return "EP";

        if (letters.Count == 1)
        {
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    var upper = char.ToUpperInvariant(ch);
                    if (upper != letters[0])
                    {
                        letters.Add(upper);
                        break;
                    }
                }
            }
        }

        return new string(letters.ToArray());
    }
}
