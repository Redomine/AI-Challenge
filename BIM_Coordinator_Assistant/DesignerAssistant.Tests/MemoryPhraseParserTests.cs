using DesignerAssistant.Memory;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tests;

public sealed class MemoryPhraseParserTests
{
    [Theory]
    [InlineData("Запомни на будущее, что отчёт нужен списком")]
    [InlineData("Запомни долговременно: я BIM-координатор")]
    [InlineData("Запомни навсегда мой формат отчёта")]
    [InlineData("Всегда помни, что транзакции требуют подтверждения")]
    [InlineData("Учитывай в дальнейшем совместимость с Revit 2022")]
    public void LongTermCatchPhrasesSelectLongTermLayer(string input)
    {
        var result = MemoryPhraseParser.Parse(input);
        Assert.NotNull(result);
        Assert.Equal(MemoryLayer.LongTerm, result.Layer);
        Assert.False(string.IsNullOrWhiteSpace(result.Value));
    }

    [Theory]
    [InlineData("Запомни для этой задачи модель Test.rvt")]
    [InlineData("Сохрани для текущей задачи: версия Revit 2024")]
    [InlineData("Запомни в рабочую память ElementId 12345")]
    [InlineData("Для этого проекта запомни код K4")]
    [InlineData("До завершения задачи запомни текущую гипотезу")]
    public void WorkingCatchPhrasesSelectWorkingLayer(string input)
    {
        var result = MemoryPhraseParser.Parse(input);
        Assert.NotNull(result);
        Assert.Equal(MemoryLayer.Working, result.Layer);
    }

    [Fact]
    public void AmbiguousRememberRequiresExplicitLayerChoice()
    {
        var result = MemoryPhraseParser.Parse("Запомни, что модель называется Test.rvt");
        Assert.NotNull(result);
        Assert.True(result.NeedsLayerChoice);
        Assert.Equal("модель называется Test.rvt", result.Value);
    }

    [Fact]
    public void RecallQuestionIsNotTreatedAsWriteIntent()
    {
        Assert.Null(MemoryPhraseParser.Parse("Что у нас зафиксировано для этого проекта?"));
    }
}
