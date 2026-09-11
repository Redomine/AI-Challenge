using DesignerAssistant.Llm;
using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public sealed class DesignAssistantAgent : IDesignAssistantAgent
{
    private readonly ILlmClient _llmClient;
    private readonly string _instructions;
    private readonly List<ChatMessage> _history = [];

    public DesignAssistantAgent(ILlmClient llmClient, string instructions)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _instructions = string.IsNullOrWhiteSpace(instructions)
            ? throw new ArgumentException("Системная инструкция не задана.", nameof(instructions))
            : instructions;
    }

    public async Task<string> AskAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException("Сообщение не должно быть пустым.", nameof(userMessage));
        }

        var pendingHistory = new List<ChatMessage>(_history)
        {
            new("user", userMessage.Trim())
        };

        var answer = await _llmClient.GenerateAsync(
            _instructions,
            pendingHistory,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new InvalidOperationException("Модель вернула пустой ответ.");
        }

        _history.Add(pendingHistory[^1]);
        _history.Add(new ChatMessage("assistant", answer));

        return answer;
    }

    public void ClearHistory() => _history.Clear();
}
