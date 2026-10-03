using System.Text;
using System.Text.RegularExpressions;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Парсер и валидатор структурированного ответа LLM для RAG (День 24).
/// <para>Модель обязана выдать ответ в формате:</para>
/// <code>
/// ANSWER: &lt;краткий текст&gt;
/// QUOTES:
/// [chunk_id] source=&lt;source&gt; section=&lt;section&gt; pdf_page=&lt;page&gt; quote="&lt;дословный фрагмент&gt;"
/// ...
/// </code>
/// <para>Парсер делает три вещи:</para>
/// <list type="number">
/// <item>Извлекает список цитат (chunk_id, метаданные, дословная цитата).</item>
/// <item>Проверяет, что каждая цитата найдена дословно в <see cref="RagSource.Text"/>
/// соответствующего чанка; иначе помечает её <see cref="RagCitation.Verified"/> = false.</item>
/// <item>Проверяет, что текст ответа поддержан хотя бы одной валидной
/// цитатой — ключевые термины ответа должны присутствовать хотя бы
/// в одной подтверждающей цитате.</item>
/// </list>
/// <para>Если валидных цитат нет или ответ не поддержан цитатами —
/// сервис обязан abstentionить и попросить уточнение.</para>
/// </summary>
public static class RagCitationParser
{
    private const string AnswerPrefix = "ANSWER:";
    private const string QuotesPrefix = "QUOTES:";

    /// <summary>
    /// Результат парсинга: чистый текст ответа и список цитат
    /// (часть может быть невалидированной, см. <see cref="RagCitation.Verified"/>).
    /// </summary>
    public sealed record ParseResult(
        string CleanAnswer,
        IReadOnlyList<RagCitation> Citations,
        bool HasStructuredOutput)
    {
        public IReadOnlyList<RagCitation> VerifiedCitations =>
            Citations.Where(c => c.Verified).ToArray();

        public bool HasAnyVerifiedCitation => VerifiedCitations.Count > 0;
    }

