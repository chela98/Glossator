using System.Diagnostics;

namespace Glossator.AI.Diagnostics;

public static class GlossatorDiagnostics
{
    public const string SourceName = "Glossator";
    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
}
