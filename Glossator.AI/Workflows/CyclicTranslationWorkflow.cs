using Glossator.AI.Executors;
using Glossator.AI.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Glossator.AI.Workflows;

public class CyclicTranslationWorkflow
{
    private readonly IChatClient _translatorClient;
    private readonly IChatClient _reviewerClient;
    private readonly string _translatorPrompt;
    private readonly string _reviewerPrompt;
    private readonly Action<PipelineStepLog>? _stepLogger;

    public CyclicTranslationWorkflow(
        IChatClient translatorClient,
        IChatClient reviewerClient,
        string translatorPrompt,
        string reviewerPrompt,
        Action<PipelineStepLog>? stepLogger = null)
    {
        _translatorClient = translatorClient;
        _reviewerClient = reviewerClient;
        _translatorPrompt = translatorPrompt;
        _reviewerPrompt = reviewerPrompt;
        _stepLogger = stepLogger;
    }

    private Workflow CreateWorkflow()
    {
        // Her çalışma için taze ve izole Executor örnekleri üretilir (Kanal & Durum İzolasyonu)
        var translator = new TranslatorExecutor(_translatorClient, _translatorPrompt, _stepLogger);
        var reviewer = new ReviewerExecutor(_reviewerClient, _reviewerPrompt, _stepLogger);

        // 1. Başlangıç Düğümü: TranslatorExecutor
        var builder = new WorkflowBuilder(translator);

        // 2. Kenar 1: Translator çıktısı Reviewer'a akar
        builder.AddEdge(translator, reviewer);

        // 3. Kenar 2 (DÖNGÜSEL KENAR): 
        // Eğer Reviewer onay vermezse (IsApproved == false) akış tekrar Translator'a döner (Max 3 deneme)
        builder.AddEdge<TranslationReviewState>(
            reviewer, 
            translator, 
            condition: state => state != null && !state.IsApproved && state.AttemptCount <= 3);

        // 4. Kenar 3 (ÇIKTI DÜĞÜMÜ):
        // Onaylandığında nihai çıktı Reviewer'dan alınır
        builder.WithOutputFrom(reviewer);

        // 5. OpenTelemetry Telemetrisi (Aspire Dashboard & OTLP uyumlu)
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

                // 1. Onaylanmış nihai çıktıyı ara
                var finalState = run.OutgoingEvents
                    .OfType<WorkflowOutputEvent>()
                    .Select(e => e.Data)
                    .OfType<TranslationReviewState>()
                    .LastOrDefault();

                if (finalState != null && !string.IsNullOrWhiteSpace(finalState.CurrentTranslation))
                {
                    return finalState.CurrentTranslation.Trim();
                }

                // 2. Güvenlik Ağı: Workflow sırasında üretilmiş son durum var mı?
                var anyState = run.OutgoingEvents
                    .Select(e => (e as dynamic)?.Data as TranslationReviewState)
                    .Where(s => s != null && !string.IsNullOrWhiteSpace(s.CurrentTranslation))
                    .LastOrDefault();

                if (anyState != null && !string.IsNullOrWhiteSpace(anyState.CurrentTranslation))
                {
                    return anyState.CurrentTranslation.Trim();
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
