using Glossator.Core.Models.Glossary;
using System;
using System.Collections.Generic;
using System.Text;

namespace Glossator.Core.Rules
{
    public enum TermFormatDecision
    {
        Hybrid,      // 1 ve 2. kullanım: pipeline (boru hattı) veya ekli hali
        EnglishOnly, // 3 ve sonrası: pipeline
        TurkishOnly  // Doğrudan Türkçe
    }

    /// <summary>
    /// Çeviride terimlerin o ünitede (Chapter veya Part) kaç kez geçtiğini sayan ve her yeni üniteye geçildiğinde sayaçları sıfırlayan deterministik kural motoru.
    /// </summary>
    public class TermUsageTracker
    {
        // Terim -> O ünitedeki kullanım sayısı
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Şu an işlenen ünite/bölüm kimliği (örn: "ch01.html")
        /// </summary>
        public string CurrentScopeId { get; private set; }
        public TermUsageTracker(string initialScopeId = "default")
        {
            CurrentScopeId = initialScopeId;
        }
        /// <summary>
        /// Yeni bir bölüme geçildiğinde çağrılır. Tüm sayaçları sıfırlar!
        /// </summary>
        public void BeginNewScope(string newScopeId)
        {
            CurrentScopeId = newScopeId;
            _counts.Clear(); // Yeni bölüm için sayaç sıfırlanır!
        }
        /// <summary>
        /// Terim metin içinde her geçtiğinde çağrılır; sayacı 1 artırır ve 
        /// kurala göre nasıl basılacağına (Hybrid mi EnglishOnly mi) karar verir.
        /// </summary>
        public TermFormatDecision RecordUsage(GlossaryEntry term)
        {
            // 1. Zaten bildiğin sektör jargonuysa sayaçtan bağımsız her zaman İngilizce
            if (term.Policy == GlossingPolicy.AlwaysEnglish)
            {
                return TermFormatDecision.EnglishOnly;
            }
            // 2. Düz kavramsa her zaman Türkçe
            if (term.Policy == GlossingPolicy.AlwaysTurkish)
            {
                return TermFormatDecision.TurkishOnly;
            }
            // 3. Aşamalı (Progressive) Öğrenme Kuralı:
            var key = term.NormalizedKey;
            _counts.TryGetValue(key, out var currentCount);
            currentCount++;
            _counts[key] = currentCount;
            // 1 ve 2. geçişte HİBRİT, 3 ve sonrasında SAF İNGİLİZCE
            return currentCount <= 2
                ? TermFormatDecision.Hybrid
                : TermFormatDecision.EnglishOnly;
        }
        /// <summary>
        /// Bir terimin o ünitede şu ana kadar kaç kez geçtiğini sorgular.
        /// </summary>
        public int GetCurrentCount(string englishTerm) =>
            _counts.TryGetValue(englishTerm.Trim().ToLowerInvariant(), out var count) ? count : 0;
    }
}
