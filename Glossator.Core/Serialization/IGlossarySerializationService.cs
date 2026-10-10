namespace Glossator.Core.Serialization;

using Glossator.Core.Models.Glossary;

public interface IGlossarySerializationService
{
    Task ExportAsync(IEnumerable<GlossaryEntry> terms, string filePath, SerializationFormat format, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GlossaryEntry>> ImportAsync(string filePath, CancellationToken cancellationToken = default);
}
