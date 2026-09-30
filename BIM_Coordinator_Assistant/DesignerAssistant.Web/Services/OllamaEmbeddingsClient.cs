using System.Net.Http.Json;
using System.Text.Json;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Минимальный клиент Ollama для эмбеддингов. Использует только
/// встроенный HttpClient, без сторонних пакетов. Повторяет контракт
/// Python-клиента из пакета docindexing: запрос
/// <c>POST /api/embed</c>, один или несколько текстов, одинаковая
/// размерность. Никаких фиктивных эмбеддингов при ошибке.
/// </summary>
public sealed class OllamaEmbeddingsClient
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;

    public OllamaEmbeddingsClient(HttpClient http, TimeSpan timeout, int maxRetries)
    {
        _http = http;
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : timeout;
        _maxRetries = Math.Max(0, maxRetries);
    }

    public async Task<float[]> EmbedAsync(string text, string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Текст для эмбеддинга пуст.", nameof(text));
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Модель эмбеддингов не задана.", nameof(model));
        }

        var payload = new { model, input = new[] { text } };
        Exception? last = null;
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeout);
                using var response = await _http.PostAsJsonAsync(
                    "/api/embed", payload, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cts.Token);
                    // Не ретраим 4xx — это ошибка запроса/модели.
                    if ((int)response.StatusCode >= 500)
                    {
                        last = new HttpRequestException(
                            $"Ollama HTTP {(int)response.StatusCode}: {body}");
                        await DelayAsync(attempt, cancellationToken);
                        continue;
                    }
                    throw new InvalidOperationException(
                        $"Ollama HTTP {(int)response.StatusCode}: {body}");
                }
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                if (!doc.RootElement.TryGetProperty("embeddings", out var arr) ||
                    arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException(
                        "Ollama не вернула поле embeddings.");
                }
                var first = arr[0];
                var vec = new float[first.GetArrayLength()];
                var i = 0;
                foreach (var v in first.EnumerateArray())
                {
                    vec[i++] = (float)v.GetDouble();
                }
                if (vec.Length == 0)
                {
                    throw new InvalidOperationException("Ollama вернула пустой вектор.");
                }
                return vec;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < _maxRetries)
            {
                last = ex;
                await DelayAsync(attempt, cancellationToken);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }
        throw new InvalidOperationException(
            $"Ollama недоступна после {_maxRetries + 1} попыток: {last?.Message}",
            last);
    }

    private static Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var ms = (int)Math.Min(2000, 200 * Math.Pow(2, attempt));
        return Task.Delay(ms, cancellationToken);
    }
}