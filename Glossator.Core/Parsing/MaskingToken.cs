namespace Glossator.Core.Parsing;

/// <summary>
/// DOM TreeWalker tarafından maskelenen teknik düğüme ait değişmez (immutable) belirteç.
/// </summary>
/// <param name="Token">Sabit genişlikli yer tutucu metin (örn: ⟦PH_000001⟧).</param>
/// <param name="Index">Belirtecin sayısal kimliği (1'den başlayan sayaç).</param>
/// <param name="OriginalMarkup">Düğümün orijinal OuterHtml veya XML içeriği.</param>
/// <param name="TagName">Maskelenen elemanın HTML/XML etiket adı (örn: code, kbd, a, svg, math).</param>
public readonly record struct MaskingToken(
    string Token,
    int Index,
    string OriginalMarkup,
    string TagName);
