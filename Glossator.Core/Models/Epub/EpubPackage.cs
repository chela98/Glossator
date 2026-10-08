using System;
using System.Collections.Generic;
using System.Text;

namespace Glossator.Core.Models.Epub
{
    public class EpubPackage
    {
        public string Title { get; init; }
        public string Language { get; init; } = "en";
        public string? Author { get; init; }
        public string OpfDirectory { get; init; } = string.Empty;

        ///<summary>
        /// Eğer paket doğrudan bir .epub dosyasından açıldıysa orjinal dosya yolu.
        /// </summary>
        public string? SourceArchivePath { get; init; }

        ///<summary>
        /// Kitaptaki tüm dosyalar (Id->EpubItem)
        /// </summary>
        public Dictionary<string, EpubItem> Manifest { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        ///<summary>
        /// Kitabın yazar tarafından belirlenmiş okuma sırası
        ///</summary>
        public List<EpubItem> Spine { get; init; } = [];

        ///<summary>
        /// Kitapta bir sözlük dosyası varsa otomatik tespiti
        /// </summary>
        public EpubItem? GlossaryItem =>
            Spine.FirstOrDefault(item =>
                item.Id.Contains("glossary", StringComparison.OrdinalIgnoreCase) ||
                item.Href.Contains("glossary", StringComparison.OrdinalIgnoreCase));

        ///<summary>
        /// Kitapta bir dizin dosyası varsa otomatik tespit eder 
        /// </summary>
        public EpubItem? IndexItem =>
            Spine.FirstOrDefault(item =>
                item.Id.Contains("index", StringComparison.OrdinalIgnoreCase) ||
                item.Href.Contains("index", StringComparison.OrdinalIgnoreCase) ||
                item.Href.StartsWith("ix", StringComparison.OrdinalIgnoreCase));
    }
}
