namespace Glossator.Core.Models.Epub;

/// <summary>
/// EPUB belgesinin semantik rolünü belirtir.
/// Dosya isimlerine bağımlı kalmadan evrensel sınıflandırma sağlar.
/// </summary>
public enum EpubDocumentRole
{
    /// <summary>
    /// Kitabın kapak sayfası veya kapak resmi.
    /// </summary>
    Cover,

    /// <summary>
    /// İçindekiler tablosu (EPUB 3 HTML5 Navigation veya EPUB 2 NCX).
    /// </summary>
    Navigation,

    /// <summary>
    /// Alfabetik dizin / fihrist (A-Z). LLM çevirisinden muaf tutulur.
    /// </summary>
    Index,

    /// <summary>
    /// Kitabın ana okuma akışını oluşturan gövde bölümleri (bölümler, parçalar, dersler).
    /// </summary>
    Chapter,

    /// <summary>
    /// Destekleyici ve ek belgeler (Önsöz, sözlük, yazar biyografisi, sonsöz, ithaf, teşekkür).
    /// </summary>
    Auxiliary,

    /// <summary>
    /// Metin belgesi olmayan kaynaklar (CSS, görsel, font, ses vb.).
    /// </summary>
    NonDocument
}
