using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Prompts;
using DesignerAssistant.Storage;

namespace DesignerAssistant.Diagnostics;

public sealed record MemoryTestStep(
    string Name,
    MemorySnapshot Memory,
    string Answer,
    bool Passed,
    string Expectation);

public static class MemoryLayersAutoTest
{
    public static async Task<IReadOnlyList<MemoryTestStep>> RunAsync(
        ILlmClient llmClient,
        AppOptions options,
        CancellationToken cancellationToken = default)
    {
        var agent = new DesignAssistantAgent(
            llmClient,
            new InMemoryChatHistoryStore(),
            new InMemoryMemoryStore(),
            DesignerAssistantPrompt.Text,
            options);
        await agent.InitializeAsync(cancellationToken);
        var results = new List<MemoryTestStep>();

        await agent.AskAsync(
            "Запомни только в рамках текущего диалога: тестовый код объекта ALPHA-17.",
            cancellationToken);
        var shortAnswer = await agent.AskAsync(
            "Какой тестовый код объекта я сообщил ранее? Ответь только кодом.",
            cancellationToken);
        var shortMemory = await agent.GetMemoryAsync(cancellationToken);
        results.Add(new MemoryTestStep(
            "Краткосрочная память",
            shortMemory,
            shortAnswer.ModelResponse.Content,
            shortMemory.ShortTerm.Count == 4 &&
            shortMemory.Working.Count == 0 &&
            shortMemory.LongTerm.Count == 0 &&
            Contains(shortAnswer.ModelResponse.Content, "ALPHA-17"),
            "Код ALPHA-17 берётся только из истории диалога; явные слои пусты."));

        await agent.RememberAsync(MemoryLayer.Working, "revit_version", "2022", cancellationToken);
        await agent.RememberAsync(MemoryLayer.Working, "test_model", "Coordination_Test.rvt", cancellationToken);
        await agent.RememberAsync(MemoryLayer.LongTerm, "report_format", "краткий маркированный список", cancellationToken);
        await agent.RememberAsync(MemoryLayer.LongTerm, "user_role", "BIM-координатор", cancellationToken);
        var layeredAnswer = await agent.AskAsync(
            "Назови версию Revit, тестовую модель и предпочтительный формат отчёта, известные тебе сейчас.",
            cancellationToken);
        var layeredMemory = await agent.GetMemoryAsync(cancellationToken);
        results.Add(new MemoryTestStep(
            "Рабочая и долговременная память",
            layeredMemory,
            layeredAnswer.ModelResponse.Content,
            layeredMemory.Working.Count == 2 &&
            layeredMemory.LongTerm.Count == 2 &&
            Contains(layeredAnswer.ModelResponse.Content, "2022") &&
            Contains(layeredAnswer.ModelResponse.Content, "Coordination_Test.rvt") &&
            Contains(layeredAnswer.ModelResponse.Content, "спис"),
            "Ответ объединяет данные текущей задачи и устойчивые предпочтения."));

        await agent.ClearHistoryAsync(cancellationToken);
        var afterSessionAnswer = await agent.AskAsync(
            "Какой тестовый код, версия Revit, тестовая модель и формат отчёта тебе сейчас известны? Если данных нет, так и напиши.",
            cancellationToken);
        var afterSessionMemory = await agent.GetMemoryAsync(cancellationToken);
        results.Add(new MemoryTestStep(
            "Новая сессия",
            afterSessionMemory,
            afterSessionAnswer.ModelResponse.Content,
            afterSessionMemory.ShortTerm.Count == 2 &&
            afterSessionMemory.Working.Count == 2 &&
            afterSessionMemory.LongTerm.Count == 2 &&
            !Contains(afterSessionAnswer.ModelResponse.Content, "ALPHA-17") &&
            Contains(afterSessionAnswer.ModelResponse.Content, "2022") &&
            Contains(afterSessionAnswer.ModelResponse.Content, "Coordination_Test.rvt"),
            "История с ALPHA-17 удалена, рабочая и долговременная память сохранились."));

        await agent.CompleteTaskAsync(cancellationToken);
        await agent.ClearHistoryAsync(cancellationToken);
        var afterTaskAnswer = await agent.AskAsync(
            "Выведи четыре строки с точными значениями: версия Revit, тестовая модель, роль пользователя и формат отчёта. Для отсутствующих данных напиши 'неизвестно'.",
            cancellationToken);
        var afterTaskMemory = await agent.GetMemoryAsync(cancellationToken);
        results.Add(new MemoryTestStep(
            "Завершение задачи",
            afterTaskMemory,
            afterTaskAnswer.ModelResponse.Content,
            afterTaskMemory.ShortTerm.Count == 2 &&
            afterTaskMemory.Working.Count == 0 &&
            afterTaskMemory.LongTerm.Count == 2 &&
            !Contains(afterTaskAnswer.ModelResponse.Content, "Coordination_Test.rvt") &&
            Contains(afterTaskAnswer.ModelResponse.Content, "BIM") &&
            Contains(afterTaskAnswer.ModelResponse.Content, "спис"),
            "Рабочие данные удалены, профиль и формат отчёта остались."));

        return results;
    }

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
