namespace Glossator.Tests.Rules;

using Glossator.Core.Rules;
using Xunit;

public class TurkishTermBoundaryMatcherTests
{
    [Theory]
    [InlineData("Bu sistemde Agent doğrudan çalışır.", "Agent", true)]
    [InlineData("Otonom Agent'a yeni bir görev verildi.", "Agent", true)]
    [InlineData("Kullanıcı Agent’ın çıktısını onayladı.", "Agent", true)] // Tipografik kesme ’
    [InlineData("Büyük ölçekli Agentlar koordinasyon kurar.", "Agent", true)] // Kesmesiz çoğul eki
    [InlineData("Bu kimyasal bir reagent molekülüdür.", "Agent", false)] // Ön ek / sahte kelime
    [InlineData("Bu reklam agency şirketine ait.", "Agent", false)] // Son ek / sahte kelime
    [InlineData("Bu dökümanda C# dili kullanılmıştır.", "C#", true)]
    [InlineData("C#'ın getirdiği yenilikler harika.", "C#", true)]
    [InlineData("Proje Microsoft Agent Framework ile geliştirildi.", "Microsoft Agent Framework", true)]
    public void IsTermPresent_WithVariousTurkishMorphologyAndTypographicApostrophes_MatchesAccurately(
        string text, string term, bool expected)
    {
        var result = TurkishTermBoundaryMatcher.IsTermPresent(text, term);
        Assert.Equal(expected, result);
    }
}
