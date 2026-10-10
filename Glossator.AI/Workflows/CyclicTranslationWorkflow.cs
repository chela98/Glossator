using Glossator.AI.Executors;
using Glossator.AI.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Glossator.AI.Workflows;

/// <summary>
/// Microsoft Agent Framework (MAF) durum makinesi tabanlı döngüsel çeviri iş akışı.
/// StyleAnalyst -> Translator -> Reviewer zincirini kurar ve AddSwitch dallanmasıyla
/// redaksiyon reddedildiğinde otomatik olarak Translator'a geri yönlendirir.
/// </summary>
public sealed class CyclicTranslationWorkflow
{
    public const int MaxReviewAttempts = 3;

    private readonly IChatClient _styleAnalystClient;
    private readonly IChatClient _translatorClient;
    private readonly IChatClient _reviewerClient;
    private readonly string _stylePrompt;
    private readonly string _translatorPrompt;
    private readonly string _reviewerPrompt;
    private readonly Action<PipelineStepLog>? _stepLogger;

    public CyclicTranslationWorkflow(
        IChatClient styleAnalystClient,
        IChatClient translatorClient,
        IChatClient reviewerClient,
        string stylePrompt,
        string translatorPrompt,
        string reviewerPrompt,
        Action<PipelineStepLog>? stepLogger = null)
    {
        _styleAnalystClient = styleAnalystClient;
        _translatorClient = translatorClient;
        _reviewerClient = reviewerClient;
        _stylePrompt = stylePrompt;
        _translatorPrompt = translatorPrompt;
        _reviewerPrompt = reviewerPrompt;
        _stepLogger = stepLogger;
    }

    public CyclicTranslationWorkflow(
        IChatClient translatorClient,
        IChatClient reviewerClient,
        string translatorPrompt,
        string reviewerPrompt,
        Action<PipelineStepLog>? stepLogger = null)
        : this(translatorClient, translatorClient, reviewerClient, string.Empty, translatorPrompt, reviewerPrompt, stepLogger)
    {
    }

    private Workflow CreateWorkflow()
    {
        var styleAnalyst = new StyleAnalystExecutor(_styleAnalystClient, _stylePrompt, _stepLogger);
        var translator = new TranslatorExecutor(_translatorClient, _translatorPrompt, _stepLogger);
        var reviewer = new ReviewerExecutor(_reviewerClient, _reviewerPrompt, _stepLogger);

        // 1. Başlangıç: StyleAnalyst (eğer üslup kılavuzu zaten doluysa 0 ms'de translator'a devreder)
        var builder = new WorkflowBuilder(styleAnalyst);
        builder.AddEdge(styleAnalyst, translator);
        builder.AddEdge(translator, reviewer);

        // 2. MAF Resmi AddSwitch Durum Makinesi Dallanması
        builder.AddSwitch(reviewer, sw =>
        {
            sw.AddCase<TranslationReviewState>(
                state => state is { IsApproved: false } && state.AttemptCount <= MaxReviewAttempts,
                translator);
        });

        // 3. Onaylandığında veya deneme tükendiğinde çıktı Reviewer'dan alınır
        builder.WithOutputFrom(reviewer);
        builder.WithOpenTelemetry(cfg => cfg.EnableSensitiveData = true);

        return builder.Build();
    }

    public async Task<string> ExecuteAsync(
        string originalText,
        IReadOnlyList<string> doNotTranslateTerms,
        IReadOnlyDictionary<string, string>? glossaryTerms = null,
        string styleGuide = "",
        CancellationToken cancellationToken = default)
    {
        var initialState = new TranslationReviewState(
            OriginalText: originalText,
            CurrentTranslation: string.Empty,
            DoNotTranslateTerms: doNotTranslateTerms,
            StyleGuide: styleGuide,
            GlossaryTerms: glossaryTerms,
            IsApproved: false,
            Feedback: null,
            AttemptCount: 1);

        for (int retry = 1; retry <= 2; retry++)
        {
            try
            {
                var workflow = CreateWorkflow();
                Run run = await InProcessExecution.RunAsync(workflow, initialState, cancellationToken: cancellationToken);

                // %100 Tip Güvenli WorkflowOutputEvent okuma (dynamic reflection hack kaldırıldı!)
                var finalState = run.OutgoingEvents
                    .OfType<WorkflowOutputEvent>()
                    .Select(e => e.Data)
                    .OfType<TranslationReviewState>()
                    .LastOrDefault();

                if (finalState != null && !string.IsNullOrWhiteSpace(finalState.CurrentTranslation))
                {
                    return finalState.CurrentTranslation.Trim();
                }

                break;
            }
            catch (Exception ex)
            {
                if (retry == 2)
                {
                    _stepLogger?.Invoke(new PipelineStepLog(
                        Stage: "CyclicWorkflow",
                        Summary: $"İş Akışı Hatası: {ex.Message}",
                        Content: ex.ToString()));
                    break;
                }
                await Task.Delay(1500, cancellationToken);
            }
        }

        return string.Empty;
    }
}
