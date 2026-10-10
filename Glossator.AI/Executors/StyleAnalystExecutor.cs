using Glossator.AI.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Glossator.AI.Executors;

public sealed class StyleAnalystExecutor : Executor<TranslationReviewState, TranslationReviewState>
{
    private readonly IChatClient _chatClient;
    private readonly string _systemPrompt;
    private readonly Action<PipelineStepLog>? _stepLogger;

    public StyleAnalystExecutor(
        IChatClient chatClient, 
        string systemPrompt, 
        Action<PipelineStepLog>? stepLogger = null) 
        : base("StyleAnalystExecutor")
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
        // 1. Eğer StyleGuide zaten hafızada varsa (bölüm başında 1 kez üretilmişse),
        // tekrar token harcamadan pas geç ve doğrudan Translator'a ilet.
        if (!string.IsNullOrWhiteSpace(state.StyleGuide))
        {
            return state;
        }

        // 2. Eğer StyleGuide henüz yoksa, metni baştan sona analiz edip üslup ve ton kimliğini çıkar
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, $"[ANALİZ EDİLECEK BÖLÜM METNİ]:\n{state.OriginalText}")
        };

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        var analyzedStyle = response.Text?.Trim() ?? string.Empty;

        _stepLogger?.Invoke(new PipelineStepLog(
            Stage: "StyleAnalyst",
            Summary: "Yazarın Üslup ve Ton Analizi Çıkarıldı",
            Content: analyzedStyle));

        return state with
        {
            StyleGuide = analyzedStyle
        };
    }
}
