using AngleSharp.Dom;

namespace Glossator.Core.Parsing;

public class HtmlSegment
{
    public int Index { get; init; }
    public string TagName { get; init; } = string.Empty;
    public string OriginalInnerHtml { get; init; } = string.Empty;

    /// <summary>
    /// Kod, formül ve dizin çapaları ⟦PH_000001⟧ şeklinde maskelenmiş, çeviriye gidecek metin.
    /// </summary>
    public string MaskedText { get; init; } = string.Empty;

    /// <summary>
    /// DOM TreeWalker tarafından üretilen AST maskeleme belirteçleri.
    /// </summary>
    public IReadOnlyList<MaskingToken> Tokens { get; init; } = [];

    /// <summary>
    /// AngleSharp DOM ağacındaki orijinal element referansı (çeviri bitince doğrudan güncellenir).
    /// </summary>
    public IElement DomElement { get; init; } = null!;

    public string? TranslatedText { get; set; }

    /// <summary>
    /// Segmentin yalnızca yer tutuculardan, sayılardan veya noktalama işaretlerinden oluşup oluşmadığını (doğal dil içermediğini) kontrol eder.
    /// Saf teknik bloklarda TranslatedText == MaskedText olması beklenen bir durumdur.
    /// </summary>
    public bool IsPureTechnicalOrPlaceholder()
    {
        return DomTreeWalkerMasker.IsPureTechnicalOrPlaceholder(MaskedText);
    }

    /// <summary>
    /// Segmentin çevirisinin tamamlanmış sayılabilmesi için boş olmadığını ve ya hedef dilde farklılaştığını
    /// ya da doğal olarak aynı kalması beklenen teknik/iletişim/kurum/kitap meta verisi veya marka adı olduğunu doğrular.
    /// </summary>
    public bool IsTranslationComplete(string? bookTitle = null, string? bookAuthor = null)
    {
        if (string.IsNullOrWhiteSpace(TranslatedText))
        {
            return false;
        }

        if (IsPureTechnicalOrPlaceholder())
        {
            return true;
        }

        var maskedVisible = DomTreeWalkerMasker.ExtractVisibleText(MaskedText);
        var translatedVisible = DomTreeWalkerMasker.ExtractVisibleText(TranslatedText);

        if (!string.Equals(TranslatedText.Trim(), MaskedText.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(translatedVisible, maskedVisible, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return DomTreeWalkerMasker.MatchesBookMetadata(maskedVisible, bookTitle, bookAuthor) ||
               DomTreeWalkerMasker.IsBrandOrProperEntityPhrase(maskedVisible);
    }

    /// <summary>
    /// DOM ağacında özel düğüm değişimi gereken karmaşık yapılar (örn. alt listeler içeren li) için özel değiştirme aksiyonu.
    /// Null ise varsayılan olarak DomElement.InnerHtml güncellenir.
    /// </summary>
    public Action<string>? ReplacementAction { get; init; }

    /// <summary>
    /// Çevrilen metindeki belirteçleri orijinal HTML parçalarıyla deterministik olarak geri takar.
    /// </summary>
    public string ReconstructHtml()
    {
        if (string.IsNullOrWhiteSpace(TranslatedText))
        {
            return OriginalInnerHtml;
        }

        var text = DomTreeWalkerMasker.NormalizeTokens(TranslatedText);

        if (Tokens != null && Tokens.Count > 0)
        {
            return DomTreeWalkerMasker.RestorePlaceholders(text, Tokens);
        }

        return text;
    }
}
