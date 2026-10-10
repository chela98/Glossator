using System.Text.Json;
using System.Text.RegularExpressions;
using Glossator.AI.Models;
using Glossator.Core.Rules;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Glossator.AI.Executors;

public sealed class ReviewerExecutor : Executor<TranslationReviewState, TranslationReviewState>
{
    private readonly IChatClient _chatClient;
    private readonly string _systemPrompt;
    private readonly Action<PipelineStepLog>? _stepLogger;

    public ReviewerExecutor(
        IChatClient chatClient, 
        string systemPrompt, 
        Action<PipelineStepLog>? stepLogger = null) 
        : base("ReviewerExecutor")
    {
        _chatClient = chatClient;
        _systemPrompt = systemPrompt;
        _stepLogger = stepLogger;
    }

    public override async ValueTask<TranslationReviewState> HandleAsync(
        TranslationReviewState state, 
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        // ─────────────────────────────────────────────────────────────────────────────
        // 1. ADIM: DETERMINİSTİK C# ÖN FİLTRESİ (KIRIK TASLAK KORUMASI - 0 MS)
        // ─────────────────────────────────────────────────────────────────────────────
        var missingMasks = FindMissingMasks(state.OriginalText, state.CurrentTranslation);
        var missingTerms = FindMissingTerms(state.DoNotTranslateTerms, state.CurrentTranslation);
        var missingBlocks = FindMissingBlocks(state.OriginalText, state.CurrentTranslation);

        // HATA VARSA: LLM Reviewer'a HİÇ GİTMEDEN doğrudan Translator'a iade et!
        if ((missingMasks.Count > 0 || missingTerms.Count > 0 || missingBlocks.Count > 0) && state.AttemptCount < 3)
        {
            var errors = new List<string>();
            if (missingBlocks.Count > 0)
                errors.Add($"Eksik bloklar: {string.Join(", ", missingBlocks)}. Tüm [BLOK_xxx] etiketlerini eksiksiz üretmelisin!");
            if (missingMasks.Count > 0)
                errors.Add($"Eksik yer tutucular: {string.Join(", ", missingMasks)}. [[INDEX_xx]] ve [[CODE_xx]] etiketleri orijinal kalmalı!");
            if (missingTerms.Count > 0)
                errors.Add($"Silinmiş dokunulmaz terimler: {string.Join(", ", missingTerms)}. Bu terimler orijinal İngilizce kalmalı!");

            var feedback = string.Join("\n", errors);

            _stepLogger?.Invoke(new PipelineStepLog(
                Stage: "Reviewer",
                Summary: "Kural İhlali Tespit Edildi (C# Ön Filtresi) -> Translator'a İade Ediliyor",
                Content: feedback,
                Attempt: state.AttemptCount));

            return state with
            {
                IsApproved = false,
                Feedback = feedback,
                AttemptCount = state.AttemptCount + 1
            };
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // 2. ADIM: REVIEWER LLM ÇAĞRISI (JSON PATCH SÖZLEŞMESİ)
        // ─────────────────────────────────────────────────────────────────────────────
        var termsList = state.DoNotTranslateTerms.Count > 0
            ? string.Join(", ", state.DoNotTranslateTerms)
            : "Yok";

        var glossarySection = state.GlossaryTerms != null && state.GlossaryTerms.Count > 0
            ? string.Join("\n", state.GlossaryTerms.Select(kv => $"- {kv.Key} => {kv.Value}"))
            : "Yok";

        var userMessage = $"""
            [ORİJİNAL İNGİLİZCE METİN]:
            {state.OriginalText}

            [TERİM SÖZLÜĞÜ - RESMİ TÜRKÇE KARŞILIKLAR]:
            {glossarySection}
            (DENETİM: Çevirmenin bu terimleri resmi Türkçe karşılıklarıyla ("makine öğrenimi uygulayıcıları / uzmanları", "veri bilimci", "modeller", "veri setleri", "maliyet") çevirdiğinden ve 'models ler', 'cost'unu', 'ML pratisyenleri' gibi bozuk Plaza Türkçesi kalıpları KULLANMADIĞINDAN emin ol. Varsa derhal akıcı yayın Türkçesine düzelt).
            (METNE SADAKAT VE AŞIRI ÇEVİRİ DENETİMİ: Çevirmenin yazarın söylemediği kavramları (örneğin 'irade', 'şuur') veya yapay edebi süsleri ekleyip eklemediğini denetle; varsa yazarın sade ifadesine geri çek!)

            [DOKUNULMAYACAK MARKA, PROTOKOL VE KISALTMALAR]:
            {termsList}
            (KURAL: Bu özel isim ve kısaltmaları orijinal İngilizce halleriyle koru).

            [ÇEVİRİ TASLAĞI]:
            {state.CurrentTranslation}
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, userMessage)
        };

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        var rawResponse = response.Text?.Trim() ?? string.Empty;

        // ─────────────────────────────────────────────────────────────────────────────
        // 3. ADIM: JSON PATCH AYRIŞTIRMA VE C# YAMA BİRLEŞTİRME (PATCH MERGER)
        // ─────────────────────────────────────────────────────────────────────────────
        var patchResult = ParseReviewerResponse(rawResponse);
        string finalApprovedText = state.CurrentTranslation;

        if (patchResult != null)
        {
            // Eğer model çeviriyi kökten reddettiyse Translator'a geri dön
            if (!patchResult.IsApproved && state.AttemptCount < 3)
            {
                var feedback = !string.IsNullOrWhiteSpace(patchResult.Feedback)
                    ? patchResult.Feedback
                    : "Taslak yayın standardını karşılamadı, lütfen üslup kılavuzuna uyarak yeniden çevir.";

                _stepLogger?.Invoke(new PipelineStepLog(
                    Stage: "Reviewer",
                    Summary: "Redaksiyon Reddedildi -> Translator'a Döngüsel İade Ediliyor",
                    Content: feedback,
                    Attempt: state.AttemptCount));

                return state with
                {
                    IsApproved = false,
                    Feedback = feedback,
                    AttemptCount = state.AttemptCount + 1
                };
            }

            // Model onayladı: Yamalar var mı kontrol et
            if (patchResult.Patches != null && patchResult.Patches.Count > 0)
            {
                var patchedText = ApplyPatches(state.CurrentTranslation, patchResult.Patches, out var appliedBlocks);

                // C# Gatekeeper: Yama uygulandıktan sonra maske veya blok kaybı var mı?
                var postMissingMasks = FindMissingMasks(state.OriginalText, patchedText);
                var postMissingBlocks = FindMissingBlocks(state.OriginalText, patchedText);

                if (postMissingMasks.Count > 0 || postMissingBlocks.Count > 0)
                {
                    _stepLogger?.Invoke(new PipelineStepLog(
                        Stage: "Reviewer",
                        Summary: "Yama İhlali Engellendi (C# Güvenlik Duvarı)",
                        Content: "Uygulanan yamada eksik maske veya blok tespit edildi. Çevirmenin eksiksiz taslağı korundu.",
                        Attempt: state.AttemptCount));

                    finalApprovedText = state.CurrentTranslation;
                }
                else
                {
                    finalApprovedText = patchedText;
                    _stepLogger?.Invoke(new PipelineStepLog(
                        Stage: "Reviewer",
                        Summary: $"Denetim Başarılı (JSON Patch): {appliedBlocks.Count} Blok Düzeltildi ({string.Join(", ", appliedBlocks)})",
                        Content: string.Join("\n", patchResult.Patches.Select(p => $"[{p.BlockId}]: {p.Reason}\n{p.FixedText}")),
                        Attempt: state.AttemptCount));
                }
            }
            else
            {
                // Sıfır yama: Taslak zaten kusursuz!
                _stepLogger?.Invoke(new PipelineStepLog(
                    Stage: "Reviewer",
                    Summary: "Denetim Başarılı: Kusursuz Taslak (Sıfır Düzeltme)",
                    Content: patchResult.Feedback ?? "Taslak yayın standardına tam uygun, ek düzeltme gerekmedi.",
                    Attempt: state.AttemptCount));
            }
        }
        else
        {
            // Fallback: Model JSON formatında dönmediyse güvenle taslağı koru veya eski tarz blok arayışına gir
            _stepLogger?.Invoke(new PipelineStepLog(
                Stage: "Reviewer",
                Summary: "Uyarı: Reviewer yanıtı JSON formatında değil, taslak doğrudan onaylandı",
                Content: rawResponse,
                Attempt: state.AttemptCount));
        }

        var approvedState = state with
        {
            CurrentTranslation = finalApprovedText,
            IsApproved = true,
            Feedback = null
        };

        await context.YieldOutputAsync(approvedState, cancellationToken);
        return approvedState;
    }

    private static ReviewerPatchResult? ParseReviewerResponse(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return null;

        var cleaned = rawResponse.Trim();

        // 1. Markdown code block cleaner (```json ... ```)
        var fenceMatch = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (fenceMatch.Success)
        {
            cleaned = fenceMatch.Groups[1].Value.Trim();
        }
        else
        {
            // Dış { ... } sınırlarını bul
            var startIdx = cleaned.IndexOf('{');
            var endIdx = cleaned.LastIndexOf('}');
            if (startIdx >= 0 && endIdx > startIdx)
            {
                cleaned = cleaned.Substring(startIdx, endIdx - startIdx + 1);
            }
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            return JsonSerializer.Deserialize<ReviewerPatchResult>(cleaned, options);
        }
        catch
        {
            return null;
        }
    }

    private static string ApplyPatches(string currentTranslation, List<BlockPatch> patches, out List<string> appliedBlockIds)
    {
        appliedBlockIds = new List<string>();
        if (patches == null || patches.Count == 0) return currentTranslation;

        var result = currentTranslation;
        foreach (var patch in patches)
        {
            if (string.IsNullOrWhiteSpace(patch.BlockId) || string.IsNullOrWhiteSpace(patch.FixedText))
                continue;

            // Normalize blockId ("025", "25", "BLOK_025")
            var idMatch = Regex.Match(patch.BlockId, @"\d+");
            if (!idMatch.Success) continue;
            var numericId = int.Parse(idMatch.Value);
            var paddedId = numericId.ToString("D3");

            // İçerikte gereksiz iç içe geçmiş [BLOK_xxx] etiketleri varsa temizle
            var content = patch.FixedText.Trim();
            var innerTagMatch = Regex.Match(content, @"\[BLOK_0*" + numericId + @"[^\]]*\]([\s\S]*?)\[/BLOK_0*" + numericId + @"\]", RegexOptions.IgnoreCase);
            if (innerTagMatch.Success)
            {
                content = innerTagMatch.Groups[1].Value.Trim();
            }

            // Orijinal metindeki hedef bloğu bul: [BLOK_025] ... [/BLOK_025]
            var blockPattern = @"\[BLOK_0*" + numericId + @"(?:\s*\|\s*TAG:\s*[^\]]+)?\]([\s\S]*?)\[/BLOK_0*" + numericId + @"\]";
            var match = Regex.Match(result, blockPattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var replacement = $"[BLOK_{paddedId}]\n{content}\n[/BLOK_{paddedId}]";
                result = result.Substring(0, match.Index) + replacement + result.Substring(match.Index + match.Length);
                appliedBlockIds.Add($"BLOK_{paddedId}");
            }
        }

        return result;
    }

    private static List<string> FindMissingBlocks(string original, string translation)
    {
        var originalBlocks = Regex.Matches(original, @"\[BLOK_0*(\d+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        if (originalBlocks.Count == 0) return [];

        var translationBlocks = Regex.Matches(translation, @"\[BLOK_0*(\d+)").Select(m => m.Groups[1].Value).ToHashSet();
        var missing = new List<string>();
        foreach (var block in originalBlocks)
        {
            if (!translationBlocks.Contains(block))
            {
                missing.Add($"[BLOK_{block}]");
            }
        }
        return missing;
    }

    private static List<string> FindMissingMasks(string original, string translation)
    {
        var missing = new List<string>();
        var matches = Regex.Matches(original, @"⟦PH_\d+⟧|\[\[(INDEX|CODE)_\d+\]\]");
        foreach (Match match in matches)
        {
            if (!translation.Contains(match.Value, StringComparison.Ordinal))
            {
                missing.Add(match.Value);
            }
        }
        return missing;
    }

    private static List<string> FindMissingTerms(IReadOnlyList<string> expectedTerms, string translation)
    {
        var missing = new List<string>();
        foreach (var term in expectedTerms)
        {
            if (!TurkishTermBoundaryMatcher.IsTermPresent(translation, term))
            {
                missing.Add(term);
            }
        }
        return missing;
    }
}
