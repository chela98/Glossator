using System.ClientModel;
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
    private readonly bool _enableSensitiveTelemetry;

    public CyclicTranslationWorkflow(
        IChatClient styleAnalystClient,
        IChatClient translatorClient,
        IChatClient reviewerClient,
        string stylePrompt,
        string translatorPrompt,
        string reviewerPrompt,
        Action<PipelineStepLog>? stepLogger = null,
        bool enableSensitiveTelemetry = false)
    {
        _styleAnalystClient = styleAnalystClient;
        _translatorClient = translatorClient;
        _reviewerClient = reviewerClient;
        _stylePrompt = stylePrompt;
        _translatorPrompt = translatorPrompt;
        _reviewerPrompt = reviewerPrompt;
        _stepLogger = stepLogger;
        _enableSensitiveTelemetry = enableSensitiveTelemetry;
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
        builder.WithOpenTelemetry(cfg => cfg.EnableSensitiveData = _enableSensitiveTelemetry);

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
            var run = await InProcessExecution.RunAsync(workflow, initialState, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await using var _runScope = run.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var workflowException = run.OutgoingEvents
                .OfType<WorkflowErrorEvent>()
                .Select(e => e.Exception)
                .FirstOrDefault(e => e != null)
                ?? run.OutgoingEvents
                    .OfType<ExecutorFailedEvent>()
                    .Select(e => e.Data)
                    .OfType<Exception>()
                    .FirstOrDefault();

            if (workflowException != null)
            {
                var unwrapped = UnwrapExecutorException(workflowException);

                if (unwrapped is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(unwrapped);
                }

                if (IsFatalConfigurationOrAuthException(unwrapped))
                {
                    _stepLogger?.Invoke(new PipelineStepLog(
                        Stage: "CyclicWorkflow",
                        Summary: $"Sağlayıcı Kimlik/Yapılandırma Hatası: {unwrapped.Message}",
                        Content: unwrapped.ToString()));
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(unwrapped);
                }

                _stepLogger?.Invoke(new PipelineStepLog(
                    Stage: "CyclicWorkflow",
                    Summary: $"İş Akışı Hatası: {unwrapped.Message}",
                    Content: unwrapped.ToString()));
            }

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // İptal sinyali derhal yukarı iletilir, yutulmaz
        }
        catch (Exception ex) when (IsFatalConfigurationOrAuthException(ex))
        {
            _stepLogger?.Invoke(new PipelineStepLog(
                Stage: "CyclicWorkflow",
                Summary: $"Sağlayıcı Kimlik/Yapılandırma Hatası: {ex.Message}",
                Content: ex.ToString()));
            throw; // 401/403/400 gibi ölümcül yetki/konfig hatalarını yedek fallback'te tekrar tekrar yakma
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

    private static Exception UnwrapExecutorException(Exception ex)
    {
        var current = ex;
        while (current is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
        {
            current = tie.InnerException;
        }
        return current;
    }

    private static bool IsFatalConfigurationOrAuthException(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is ClientResultException cre && cre.Status is 400 or 401 or 403 or 404 or 422)
            {
                return true;
            }

            if (current is HttpRequestException hre && hre.StatusCode.HasValue
                && (int)hre.StatusCode.Value is 400 or 401 or 403 or 404 or 422)
            {
                return true;
            }
        }

        return false;
    }
}
