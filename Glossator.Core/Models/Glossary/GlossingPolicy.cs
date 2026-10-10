namespace Glossator.Core.Models.Glossary;

public enum GlossingPolicy
{
    /// <summary>
    /// Pedagojik Aşamalı Tanıtım: Ünite (Chapter) bazında ilk 2 geçişte hibrit "Türkçe (İngilizce)",
    /// 3. ve sonraki kullanımlarda ise YALNIZCA akıcı TÜRKÇE karşılığı kullanılır.
    /// </summary>
    ProgressiveGloss = 0,

    /// <summary>
    /// Dokunulmaz Sektör Jargonu: Markalar, SDK'ler, protokoller ve kısaltmalar (LLM, API, REST).
    /// Asla Türkçeleştirilmez, parantez açılmaz; orijinal İngilizce kalır.
    /// </summary>
    AlwaysEnglish = 1,

    /// <summary>
    /// Düz Teknik Kavramlar: Doğrudan yerleşik Türkçe karşılığı kullanılır (pipeline -> boru hattı).
    /// Asla İngilizce parantez eklenmez.
    /// </summary>
    AlwaysTurkish = 2
}
