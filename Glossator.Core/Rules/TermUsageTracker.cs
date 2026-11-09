namespace Glossator.Core.Rules;

using System.Collections.Concurrent;
using Glossator.Core.Models.Glossary;

public enum TermFormatDecision
{
    Hybrid,      // 1. kullanım: Türkçe (İngilizce)
    PureTurkish, // 2 ve sonrası: Yalnızca Türkçe
    EnglishOnly  // Sektör jargonu: AlwaysEnglish
}

/// <summary>
/// Çeviride terimlerin o ünitede (Chapter veya Part) kaç kez geçtiğini sayan ve her yeni üniteye geçildiğinde sayaçları sıfırlayan thread-safe kural motoru.
/// </summary>
public sealed class TermUsageTracker(string initialScopeId = "default")
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public string CurrentScopeId { get; private set; } = initialScopeId;

    /// <summary>
    /// Yeni bir bölüme geçildiğinde çağrılır. Bölüm içi sayaçları sıfırlar.
    /// </summary>
    public void BeginNewScope(string newScopeId)
    {
        CurrentScopeId = newScopeId;
        _counts.Clear();
    }

    /// <summary>
    /// Terim metin içinde her geçtiğinde çağrılır; sayacı atomik artırır ve 
    /// kurala göre nasıl basılacağına (Hybrid mi PureTurkish mi) karar verir.
    /// </summary>
    public TermFormatDecision RecordUsage(GlossaryEntry term)
    {
        if (term.Policy == GlossingPolicy.AlwaysEnglish)
        {
            return TermFormatDecision.EnglishOnly;
        }

        if (term.Policy == GlossingPolicy.AlwaysTurkish)
        {
            return TermFormatDecision.PureTurkish;
        }

        // Aşamalı (Progressive) Öğrenme Kuralı:
        var currentCount = _counts.AddOrUpdate(
            term.NormalizedKey,
            1,
            static (_, count) => count + 1);

        // 1. geçişte HİBRİT "Türkçe (İngilizce)", 2 ve sonrasında SAF TÜRKÇE
        return currentCount <= 1
            ? TermFormatDecision.Hybrid
            : TermFormatDecision.PureTurkish;
    }

    /// <summary>
    /// Bir terimin o ünitede şu ana kadar kaç kez geçtiğini sorgular.
    /// </summary>
    public int GetCurrentCount(string englishTerm) =>
        _counts.TryGetValue(englishTerm.Trim().ToLowerInvariant(), out var count) ? count : 0;
}
