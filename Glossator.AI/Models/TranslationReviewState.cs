namespace Glossator.AI.Models;

public record TranslationReviewState(
    string OriginalText,
    string CurrentTranslation,
    IReadOnlyList<string> DoNotTranslateTerms,
    string StyleGuide,
    IReadOnlyDictionary<string, string>? GlossaryTerms = null,
    bool IsApproved = false,
    string? Feedback = null,
    int AttemptCount = 1
);
