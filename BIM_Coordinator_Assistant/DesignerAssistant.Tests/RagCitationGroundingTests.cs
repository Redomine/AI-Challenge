using DesignerAssistant.Web.Services;
using Xunit;

namespace DesignerAssistant.Tests;

public sealed class RagCitationGroundingTests
{
    private static readonly RagSource Source = new(
        "c703", "confluence_html", "Проекты 2022 / revit-703",
        "Проекты 2022 / revit-703", null,
        "Сервер: revit-703. Проект: STLB-OK1.", 0.9f);

    [Fact]
    public void WrongServerFailsEvenWhenProjectMatches()
    {
        var citation = new RagCitation("c703", Source.Source, Source.Section,
            null, Source.Text, true);
        Assert.True(RagCitationParser.AnswerSupportedByQuotes(
            "STLB-OK1 находится на revit-703", [citation]));
        Assert.False(RagCitationParser.AnswerSupportedByQuotes(
            "STLB-OK1 находится на revit-702", [citation]));
    }

    [Theory]
    [InlineData("pdf", "Проекты 2022 / revit-703", null)]
    [InlineData("confluence_html", "Проекты 2022 / revit-702", null)]
    [InlineData("confluence_html", "Проекты 2022 / revit-703", 2)]
    public void WrongMetadataInvalidatesLiteralQuote(string source, string section, int? page)
    {
        var parsed = new RagCitationParser.ParseResult("STLB-OK1: revit-703",
            [new RagCitation("c703", source, section, page, Source.Text, false)], true);
        var result = RagCitationParser.ValidateAgainstSources(parsed, [Source]);
        Assert.False(result.HasAnyVerifiedCitation);
    }

    [Fact]
    public void UnrelatedIdentifierFailsDespiteSharedProject()
    {
        var citation = new RagCitation("c703", Source.Source, Source.Section,
            null, Source.Text, true);
        Assert.False(RagCitationParser.AnswerSupportedByQuotes(
            "STLB-OK1 находится на revit-704", [citation]));
        Assert.False(RagCitationParser.AnswerSupportedByQuotes(
            "Не знаю", [citation]));
    }

    [Fact]
    public void StructuredQuoteUsesRetrievedSourceMetadata()
    {
        const string json = """{"answer":"STLB-OK1 находится на revit-703","quotes":[{"chunk_id":"c703","quote":"Сервер: revit-703. Проект: STLB-OK1."}]}""";
        var parsed = RagCitationParser.ValidateAgainstSources(
            RagCitationParser.ParseJson(json, [Source]), [Source]);

        Assert.True(parsed.HasAnyVerifiedCitation);
        Assert.Equal(Source.Section, parsed.Citations[0].Section);
        Assert.True(RagCitationParser.AnswerSupportedByQuotes(parsed.CleanAnswer, parsed.Citations));
    }

    [Fact]
    public void StructuredQuoteWithUnknownChunkIsRejected()
    {
        const string json = """{"answer":"STLB-OK1 находится на revit-703","quotes":[{"chunk_id":"missing","quote":"Сервер: revit-703. Проект: STLB-OK1."}]}""";
        var parsed = RagCitationParser.ValidateAgainstSources(
            RagCitationParser.ParseJson(json, [Source]), [Source]);

        Assert.False(parsed.HasAnyVerifiedCitation);
    }
}
