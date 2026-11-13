using Glossator.AI.Models;
using Glossator.AI.Streaming;
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

        // 2. Üslup kılavuzu boşsa metin örneğinden analiz üret (UTF-16 ve kelime sınırına duyarlı güvenli kesim)
        var sampleText = SliceSafeSampleText(state.OriginalText, 2500);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, $"[ANALİZ EDİLECEK BÖLÜM METNİ]:\n{sampleText}")
        };

        var streamResult = await StreamingChatHelper.StreamResponseAsync(
            _chatClient,
            messages,
            stageName: "StyleAnalyst",
            attempt: 1,
            stepLogger: _stepLogger,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var styleGuide = !string.IsNullOrWhiteSpace(streamResult.Text)
            ? streamResult.Text.Trim()
            : "Genel teknik ve profesyonel O'Reilly mühendislik üslubu (doğrudan, rasyonel ve duru dil).";

        _stepLogger?.Invoke(new PipelineStepLog(
            Stage: "StyleAnalyst",
            Summary: "MAF İş Akışında Yazar Üslup & Ton Parmak İzi Çıkarıldı",
            Content: styleGuide));

        return state with
        {
            StyleGuide = styleGuide
        };
    }

    private static string SliceSafeSampleText(string text, int maxChars = 2500)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxChars)
        {
            return text;
        }

        var cutoff = maxChars;
        // Eğer cutoff surrogate pair ortasına denk geldiyse 1 karakter geri çek
        if (char.IsHighSurrogate(text[cutoff - 1]))
        {
            cutoff--;
        }

        // Kelime veya cümle ortasında bölmemek için geriye doğru son boşluk veya satır sonunu bul
        var lastWhitespace = text.LastIndexOfAny(['\n', '\r', ' ', '\t'], cutoff - 1, Math.Min(cutoff, 200));
        if (lastWhitespace > 1000)
        {
            cutoff = lastWhitespace;
        }

        return text[..cutoff].Trim();
    }
}
