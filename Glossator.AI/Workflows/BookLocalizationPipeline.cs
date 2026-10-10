using Glossator.AI.Diagnostics;
using Glossator.AI.Executors;
using Glossator.AI.Extensions;
using Glossator.AI.Models;
using Glossator.Core.Parsing;
using Glossator.Core.Rules;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Glossator.AI.Workflows;

public class BookLocalizationPipeline
{
    private readonly IChatClient _styleAnalystClient;
    private readonly IChatClient _translatorClient;
    private readonly IChatClient _reviewerClient;
    private readonly string _stylePrompt;
    private readonly string _translatorPrompt;
    private readonly CyclicTranslationWorkflow _cyclicWorkflow;
    private readonly TermGlossingPostProcessor _postProcessor;
    private string _cachedStyleGuide = string.Empty;

    public event Action<PipelineStepLog>? StepProgress;

    public BookLocalizationPipeline(
        [FromKeyedServices(AiServiceExtensions.KeyStyleAnalyst)] IChatClient styleAnalystClient,
        [FromKeyedServices(AiServiceExtensions.KeyTranslator)] IChatClient translatorClient,
        [FromKeyedServices(AiServiceExtensions.KeyReviewer)] IChatClient reviewerClient,
        TermGlossingPostProcessor postProcessor,
        string stylePrompt,
        string translatorPrompt,
        string reviewerPrompt)
    {
        _styleAnalystClient = styleAnalystClient;
        _translatorClient = translatorClient;
        _reviewerClient = reviewerClient;
        _stylePrompt = stylePrompt;
        _translatorPrompt = translatorPrompt;
        _postProcessor = postProcessor;

        Action<PipelineStepLog> logRelay = log => StepProgress?.Invoke(log);

        // MAF Döngüsel İş Akışı: StyleAnalyst -> Translator <-> Reviewer
        _cyclicWorkflow = new CyclicTranslationWorkflow(
            styleAnalystClient,
            translatorClient,
            reviewerClient,
            stylePrompt,
            translatorPrompt,
            reviewerPrompt,
            logRelay);
    }

    /// <summary>
    /// Yeni bir bölüme başlandığında terim sayaçlarını sıfırlar ve kapsamı izole eder.
    /// </summary>
    public void BeginChapterScope(string chapterScopeId)
    {
        _postProcessor.BeginChapterScope(chapterScopeId);
        _cachedStyleGuide = string.Empty;
    }

