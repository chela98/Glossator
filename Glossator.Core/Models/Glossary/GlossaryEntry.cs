using System;
using System.Collections.Generic;
using System.Text;

namespace Glossator.Core.Models.Glossary
{
    public class GlossaryEntry
    {
        public int Id { get; set; }
        public required string EnglishTerm {  get; init; }
        public string? TurkishTerm { get; set; }
        public string? EnglishDefinition {  get; init; }
        public GlossingPolicy Policy { get; set; } = GlossingPolicy.ProgressiveGloss;
        /// <summary>
        /// Bu terim kullanıcı tarafından incelenip onaylandı mı?
        /// </summary>
        public bool IsReviewed { get; set; } = false;
        /// <summary>
        /// Bu terimin ilk kez yakalanadığı kitap/kaynak adı
        /// </summary>
        public string? SourceBook { get; set; }

        /// <summary>
        /// Arama ve Karşılaştırmalarda kullanılcak küçük harfli anahtar.
        /// </summary>
        public string NormalizedKey => EnglishTerm.Trim().ToLowerInvariant();

        public override string ToString() =>
            string.IsNullOrEmpty(TurkishTerm)
                ? EnglishTerm
                : $"{EnglishTerm} ({TurkishTerm})";
        
    }
}
