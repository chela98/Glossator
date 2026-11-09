using Glossator.AI.Executors;
using Glossator.AI.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Glossator.AI.Workflows;

/// <summary>
/// Microsoft Agent Framework (MAF) Workflows altyapısı üzerinde çalışan,
/// Translator ve Reviewer arasında çift yönlü durum makinesi (Cyclic Translation Graph) kuran
/// kurumsal iş akışı orkestratörü.
/// </summary>
public sealed class CyclicTranslationWorkflow
{
    private const int MaxReviewAttempts = 3;

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
        cancellationToken.ThrowIfCancellationRequested();

        var initialState = new TranslationReviewState(
            OriginalText: originalText,
            CurrentTranslation: string.Empty,
            DoNotTranslateTerms: doNotTranslateTerms,
            StyleGuide: styleGuide,
            GlossaryTerms: glossaryTerms,
            IsApproved: false,
            Feedback: null,
            AttemptCount: 1);

        try
        {
            var workflow = CreateWorkflow();
            Run run = await InProcessExecution.RunAsync(workflow, initialState, cancellationToken: cancellationToken);

            var finalState = run.OutgoingEvents
                .OfType<WorkflowOutputEvent>()
                .Select(e => e.Data)
                .OfType<TranslationReviewState>()
                .LastOrDefault();

            if (finalState != null && !string.IsNullOrWhiteSpace(finalState.CurrentTranslation))
            {
                return finalState.CurrentTranslation.Trim();
            }
        }
        catch (OperationCanceledException)
        {
            throw; // İptal sinyali derhal yukarı iletilir, yutulmaz
        }
        catch (Exception ex)
        {
            _stepLogger?.Invoke(new PipelineStepLog(
                Stage: "CyclicWorkflow",
                Summary: $"İş Akışı Hatası: {ex.Message}",
                Content: ex.ToString()));
        }

        return string.Empty;
    }
}
