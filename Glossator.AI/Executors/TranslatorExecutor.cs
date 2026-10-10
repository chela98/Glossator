using Glossator.AI.Models;
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
        var termsList = state.DoNotTranslateTerms.Count > 0
            ? string.Join(", ", state.DoNotTranslateTerms)
            : "Yok";

        var glossarySection = state.GlossaryTerms != null && state.GlossaryTerms.Count > 0
            ? string.Join("\n", state.GlossaryTerms.Select(kv => $"- {kv.Key} => {kv.Value}"))
            : "Yok";

        var feedbackSection = !string.IsNullOrWhiteSpace(state.Feedback)
            ? $"\n[ÖNCEKİ DENETİMDEKİ HATALAR - DÜZELTME EMRİ]:\n{state.Feedback}\nLütfen bu düzeltmeleri uygulayarak yeniden çevir!\n"
            : string.Empty;

        var userMessage = $"""
            [YAZAR ÜSLUP KILAVUZU]:
            {state.StyleGuide}

            [TERİM SÖZLÜĞÜ - ZORUNLU TÜRKÇE KARŞILIKLAR]:
            {glossarySection}
            (KURAL: Metinde geçen bu terimleri yukarıdaki resmi Türkçe karşılıklarıyla ve cümlenin akışına göre doğru Türkçe eklerle ('modellerin', 'veri setlerini', 'maliyetini') çevir. ASLA 'models ler', 'cost'unu', 'ML pratisyenleri' gibi bozuk Plaza Türkçesi üretme!)
            (AYNA İLKESİ KURALI: Yazar ne dediyse birebir o. Metne kendinden felsefi kavram (örneğin 'decision making' için 'irade') veya yapay drama ekleme!)

            [DOKUNULMAYACAK MARKA, PROTOKOL VE KISALTMALAR]:
            {termsList}
            (KURAL: Yukarıdaki marka, kütüphane, protokol ve kısaltmaları KESİNLİKLE Türkçeleştirme! Orijinal İngilizce halleriyle koru).
            {feedbackSection}
            [ÇEVRİLECEK METİN]:
            {state.OriginalText}
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, userMessage)
        };

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        var translatedText = response.Text?.Trim() ?? string.Empty;

        _stepLogger?.Invoke(new PipelineStepLog(
            Stage: "Translator",
            Summary: state.AttemptCount > 1 
                ? $"Düzeltilmiş Çeviri Taslağı Üretildi (Döngü Denemesi: {state.AttemptCount})" 
                : "İlk Çeviri Taslağı Üretildi",
            Content: translatedText,
            Attempt: state.AttemptCount));

        return state with
        {
            CurrentTranslation = translatedText
        };
    }
}
