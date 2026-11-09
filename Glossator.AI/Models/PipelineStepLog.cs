namespace Glossator.AI.Models;

public record PipelineStepLog(
    string Stage, 
    string Summary, 
    string? Content = null, 
    int Attempt = 1,
    bool IsTransient = false);
