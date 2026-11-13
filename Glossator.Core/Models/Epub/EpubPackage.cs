using System;
using System.Collections.Generic;
using System.Text;

namespace Glossator.Core.Models.Epub
{
    public class EpubPackage
    {
        public string Title { get; init; } = string.Empty;
        public string Language { get; init; } = "en";
        public string? Author { get; init; }
        public string OpfDirectory { get; init; } = string.Empty;

        /// <summary>
        /// EPUB paketinin kök dizini (META-INF ve mimetype barındıran üst dizin).
        /// </summary>
        public string RootDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(OpfDirectory)) return string.Empty;

                // 1. OpfDirectory içinde META-INF var mı?
                if (Directory.Exists(Path.Combine(OpfDirectory, "META-INF")))
                    return OpfDirectory;

                // 2. OpfDirectory'nin üst klasöründe META-INF var mı?
                var parent = Directory.GetParent(OpfDirectory)?.FullName;
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(Path.Combine(parent, "META-INF")))
                    return parent;

                return OpfDirectory;
            }
        }

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

        /// <summary>
        /// Kitabın ana okuma gövdesini oluşturan bölümleri
        /// </summary>
        public List<EpubItem> Chapters =>
            Spine.Where(item => item.Role == EpubDocumentRole.Chapter).ToList();

        /// <summary>
        /// Önsöz, sözlük, yazar biyografisi vb. destekleyici ek sayfalar
        /// </summary>
        public List<EpubItem> AuxiliaryItems =>
            Spine.Where(item => item.Role == EpubDocumentRole.Auxiliary).ToList();

        /// <summary>
        /// Kitapta bir sözlük dosyası varsa otomatik tespiti
        /// </summary>
        public EpubItem? GlossaryItem =>
            Spine.FirstOrDefault(item =>
                item.Id.Contains("glossary", StringComparison.OrdinalIgnoreCase) ||
                item.Href.Contains("glossary", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Kitapta bir dizin dosyası varsa otomatik tespit eder 
        /// </summary>
        public EpubItem? IndexItem =>
            Spine.FirstOrDefault(item =>
                item.Role == EpubDocumentRole.Index ||
                item.Id.Contains("index", StringComparison.OrdinalIgnoreCase) ||
                item.Href.Contains("index", StringComparison.OrdinalIgnoreCase) ||
                item.Href.StartsWith("ix", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// İçindekiler (TOC / Navigasyon) belgesi
        /// </summary>
        public EpubItem? NavigationItem =>
            Manifest.Values.FirstOrDefault(item => item.Role == EpubDocumentRole.Navigation);

        /// <summary>
        /// Kitabın OPF manifestine göre belirlenmiş kapak görselinin göreceli yolu (varsa).
        /// </summary>
        public string? CoverImageHref { get; init; }

        /// <summary>
        /// Kapak görselinin diskteki güvenli tam dosya yolunu döndürür (dosya yoksa veya geçersizse null).
        /// </summary>
        public string? ResolveCoverImageFullPath()
        {
            if (string.IsNullOrWhiteSpace(CoverImageHref) || string.IsNullOrWhiteSpace(OpfDirectory))
                return null;

            var allowedRoot = string.IsNullOrWhiteSpace(RootDirectory) ? OpfDirectory : RootDirectory;
            var normalizedRoot = Path.GetFullPath(allowedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            var candidate = Path.GetFullPath(Path.Combine(
                OpfDirectory,
                CoverImageHref.Replace('/', Path.DirectorySeparatorChar)));

            if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            return File.Exists(candidate) ? candidate : null;
        }
    }
}
