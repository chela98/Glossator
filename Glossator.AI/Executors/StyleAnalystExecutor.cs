using Glossator.AI.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Glossator.AI.Executors;

/// <summary>
/// Yazarın üslup ve ton parmak izini çıkaran veya mevcut önbelleği sonraki düğümlere
/// aktaran resmi MAF durum makinesi düğümü (Executor).
/// </summary>
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
        // 1. Durumda zaten aktif bir üslup kılavuzu varsa doğrudan sonraki düğüme (Translator) aktar
        if (!string.IsNullOrWhiteSpace(state.StyleGuide))
        {
            return state;
        }

        // 2. Üslup kılavuzu boşsa metin örneğinden analiz üret
        var sampleText = state.OriginalText;
        if (sampleText.Length > 2500)
        {
            sampleText = sampleText[..2500];
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, $"[ANALİZ EDİLECEK BÖLÜM METNİ]:\n{sampleText}")
        };

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        var styleGuide = response.Text?.Trim() ?? string.Empty;

        _stepLogger?.Invoke(new PipelineStepLog(
            Stage: "StyleAnalyst",
            Summary: "MAF İş Akışında Yazar Üslup & Ton Parmak İzi Çıkarıldı",
            Content: styleGuide));

        return state with
        {
            StyleGuide = styleGuide
        };
    }
}
