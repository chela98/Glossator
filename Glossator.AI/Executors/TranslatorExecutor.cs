using Glossator.AI.Models;
using Glossator.AI.Streaming;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Glossator.AI.Executors;

public sealed class TranslatorExecutor : Executor<TranslationReviewState, TranslationReviewState>
{
    private readonly IChatClient _chatClient;
    private readonly string _systemPrompt;
    private readonly Action<PipelineStepLog>? _stepLogger;

    public TranslatorExecutor(
        IChatClient chatClient, 
        string systemPrompt, 
        Action<PipelineStepLog>? stepLogger = null) 
        : base("TranslatorExecutor")
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
        using var activity = Glossator.AI.Diagnostics.GlossatorDiagnostics.ActivitySource.StartActivity("TranslatorAgent");
        activity?.SetTag("agent.attempt", state.AttemptCount);
        activity?.SetTag("input.text_length", state.OriginalText.Length);
        var termsList = state.DoNotTranslateTerms.Count > 0
            ? string.Join(", ", state.DoNotTranslateTerms)
            : "Yok";

        var glossarySection = state.GlossaryTerms != null && state.GlossaryTerms.Count > 0
            ? string.Join("\n", state.GlossaryTerms.Select(kv => $"- {kv.Key} => {kv.Value}"))
            : "Yok";

        var styleSection = !string.IsNullOrWhiteSpace(state.StyleGuide)
            ? state.StyleGuide
            : "Genel O'Reilly teknik mentor mühendis tonu.";

        var feedbackSection = !string.IsNullOrWhiteSpace(state.Feedback)
            ? $"\n[ÖNCEKİ DENETİMDEKİ HATALAR - DÜZELTME EMRİ]:\n{state.Feedback}\nLütfen bu düzeltmeleri uygulayarak yeniden çevir!\n"
            : string.Empty;

        // KV CACHE HİYERARŞİSİ (Prefix Caching Optimizasyonu):
        // 1. Bölüm düzeyinde sabit bağlam (Üslup Kılavuzu) EN BAŞTA
        // 2. Terim sözlüğü ve dokunulmazlar listesi ORTADA
        // 3. Varsa düzeltme emri ve asıl dinamik yük ([ÇEVRİLECEK METİN]) EN SONDA
        var userMessage = $"""
            [YAZAR ÜSLUP KILAVUZU]:
            {styleSection}

            [TERİM SÖZLÜĞÜ - ZORUNLU TÜRKÇE KARŞILIKLAR]:
            {glossarySection}

            [DOKUNULMAYACAK MARKA, PROTOKOL VE KISALTMALAR]:
            {termsList}
            {feedbackSection}
            [ÇEVRİLECEK METİN]:
            {state.OriginalText}
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, userMessage)
        };

        var streamResult = await StreamingChatHelper.StreamResponseAsync(
            _chatClient,
            messages,
            stageName: "Translator",
            attempt: state.AttemptCount,
            activity: activity,
            stepLogger: _stepLogger,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var translatedText = streamResult.Text.Trim();

        _stepLogger?.Invoke(new PipelineStepLog(
            Stage: "Translator",
            Summary: state.AttemptCount > 1 
                ? $"Düzeltilmiş Çeviri Taslağı Üretildi ({streamResult.FormatSummary(state.AttemptCount)})" 
                : $"İlk Çeviri Taslağı Üretildi ({streamResult.FormatSummary(state.AttemptCount)})",
            Content: translatedText,
            Attempt: state.AttemptCount));

        return state with
        {
            CurrentTranslation = translatedText
        };
    }
}
