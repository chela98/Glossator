using System.Xml;
using System.Xml.Linq;

namespace Glossator.Core.Security;

/// <summary>
/// Güvenilmeyen XML dosyalarını (container.xml, OPF, NCX) XXE, DTD varlık genişlemesi (Billion Laughs)
/// ve aşırı bellek/DoS saldırılarına karşı <see cref="XmlReaderSettings"/> sınırlarıyla güvenli yükler.
/// </summary>
public static class SafeXmlLoader
{
    public const long DefaultMaxCharactersInDocument = 10_000_000;
    public const long DefaultMaxCharactersFromEntities = 10_000;

    public static XDocument Load(
        string xmlFilePath,
        bool allowDtdIgnore = false,
        long maxCharactersInDocument = DefaultMaxCharactersInDocument,
        long maxCharactersFromEntities = DefaultMaxCharactersFromEntities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlFilePath);

        var settings = CreateSettings(allowDtdIgnore, maxCharactersInDocument, maxCharactersFromEntities);
        using var reader = XmlReader.Create(xmlFilePath, settings);
        return XDocument.Load(reader);
    }

    public static XmlReaderSettings CreateSettings(
        bool allowDtdIgnore = false,
        long maxCharactersInDocument = DefaultMaxCharactersInDocument,
        long maxCharactersFromEntities = DefaultMaxCharactersFromEntities)
    {
        return new XmlReaderSettings
        {
            DtdProcessing = allowDtdIgnore ? DtdProcessing.Ignore : DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharactersInDocument,
            MaxCharactersFromEntities = maxCharactersFromEntities
        };
    }
}
