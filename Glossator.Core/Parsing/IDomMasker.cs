using AngleSharp.Dom;

namespace Glossator.Core.Parsing;

/// <summary>
/// AngleSharp DOM elemanlarını AST seviyesinde güvenli yer tutucularla maskeleyen
/// ve çevrilen metinleri orijinal teknik düğümlerle birleştiren maskeleyici sözleşmesi.
/// </summary>
public interface IDomMasker
{
    /// <summary>
    /// Verilen elemanın içindeki teknik düğümleri (code, kbd, samp, var, math, svg, index vb.)
    /// tespit eder, yerlerine ⟦PH_000001⟧ biçiminde belirteçler yerleştirir ve maskelenmiş metinle belirteç listesini döner.
    /// Orijinal eleman üzerinde derin kopya (clone) ile güvenli çalışır.
    /// </summary>
    (string MaskedInnerHtml, IReadOnlyList<MaskingToken> Tokens) MaskElement(IElement element);

    /// <summary>
    /// Çevirisi tamamlanmış metindeki belirteçleri doğrular ve orijinal teknik düğümleri
    /// deterministik olarak hedef elemanın InnerHtml alanına geri yükler.
    /// </summary>
    void UnmaskElement(IElement element, string translatedInnerHtml, IReadOnlyList<MaskingToken> tokens);
}
