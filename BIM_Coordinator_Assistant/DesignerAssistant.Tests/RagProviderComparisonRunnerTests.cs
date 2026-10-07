using DesignerAssistant.Web.Services;

namespace DesignerAssistant.Tests;

public sealed class RagProviderComparisonRunnerTests
{
    private static readonly AutoTestCase Positive = new(
        "Где инструкция?", "confluence", null, null, ["контур"],
        ForbiddenTerms: ["выдумка"]);

    [Fact]
    public void VerifiedLocalAnswerPassesQualityCheck()
    {
        var answer = Result("Проверьте контур.", sources: [Source()], citations: [Citation()]);

        Assert.Null(RagProviderComparisonRunner.EvaluateQuality(Positive, answer));
    }

    [Fact]
    public void MissingCitationFailsEvenWhenFactTermMatches()
    {
        var answer = Result("Проверьте контур.", sources: [Source()]);

        Assert.Contains("цитаты", RagProviderComparisonRunner.EvaluateQuality(Positive, answer));
    }

    [Fact]
    public void NegativeCaseRequiresAbstention()
    {
        var testCase = Positive with { ExpectedNoEvidence = true, ExpectedFactTerms = [] };

        Assert.NotNull(RagProviderComparisonRunner.EvaluateQuality(testCase, Result("Не найдено.")));
        Assert.Null(RagProviderComparisonRunner.EvaluateQuality(testCase,
            Result("Не знаю.") with { Abstained = true }));
    }

    private static RagQueryResult Result(string answer,
        IReadOnlyList<RagSource>? sources = null, IReadOnlyList<RagCitation>? citations = null) =>
        new("Где инструкция?", RagMode.Rag, answer, true, sources?.Count ?? 0,
            sources ?? [], null, Citations: citations);

    private static RagSource Source() => new("chunk-1", "confluence", "Инструкция", "Раздел", null,
        "Проверьте контур.", 0.9f);

    private static RagCitation Citation() => new("chunk-1", "confluence", "Раздел", null,
        "Проверьте контур.", true);
}
