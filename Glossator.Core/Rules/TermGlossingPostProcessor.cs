using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;

namespace Glossator.Core.Rules;

public class TermGlossingPostProcessor
{
    private readonly SqliteGlossaryRepository _repository;
    private readonly TermUsageTracker _tracker;
    private List<GlossaryEntry>? _terms;

    public TermGlossingPostProcessor(SqliteGlossaryRepository repository, TermUsageTracker tracker)
    {
        _repository = repository;
        _tracker = tracker;
    }

    /// <summary>
    /// Yeni bir bölüme geçildiğinde terim sayacını izole etmek için çağrılır.
    /// </summary>
    public void BeginChapterScope(string scopeId)
    {
        _tracker.BeginNewScope(scopeId);
    }

    private async Task<List<GlossaryEntry>> GetCachedTermsAsync()
    {
        if (_terms == null)
        {
            var terms = await _repository.GetAllTermsAsync().ConfigureAwait(false);
            _terms = terms
                .OrderByDescending(t => t.EnglishTerm.Length)
                .ToList();
        }
        return _terms;
    }

    /// <summary>
    /// Metinde geçen terimleri 3 politikaya göre ayıklar:
    /// 1. AlwaysEnglish: Dokunulmaz isimler, protokoller ve kısaltmalar (LLM, API, Microsoft Agent Framework)
    /// 2. AlwaysTurkish: Resmi Türkçe karşılıklar (ML practitioners -> makine öğrenimi uzmanları, data scientist -> veri bilimci, models -> modeller, data -> veri, cost -> maliyet)
    /// 3. ProgressiveGloss: İlk geçişte hibrit Türkçe (İngilizce), 2+ geçişte sadece Türkçe (otonom ajanlar (autonomous agents))
    /// </summary>
    public async Task<(List<string> AlwaysEnglishTerms, Dictionary<string, string> GlossaryTerms)> GetTermsForSectionAsync(
        string sectionText,
        bool isHeading = false)
    {
        if (isHeading || string.IsNullOrWhiteSpace(sectionText))
        {
            return (new List<string>(), new Dictionary<string, string>());
        }

        var terms = await GetCachedTermsAsync().ConfigureAwait(false);
        var alwaysEnglish = new List<string>();
        var glossaryMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in terms)
        {
            if (!EnglishTermMatcher.IsTermPresent(sectionText, term.EnglishTerm))
            {
                continue;
            }

            switch (term.Policy)
            {
                case GlossingPolicy.AlwaysEnglish:
                    if (!alwaysEnglish.Contains(term.EnglishTerm, StringComparer.OrdinalIgnoreCase))
                    {
                        alwaysEnglish.Add(term.EnglishTerm);
                    }
                    break;

                case GlossingPolicy.AlwaysTurkish:
                    if (!string.IsNullOrWhiteSpace(term.TurkishTerm))
                    {
                        glossaryMappings.TryAdd(term.EnglishTerm, term.TurkishTerm);
                    }
                    break;

                case GlossingPolicy.ProgressiveGloss:
                    if (!string.IsNullOrWhiteSpace(term.TurkishTerm))
                    {
                        var decision = _tracker.RecordUsage(term);
                        if (decision == TermFormatDecision.Hybrid)
                        {
                            // 1. kullanım: Türkçe karşılık + (İngilizce orijinali)
                            glossaryMappings.TryAdd(term.EnglishTerm, $"{term.TurkishTerm} ({term.EnglishTerm})");
                        }
                        else
                        {
                            // 2 ve sonrası: Yalnızca doğal Türkçe karşılık
                            glossaryMappings.TryAdd(term.EnglishTerm, term.TurkishTerm);
                        }
                    }
                    break;
            }
        }

        return (alwaysEnglish, glossaryMappings);
    }

    /// <summary>
    /// Önbellekten (checkpoint) yüklenen segmentlerdeki terim kullanımlarını 
    /// sayaç motoruna (TermUsageTracker) işler. Böylece daha sonraki segmentlerde parantez kuralı bozulmaz.
    /// </summary>
    public async Task RegisterCachedSectionUsageAsync(string sectionText, bool isHeading = false)
    {
        await GetTermsForSectionAsync(sectionText, isHeading).ConfigureAwait(false);
    }

}
