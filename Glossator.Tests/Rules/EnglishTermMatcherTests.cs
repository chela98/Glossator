using Glossator.Core.Rules;
using Xunit;

namespace Glossator.Tests.Rules;

public class EnglishTermMatcherTests
{
    [Theory]
    // 1. Tireli/alt çizgili kelimelerde yanlış akronim eşleşmesi olmamalı
    [InlineData("Supports encryption-at-rest for databases.", "REST", false)]
    [InlineData("Supports encryption_at_rest for databases.", "REST", false)]

    // 2. Büyük Harfli Akronimler Doğru Bağlamda Eşleşmeli
    [InlineData("We provide a REST API for our services.", "REST", true)]
    [InlineData("We provide a REST API for our services.", "API", true)]

    // 3. Küçük Harfli Sıradan Sözcükler Akronimleri Tetiklememeli
    [InlineData("You should take a rest after working.", "REST", false)]
    [InlineData("The rest of the chapter covers this.", "REST", false)]

    // 4. Genel Terimler Büyük/Küçük Harf Fark Etmeksizin Eşleşmeli
    [InlineData("Building Autonomous Agents is challenging.", "autonomous agent", false)]
    [InlineData("Building autonomous agent architectures.", "autonomous agent", true)]
    [InlineData("Building Autonomous Agents is challenging.", "Autonomous Agents", true)]
    [InlineData("Building autonomous agents is challenging.", "Autonomous Agents", true)]

    // 5. Alt Kelime ve Unicode Türkçe Harf Sınır Koruması
    [InlineData("Using a chemical reagent in the lab.", "agent", false)]
    [InlineData("The advertising agency was hired.", "agent", false)]
    [InlineData("Bu sistemdeki agentçağrı mekanizması bozuk.", "agent", false)]
    [InlineData("Çoklu-agent yaklaşımı burada incelenir.", "agent", false)]
    [InlineData("The intelligent agent acted autonomously.", "agent", true)]
    [InlineData("Bir agent, görevini otonom yürütür.", "agent", true)]

    // 6. Sembol İçeren Özel İsimler (C#, C++, .NET) ve Sınır Koruması
    [InlineData("Written in C# 14 on .NET 10.", "C#", true)]
    [InlineData("Avoid calling C#abc or similar identifiers.", "C#", false)]
    [InlineData("Written in C++ 23 natively.", "C++", true)]
    [InlineData("Using C++builder IDE tool.", "C++", false)]
    [InlineData("Developing modern applications in .NET 10.", ".NET", true)]
    [InlineData("Visit our website at asp.NET for details.", ".NET", false)]

    // 7. Kısa Sözcükler ve Akronimler ("Go", "IT")
    [InlineData("We write backend services in Go on Kubernetes.", "Go", true)]
    [InlineData("Please go to the next chapter.", "Go", false)]
    [InlineData("Working in the enterprise IT department.", "IT", true)]
    [InlineData("it is important to remember.", "IT", false)]

    // 8. Tireli Bileşik Terimler
    [InlineData("Using state-of-the-art foundation models.", "state-of-the-art", true)]
    [InlineData("A few-shot prompt was supplied.", "few-shot", true)]

    // 9. Marka ve Framework İsimleri
    [InlineData("Tracing via OpenTelemetry is enabled.", "OpenTelemetry", true)]
    [InlineData("tracing via opentelemetry is enabled.", "OpenTelemetry", true)]
    public void IsTermPresent_EvaluatesAccuratelyWithoutFalsePositives(string text, string term, bool expected)
    {
        // Act
        var result = EnglishTermMatcher.IsTermPresent(text, term);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, "term")]
    [InlineData("", "term")]
    [InlineData("   ", "term")]
    [InlineData("Some text", null)]
    [InlineData("Some text", "")]
    [InlineData("Some text", "   ")]
    public void IsTermPresent_WithNullOrEmpty_ReturnsFalse(string? text, string? term)
    {
        // Act
        var result = EnglishTermMatcher.IsTermPresent(text!, term!);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildMatcher_WithEmptyOrWhitespaceTerm_ReturnsNonMatchingRegex(string term)
    {
        // Act
        var regex = EnglishTermMatcher.BuildMatcher(term);

        // Assert
        Assert.DoesNotMatch(regex, string.Empty);
        Assert.DoesNotMatch(regex, "anything here");
        Assert.DoesNotMatch(regex, "REST API");
    }

    [Fact]
    public void BuildMatcher_WhenCalledRepeatedlyWithSameTerm_ReturnsCachedRegexInstance()
    {
        // Act
        var first = EnglishTermMatcher.BuildMatcher("Vector Database");
        var second = EnglishTermMatcher.BuildMatcher("Vector Database");

        // Assert
        Assert.Same(first, second);
    }
}
