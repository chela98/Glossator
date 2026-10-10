using System;
using System.Collections.Generic;
using System.Text;

namespace Glossator.Core.Models.Epub
{
    public class EpubItem
    {
        public required string Id { get; init; }
        public required string Href { get; init; }
        public required string MediaType { get; init; }
        public string? Properties { get; init; }

        /// <summary>
        /// Bu öğenin çevrilebilir bir HTML/XHTML metin belgesi olup olmadığını belirtir.
        /// </summary>
        public bool IsDocument =>
            MediaType == "application/xhtml+html" ||
            Href.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            Href.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Belgenin semantik rolü.
        /// </summary>
        public EpubDocumentRole Role { get; set; } = EpubDocumentRole.NonDocument;

        /// <summary>
        /// Belgenin tespit edilen veya TOC'tan okunan dostane başlığı.
        /// </summary>
        public string? Title { get; set; }

        public override string ToString() => $"{Id} -> {Href} ({Role}, {MediaType})";
    }
}