    /// <summary>
    /// Bölümün başında 1 kez çağrılabilir. Yazarın o ünitedeki dil, ton ve üslup parmak izini çıkarır ve hafızaya kaydeder.
    /// </summary>
    public async Task<string> AnalyzeStyleAsync(string chapterSampleText, CancellationToken cancellationToken = default)
    {
        using var activity = GlossatorDiagnostics.ActivitySource.StartActivity("AnalyzeStyle");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _stylePrompt),
            new(ChatRole.User, $"[ANALİZ EDİLECEK BÖLÜM METNİ]:\n{chapterSampleText}")
        };

        var response = await _styleAnalystClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        _cachedStyleGuide = response.Text?.Trim() ?? string.Empty;

        StepProgress?.Invoke(new PipelineStepLog(
            Stage: "StyleAnalyst",
            Summary: "Bölüm Başında Yazar Üslup & Ton Parmak İzi Çıkarıldı",
            Content: _cachedStyleGuide));

        return _cachedStyleGuide;
    }

    /// <summary>
    /// Önbellekteki üslup kılavuzunu manuel set etmek veya dışarıdan yüklemek için.
    /// </summary>
    public void SetStyleGuide(string styleGuide)
    {
        _cachedStyleGuide = styleGuide;
    }

    /// <summary>
    /// Önbellekten yüklenen segment için sayaçları senkronize eder (Rehydration).
    /// </summary>
    public async Task RegisterCachedSectionAsync(string sectionText, bool isHeading = false)
    {
        await _postProcessor.RegisterCachedSectionUsageAsync(sectionText, isHeading);
    }

    /// <summary>
    /// Mantıksal bir konuyu (HtmlSection - başlık ve altındaki paragraflar/tanımlar) 
    /// bütüncül bağlamla tek bir MAF döngüsünde çevirir ve segmentleri geri unpack eder.
    /// </summary>
    public async Task<HtmlSection> TranslateSectionAsync(
        HtmlSection section,
        string? styleGuide = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = GlossatorDiagnostics.ActivitySource.StartActivity("TranslateLogicalSection");
        activity?.SetTag("section.title", section.Title);
        activity?.SetTag("section.segments_count", section.Segments.Count);

        var activeStyleGuide = styleGuide ?? _cachedStyleGuide;
        var promptText = section.ToPromptFormat();
        var combinedText = section.GetCombinedText();

        // 1. Pre-Processing: Tüm section metnindeki terimleri ayıkla
        var (doNotTranslateTerms, glossaryTerms) = await _postProcessor.GetTermsForSectionAsync(combinedText);

        StepProgress?.Invoke(new PipelineStepLog(
            Stage: "PreProcessor",
            Summary: $"Konu Bölümü ({section.Title}): {section.Segments.Count} blok, {glossaryTerms.Count} sözlük terimi, {doNotTranslateTerms.Count} dokunulmaz terim",
            Content: glossaryTerms.Count > 0 ? string.Join(", ", glossaryTerms.Select(kv => $"{kv.Key} => {kv.Value}")) : "Özel terim yok"));

        // 2. MAF Döngüsel İş Akışı: StyleAnalyst -> Translator <-> Reviewer
        var reviewedText = await _cyclicWorkflow.ExecuteAsync(
            promptText,
            doNotTranslateTerms,
            glossaryTerms,
            activeStyleGuide,
            cancellationToken);

        // Güvenlik Ağı: Beklenmedik boş çıktıda doğrudan Translator ile kurtar
        if (string.IsNullOrWhiteSpace(reviewedText))
        {
            var fallbackMessages = new List<ChatMessage>
            {
                new(ChatRole.System, _translatorPrompt),
                new(ChatRole.User, $"[YAZAR ÜSLUP KILAVUZU]:\n{activeStyleGuide}\n\n[ÇEVRİLECEK METİN]:\n{promptText}")
            };
            var resp = await _translatorClient.GetResponseAsync(fallbackMessages, cancellationToken: cancellationToken);
            reviewedText = resp.Text?.Trim() ?? promptText;
        }

        // 2. Blokları ayrıştırıp segmentlere yerleştir
        var unpacked = section.TryUnpackTranslations(reviewedText, out var missing);
        if (!unpacked && missing.Count > 0)
        {
            StepProgress?.Invoke(new PipelineStepLog(
                Stage: "Warning",
                Summary: $"Uyarı: {missing.Count} blok model çıktısında eksik kaldı. Mikro kurtarma başlatılıyor...",
                Content: string.Join(", ", missing.Select(m => $"BLOK_{m:D3}"))));

            // Delta-Retry Kurtarma Döngüsü: Yalnızca eksik blokları çevirmek üzere mikro prompt gönder
            try
            {
                var deltaSection = section.CreateDeltaSection(missing);
                var deltaPrompt = $"[ACİL MİKRO KURTARMA: AŞAĞIDAKİ BLOKLAR EKSİK KALDI. LÜTFEN YALNIZCA BU BLOKLARI ÇEVİR VE ETİKETLERİNİ EKSİKSİZ KORU]:\n\n{deltaSection.ToPromptFormat()}";

                var recoveryMessages = new List<ChatMessage>
                {
                    new(ChatRole.System, _translatorPrompt),
                    new(ChatRole.User, $"[YAZAR ÜSLUP KILAVUZU]:\n{activeStyleGuide}\n\n{deltaPrompt}")
                };

                var deltaResp = await _translatorClient.GetResponseAsync(recoveryMessages, cancellationToken: cancellationToken);
                if (!string.IsNullOrWhiteSpace(deltaResp.Text))
                {
                    deltaSection.TryUnpackTranslations(deltaResp.Text, out _);
                    foreach (var dSeg in deltaSection.Segments)
                    {
                        if (!string.IsNullOrWhiteSpace(dSeg.TranslatedText))
                        {
                            var targetSeg = section.Segments.FirstOrDefault(s => s.Index == dSeg.Index);
                            if (targetSeg != null)
                            {
                                targetSeg.TranslatedText = dSeg.TranslatedText;
                            }
                        }
                    }

                    missing = section.Segments.Where(s => string.IsNullOrWhiteSpace(s.TranslatedText)).Select(s => s.Index).ToList();
                    if (missing.Count == 0)
                    {
                        StepProgress?.Invoke(new PipelineStepLog(
                            Stage: "RecoverySuccess",
                            Summary: "Eksik kalan tüm bloklar mikro kurtarma döngüsüyle başarıyla tamamlandı",
                            Content: deltaResp.Text));
                    }
                }
            }
            catch (Exception ex)
            {
                StepProgress?.Invoke(new PipelineStepLog(
                    Stage: "Warning",
                    Summary: $"Mikro kurtarma sırasında hata oluştu: {ex.Message}",
                    Content: ex.ToString()));
            }
        }

        StepProgress?.Invoke(new PipelineStepLog(
            Stage: "PostProcessor",
            Summary: $"Bölüm ({section.Title}) Tamamlandı: {section.Segments.Count - missing.Count}/{section.Segments.Count} blok başarıyla yerelleştirildi",
            Content: reviewedText));

        return section;
    }

    /// <summary>
    /// Bir alt bölümü (Section) MAF boru hattında uçtan uca çevirir:
    /// 1. C# ile SQLite'taki teknik terimleri yakalar (Do-Not-Translate Whitelist).
    /// 2. MAF Döngüsel İş Akışı (Translator <-> Reviewer) çevirir ve denetler.
    /// 3. C# ile 1 ve 2. kullanımların yanına deterministik parantez içi Türkçe anlamını ekler.
    /// </summary>
    public async Task<string> TranslateSectionAsync(
        string sectionText,
        string? styleGuide = null,
        bool isHeading = false,
        CancellationToken cancellationToken = default)
    {
        using var activity = GlossatorDiagnostics.ActivitySource.StartActivity("TranslateSection");
        activity?.SetTag("book.is_heading", isHeading);
        activity?.SetTag("book.text_length", sectionText.Length);

        var activeStyleGuide = styleGuide ?? _cachedStyleGuide;

        // 1. Pre-Processing: 3-Tier Terim Ayıklama (AlwaysEnglish, AlwaysTurkish, ProgressiveGloss)
        var (doNotTranslateTerms, glossaryTerms) = await _postProcessor.GetTermsForSectionAsync(sectionText, isHeading);

        var preProcessorSummary = isHeading
            ? "Başlık segmenti: Doğal başlık akıcılığı için terim kuralları devre dışı bırakıldı"
            : $"Pre-Processor: {doNotTranslateTerms.Count} dokunulmaz İngilizce terim, {glossaryTerms.Count} resmi Türkçe sözlük karşılığı ayıklandı";

        var preProcessorContent = glossaryTerms.Count > 0
            ? $"Sözlük: {string.Join(", ", glossaryTerms.Select(kv => $"{kv.Key} => {kv.Value}"))} | Dokunulmaz: {string.Join(", ", doNotTranslateTerms)}"
            : (doNotTranslateTerms.Count > 0 ? $"Dokunulmaz: {string.Join(", ", doNotTranslateTerms)}" : "Özel terim bulunamadı");

        StepProgress?.Invoke(new PipelineStepLog(
            Stage: "PreProcessor",
            Summary: preProcessorSummary,
            Content: preProcessorContent));

        // 2. MAF Döngüsel İş Akışı: StyleAnalyst -> Translator <-> Reviewer
        var reviewedText = await _cyclicWorkflow.ExecuteAsync(
            sectionText, 
            doNotTranslateTerms, 
            glossaryTerms, 
            activeStyleGuide, 
            cancellationToken);

        // Güvenlik Ağı: Beklenmedik boş çıktıda doğrudan Translator ile kurtar
        if (string.IsNullOrWhiteSpace(reviewedText))
        {
            var fallbackMessages = new List<ChatMessage>
            {
                new(ChatRole.System, _translatorPrompt),
                new(ChatRole.User, $"[YAZAR ÜSLUP KILAVUZU]:\n{activeStyleGuide}\n\n[ÇEVRİLECEK METİN]:\n{sectionText}")
            };
            var resp = await _translatorClient.GetResponseAsync(fallbackMessages, cancellationToken: cancellationToken);
            reviewedText = resp.Text?.Trim() ?? sectionText;
        }

        // 3. Post-Processing: Başlık değilse 1 ve 2. kullanımlara deterministik parantez enjeksiyonu
        var finalText = isHeading
            ? reviewedText
            : await _postProcessor.ApplyParenthesesAsync(reviewedText);

        StepProgress?.Invoke(new PipelineStepLog(
            Stage: "PostProcessor",
            Summary: isHeading
                ? "Başlık temiz Türkçe ile onaylandı"
                : "Kural Motoru: 1-2. kullanımlara Türkçe parantez enjekte edildi, 3+ İngilizce bırakıldı",
            Content: finalText));

        return finalText;
    }
}
