using System.Diagnostics;
using Glossator.AI.Diagnostics;
using Glossator.AI.Executors;
using Glossator.AI.Extensions;
using Glossator.AI.Models;
using Glossator.AI.Streaming;
using Glossator.Core.Parsing;
using Glossator.Core.Rules;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger<BookLocalizationPipeline> _logger;
    private string _cachedStyleGuide = string.Empty;

    public event Action<PipelineStepLog>? StepProgress;

    public BookLocalizationPipeline(
        [FromKeyedServices(AiServiceExtensions.KeyStyleAnalyst)] IChatClient styleAnalystClient,
        [FromKeyedServices(AiServiceExtensions.KeyTranslator)] IChatClient translatorClient,
        [FromKeyedServices(AiServiceExtensions.KeyReviewer)] IChatClient reviewerClient,
        TermGlossingPostProcessor postProcessor,
        string stylePrompt,
        string translatorPrompt,
        string reviewerPrompt,
        ILogger<BookLocalizationPipeline>? logger = null)
    {
        _styleAnalystClient = styleAnalystClient;
        _translatorClient = translatorClient;
        _reviewerClient = reviewerClient;
        _stylePrompt = stylePrompt;
        _translatorPrompt = translatorPrompt;
        _postProcessor = postProcessor;
        _logger = logger ?? NullLogger<BookLocalizationPipeline>.Instance;

        // MAF Döngüsel İş Akışı: StyleAnalyst -> Translator <-> Reviewer
        _cyclicWorkflow = new CyclicTranslationWorkflow(
            styleAnalystClient,
            translatorClient,
            reviewerClient,
            stylePrompt,
            translatorPrompt,
            reviewerPrompt,
            EmitProgress);
    }

    private void EmitProgress(PipelineStepLog log)
    {
        StepProgress?.Invoke(log);
        if (!log.IsTransient)
        {
            _logger.LogInformation("[{Stage}] {Summary} (Attempt: {Attempt})", log.Stage, log.Summary, log.Attempt);
        }
        else
        {
            _logger.LogTrace("[{Stage}] {Summary} (Attempt: {Attempt})", log.Stage, log.Summary, log.Attempt);
        }
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
        activity?.SetTag("sample.length", chapterSampleText.Length);
        _logger.LogInformation("Analyzing chapter style and tone fingerprint (Sample length: {Length} chars)...", chapterSampleText.Length);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _stylePrompt),
            new(ChatRole.User, $"[ANALİZ EDİLECEK BÖLÜM METNİ]:\n{chapterSampleText}")
        };

        var streamResult = await StreamingChatHelper.StreamResponseAsync(
            _styleAnalystClient,
            messages,
            stageName: "StyleAnalyst",
            attempt: 1,
            activity: activity,
            stepLogger: EmitProgress,
            cancellationToken: cancellationToken);
        _cachedStyleGuide = streamResult.Text.Trim();

        _logger.LogInformation("StyleAnalyst completed. Guide extracted ({GuideLength} chars).", _cachedStyleGuide.Length);

        EmitProgress(new PipelineStepLog(
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

        _logger.LogInformation("Translating logical section '{Title}' ({SegmentsCount} blocks)...", section.Title, section.Segments.Count);

        var activeStyleGuide = styleGuide ?? _cachedStyleGuide;
        var promptText = section.ToPromptFormat();
        var combinedText = section.GetCombinedText();

        // 1. Pre-Processing Child Activity: Terimleri ayıkla (0-20ms)
        IReadOnlyList<string> doNotTranslateTerms;
        IReadOnlyDictionary<string, string> glossaryTerms;

        using (var preActivity = GlossatorDiagnostics.ActivitySource.StartActivity("PreProcessor"))
        {
            (doNotTranslateTerms, glossaryTerms) = await _postProcessor.GetTermsForSectionAsync(combinedText);
            preActivity?.SetTag("terms.glossary_count", glossaryTerms.Count);
            preActivity?.SetTag("terms.protected_count", doNotTranslateTerms.Count);

            _logger.LogInformation("PreProcessor for '{Title}': {GlossaryCount} glossary terms, {ProtectedCount} protected terms",
                section.Title, glossaryTerms.Count, doNotTranslateTerms.Count);
        }

        EmitProgress(new PipelineStepLog(
            Stage: "PreProcessor",
            Summary: $"Konu Bölümü ({section.Title}): {section.Segments.Count} blok, {glossaryTerms.Count} sözlük terimi, {doNotTranslateTerms.Count} dokunulmaz terim",
            Content: glossaryTerms.Count > 0 ? string.Join(", ", glossaryTerms.Select(kv => $"{kv.Key} => {kv.Value}")) : "Özel terim yok"));

        // 2. MAF Döngüsel İş Akışı: StyleAnalyst -> Translator <-> Reviewer
        string reviewedText;
        using (var workflowActivity = GlossatorDiagnostics.ActivitySource.StartActivity("CyclicWorkflow"))
        {
            workflowActivity?.SetTag("section.title", section.Title);
            reviewedText = await _cyclicWorkflow.ExecuteAsync(
                promptText,
                doNotTranslateTerms,
                glossaryTerms,
                activeStyleGuide,
                cancellationToken);
        }

        // Güvenlik Ağı: Beklenmedik boş çıktıda doğrudan Translator ile kurtar
        if (string.IsNullOrWhiteSpace(reviewedText))
        {
            using var fallbackActivity = GlossatorDiagnostics.ActivitySource.StartActivity("DirectTranslatorFallback");
            _logger.LogWarning("Cyclic workflow returned empty text for '{Title}'. Initiating fallback...", section.Title);
            var fallbackMessages = new List<ChatMessage>
            {
                new(ChatRole.System, _translatorPrompt),
                new(ChatRole.User, $"[YAZAR ÜSLUP KILAVUZU]:\n{activeStyleGuide}\n\n[ÇEVRİLECEK METİN]:\n{promptText}")
            };
            var fallbackResult = await StreamingChatHelper.StreamResponseAsync(
                _translatorClient,
                fallbackMessages,
                stageName: "FallbackTranslator",
                attempt: 1,
                activity: fallbackActivity,
                stepLogger: EmitProgress,
                cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(fallbackResult.Text))
            {
                reviewedText = fallbackResult.Text.Trim();
            }
            else
            {
                _logger.LogError("Fallback translation failed for section '{Title}'. Model returned empty response. Segments will remain untranslated.", section.Title);
                reviewedText = string.Empty;
            }
        }

        // 3. Blokları ayrıştırıp segmentlere yerleştir
        var unpacked = section.TryUnpackTranslations(reviewedText, out var missing);
        if (!unpacked && missing.Count > 0)
        {
            GlossatorDiagnostics.RecoveryAttemptsCounter.Add(1);
            _logger.LogWarning("Section '{Title}': {MissingCount} blocks missing from model output. Starting micro-recovery...", section.Title, missing.Count);

            EmitProgress(new PipelineStepLog(
                Stage: "Warning",
                Summary: $"Uyarı: {missing.Count} blok model çıktısında eksik kaldı. Mikro kurtarma başlatılıyor...",
                Content: string.Join(", ", missing.Select(m => $"BLOK_{m:D3}"))));

            // Delta-Retry Kurtarma Döngüsü
            using var recoveryActivity = GlossatorDiagnostics.ActivitySource.StartActivity("MicroRecovery");
            recoveryActivity?.SetTag("missing.count", missing.Count);

            try
            {
                var deltaSection = section.CreateDeltaSection(missing);
                var deltaPrompt = $"[ACİL MİKRO KURTARMA: AŞAĞIDAKİ BLOKLAR EKSİK KALDI. LÜTFEN YALNIZCA BU BLOKLARI ÇEVİR VE ETİKETLERİNİ EKSİKSİZ KORU]:\n\n{deltaSection.ToPromptFormat()}";

                var recoveryMessages = new List<ChatMessage>
                {
                    new(ChatRole.System, _translatorPrompt),
                    new(ChatRole.User, $"[YAZAR ÜSLUP KILAVUZU]:\n{activeStyleGuide}\n\n{deltaPrompt}")
                };

                var deltaResult = await StreamingChatHelper.StreamResponseAsync(
                    _translatorClient,
                    recoveryMessages,
                    stageName: "MicroRecovery",
                    attempt: 1,
                    activity: recoveryActivity,
                    stepLogger: EmitProgress,
                    cancellationToken: cancellationToken);

                if (!string.IsNullOrWhiteSpace(deltaResult.Text))
                {
                    deltaSection.TryUnpackTranslations(deltaResult.Text, out _);
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
                        _logger.LogInformation("Section '{Title}': Micro-recovery succeeded for all missing blocks.", section.Title);
                        EmitProgress(new PipelineStepLog(
                            Stage: "RecoverySuccess",
                            Summary: "Eksik kalan tüm bloklar mikro kurtarma döngüsüyle başarıyla tamamlandı",
                            Content: deltaResult.Text));
                    }
                    else
                    {
                        _logger.LogWarning("Section '{Title}': After micro-recovery, {StillMissing} blocks remain missing.", section.Title, missing.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                recoveryActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "Micro-recovery failed for section '{Title}'", section.Title);
                EmitProgress(new PipelineStepLog(
                    Stage: "Warning",
                    Summary: $"Mikro kurtarma sırasında hata oluştu: {ex.Message}",
                    Content: ex.ToString()));
            }
        }

        // Post-Processing Child Activity & Metrik Güncellemesi
        using (var postActivity = GlossatorDiagnostics.ActivitySource.StartActivity("PostProcessor"))
        {
            var finalizedCount = section.Segments.Count - missing.Count;
            postActivity?.SetTag("blocks.finalized", finalizedCount);
            postActivity?.SetTag("blocks.total", section.Segments.Count);

            GlossatorDiagnostics.SectionsTranslatedCounter.Add(1);
            _logger.LogInformation("Section '{Title}' successfully finalized: {FinalizedCount}/{TotalCount} blocks translated.",
                section.Title, finalizedCount, section.Segments.Count);
        }

        EmitProgress(new PipelineStepLog(
            Stage: "PostProcessor",
            Summary: $"Bölüm ({section.Title}) Tamamlandı: {section.Segments.Count - missing.Count}/{section.Segments.Count} blok başarıyla yerelleştirildi",
            Content: reviewedText));

        return section;
    }
}