    /// <summary>
    /// Распарсить ответ LLM. Не валидирует цитаты против источников —
    /// только структура. Валидацию делает
    /// <see cref="ValidateAgainstSources"/>.
    /// </summary>
    public static ParseResult Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ParseResult("", Array.Empty<RagCitation>(), false);
        }

        var text = raw.Replace("\r\n", "\n").TrimEnd();
        var answerLines = new List<string>();
        var quoteLines = new List<string>();
        var hasStructured = false;

        var currentSection = "preamble";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith(AnswerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                currentSection = "answer";
                var rest = line[AnswerPrefix.Length..].TrimStart();
                if (rest.Length > 0) answerLines.Add(rest);
                hasStructured = true;
                continue;
            }
            if (line.StartsWith(QuotesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                currentSection = "quotes";
                hasStructured = true;
                continue;
            }
            // Пустые строки внутри секций разделяют.
            if (string.IsNullOrWhiteSpace(line))
            {
                if (currentSection == "answer") answerLines.Add("");
                continue;
            }
            if (currentSection == "answer")
            {
                answerLines.Add(line);
            }
            else if (currentSection == "quotes")
            {
                quoteLines.Add(line);
            }
            // preamble и другие секции игнорируем.
        }

        var answer = string.Join("\n", answerLines).Trim();
        var parsed = new List<RagCitation>();
        foreach (var q in quoteLines)
        {
            var citation = ParseCitationLine(q);
            if (citation is not null) parsed.Add(citation);
        }

        return new ParseResult(answer, parsed, hasStructured);
    }

    /// <summary>
    /// Валидировать цитаты против источников: проверить, что цитата
    /// найдена дословно в <see cref="RagSource.Text"/>; проверить,
    /// что chunk_id действительно присутствует в списке источников;
    /// проверить, что текст ответа поддержан цитатами (есть
    /// пересечение хотя бы по одному осмысленному термину).
    /// </summary>
    public static ParseResult ValidateAgainstSources(
        ParseResult parsed,
        IReadOnlyList<RagSource> sources)
    {
        if (parsed.Citations.Count == 0)
        {
            return parsed;
        }

        var byId = sources
            .GroupBy(s => s.ChunkId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var validated = new List<RagCitation>(parsed.Citations.Count);
        foreach (var citation in parsed.Citations)
        {
            // Цитата без chunk_id считается невалидной: без привязки
            // к источнику её нельзя проверить против текста.
            RagSource? source = null;
            if (!string.IsNullOrWhiteSpace(citation.ChunkId))
            {
                byId.TryGetValue(citation.ChunkId, out source);
                if (source is null && int.TryParse(citation.ChunkId, out var ordinal) &&
                    ordinal >= 1 && ordinal <= sources.Count)
                {
                    source = sources[ordinal - 1];
                }
            }
            if (source is null)
            {
                validated.Add(citation with { Verified = false });
                continue;
            }
            // Цитата без текста тоже невалидна.
            if (string.IsNullOrWhiteSpace(citation.Quote))
            {
                validated.Add(citation with { Verified = false });
                continue;
            }
            // Дословная проверка: цитата должна встречаться в Text чанка
            // (с учётом регистра и без усечения).
            var ok = citation.Source == source.Source &&
                citation.Section == source.Section &&
                citation.PdfPage == source.PdfPage &&
                source.Text.Contains(citation.Quote, StringComparison.Ordinal);
            validated.Add(citation with
            {
                ChunkId = source.ChunkId,
                Verified = ok
            });
        }

        return new ParseResult(parsed.CleanAnswer, validated, parsed.HasStructuredOutput);
    }

    /// <summary>
    /// Проверить, что ответ поддержан цитатами: если в тексте ответа
    /// есть хотя бы один термин длиной ≥ 3 символа, он должен
    /// встречаться хотя бы в одной из валидных цитат. Иначе — ответ
    /// считается неподтверждённым.
    /// </summary>
    public static bool AnswerSupportedByQuotes(
        string answer,
        IReadOnlyList<RagCitation> verifiedCitations)
    {
        if (verifiedCitations.Count == 0 || string.IsNullOrWhiteSpace(answer) ||
            answer.Contains("Не знаю", StringComparison.OrdinalIgnoreCase)) return false;

        var quoted = string.Join(" ", verifiedCitations.Select(c => c.Quote));
        var quotedIds = IdentifierPattern.Matches(quoted)
            .Select(m => m.Value).Where(s => s.Any(char.IsDigit))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var answerIds = IdentifierPattern.Matches(answer).Select(m => m.Value)
            .Where(s => s.Any(char.IsDigit)).ToArray();
        if (answerIds.Any(id => !quotedIds.Contains(id))) return false;

        // Non-identifier prose may be paraphrased, but must still share
        // substantive terms with the evidence.
        var answerTerms = ExtractSupportTerms(answer).ToArray();
        return answerIds.Length > 0 || answerTerms.Any(term =>
            quoted.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex IdentifierPattern = new(
        @"(?<![\p{L}\p{N}])[\p{L}\p{N}]+(?:[-_.][\p{L}\p{N}]+)*(?![\p{L}\p{N}])",
        RegexOptions.Compiled);

    private const int MinSupportTermLength = 3;

    private static IEnumerable<string> ExtractSupportTerms(string answer)
    {
        // Простая токенизация: слова длиной ≥ MinSupportTermLength, в нижнем регистре.
        var current = new StringBuilder();
        foreach (var ch in answer)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                if (current.Length >= MinSupportTermLength) yield return current.ToString();
                current.Clear();
            }
        }
        if (current.Length >= MinSupportTermLength) yield return current.ToString();
    }

    private static RagCitation? ParseCitationLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var trimmed = line.Trim();

        // Допустимые формы строки цитаты (без строгой JSON-схемы):
        //   [chunk_id] source=confluence section="..." pdf_page=2 quote="..."
        //   [chunk_id] source=pdf section="..." quote="..."
        // chunk_id — первая непустая [...] группа в начале строки.
        var chunkId = "";
        if (trimmed.StartsWith('['))
        {
            var end = trimmed.IndexOf(']');
            if (end > 1)
            {
                chunkId = trimmed[1..end].Trim();
                trimmed = trimmed[(end + 1)..].TrimStart();
            }
        }
        if (string.IsNullOrWhiteSpace(chunkId))
        {
            // Без chunk_id цитата бессмысленна — отбрасываем.
            return null;
        }

        var source = ReadKeyValue(trimmed, "source") ?? "";
        var section = ReadKeyValue(trimmed, "section") ?? "";
        int? page = null;
        var pageRaw = ReadKeyValue(trimmed, "pdf_page");
        if (!string.IsNullOrWhiteSpace(pageRaw) &&
            int.TryParse(pageRaw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var p) &&
            p > 0)
        {
            page = p;
        }
        var quote = ReadKeyValue(trimmed, "quote") ?? "";

        return new RagCitation(
            ChunkId: chunkId,
            Source: source,
            Section: section,
            PdfPage: page,
            Quote: quote,
            Verified: false);
    }

    /// <summary>
    /// Прочитать значение ключа из строки вида
    /// <c>key=value</c> или <c>key="value"</c>. Ищет первое вхождение.
    /// </summary>
    private static string? ReadKeyValue(string text, string key)
    {
        var idx = text.IndexOf(key + "=", StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + key.Length + 1;
        if (start >= text.Length) return null;
        if (text[start] == '"')
        {
            // Quoted value: ищем следующую " не перед \
            var end = start + 1;
            while (end < text.Length)
            {
                if (text[end] == '\\' && end + 1 < text.Length)
                {
                    end += 2;
                    continue;
                }
                if (text[end] == '"') break;
                end++;
            }
            if (end >= text.Length) return null;
            var raw = text.Substring(start + 1, end - start - 1);
            return Unescape(raw);
        }
        // Bare value до следующего пробела.
        var space = text.IndexOfAny(new[] { ' ', '\t' }, start);
        var valueEnd = space < 0 ? text.Length : space;
        return text.Substring(start, valueEnd - start);
    }

    private static string Unescape(string raw)
    {
        // Поддерживаем \" и \\, остальное — литерал.
        var sb = new StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                var next = raw[i + 1];
                if (next == '"' || next == '\\') { sb.Append(next); i += 2; continue; }
            }
            sb.Append(raw[i]);
            i++;
        }
        return sb.ToString();
    }
}
