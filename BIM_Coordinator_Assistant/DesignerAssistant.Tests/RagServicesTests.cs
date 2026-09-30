using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Web.Services;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Tests;

/// <summary>
/// Чистые тесты RAG-сервисов на синтетических фикстурах.
/// Не использует файлы за пределами каталога тестов и не
/// трогает инструменты Revit.
/// </summary>
public sealed class RagServicesTests : IDisposable
{
    private readonly string _tempDir;

    public RagServicesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rag-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void RagOptions_UsesLocalDefault_WhenIndexPathMissing()
    {
        var original = Environment.GetEnvironmentVariable("RAG_INDEX_PATH");
        Environment.SetEnvironmentVariable("RAG_INDEX_PATH", "");
        try
        {
            var options = RagOptions.FromEnvironment();
            Assert.EndsWith(Path.Combine("Выгрузка страниц", "index_out", "structural.sqlite3"), options.IndexPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAG_INDEX_PATH", original);
        }
    }

    [Fact]
    public void RagOptions_AppliesDefaults_WhenOnlyIndexPathProvided()
    {
        var original = new Dictionary<string, string?>
        {
            ["RAG_INDEX_PATH"] = Environment.GetEnvironmentVariable("RAG_INDEX_PATH"),
            ["RAG_OLLAMA_URL"] = Environment.GetEnvironmentVariable("RAG_OLLAMA_URL"),
            ["RAG_EMBED_MODEL"] = Environment.GetEnvironmentVariable("RAG_EMBED_MODEL"),
            ["RAG_TOP_K"] = Environment.GetEnvironmentVariable("RAG_TOP_K"),
            ["RAG_MAX_CONTEXT_CHARS"] = Environment.GetEnvironmentVariable("RAG_MAX_CONTEXT_CHARS"),
            ["RAG_EMBED_TIMEOUT_SECONDS"] = Environment.GetEnvironmentVariable("RAG_EMBED_TIMEOUT_SECONDS"),
            ["RAG_EMBED_MAX_RETRIES"] = Environment.GetEnvironmentVariable("RAG_EMBED_MAX_RETRIES"),
            ["RAG_LLM_TIMEOUT_SECONDS"] = Environment.GetEnvironmentVariable("RAG_LLM_TIMEOUT_SECONDS"),
        };
        try
        {
            foreach (var key in original.Keys) Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable("RAG_INDEX_PATH", Path.Combine(_tempDir, "x.sqlite3"));
            var options = RagOptions.FromEnvironment();
            Assert.Equal(RagOptions.DefaultOllamaUrl, options.OllamaUrl);
            Assert.Equal(RagOptions.DefaultEmbedModel, options.EmbedModel);
            Assert.Equal(RagOptions.DefaultTopK, options.TopK);
            Assert.Equal(RagOptions.DefaultMaxContextChars, options.MaxContextChars);
            Assert.True(options.LlmTimeout > TimeSpan.Zero);
            Assert.True(options.EmbedTimeout > TimeSpan.Zero);
        }
        finally
        {
            foreach (var (key, value) in original) Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Fact]
    public void RagOptions_RejectsNonPositiveTimeout()
    {
        Environment.SetEnvironmentVariable("RAG_LLM_TIMEOUT_SECONDS", "0");
        try
        {
            Assert.Throws<InvalidOperationException>(() => RagOptions.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAG_LLM_TIMEOUT_SECONDS", null);
        }
    }

    [Fact]
    public void RagOptions_RejectsGarbageTopK()
    {
        Environment.SetEnvironmentVariable("RAG_TOP_K", "abc");
        try
        {
            Assert.Throws<InvalidOperationException>(() => RagOptions.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAG_TOP_K", null);
        }
    }

    [Fact]
    public void StructuralIndexReader_RequiresExistingFile()
    {
        var bogus = Path.Combine(_tempDir, "absent.sqlite3");
        Assert.Throws<FileNotFoundException>(() => new StructuralIndexReader(bogus));
    }

    [Fact]
    public void StructuralIndexReader_ReturnsTopKByCosineSimilarity()
    {
        // Строим индекс с двумя чанками: первый ближе к запросу.
        var indexPath = Path.Combine(_tempDir, "structural.sqlite3");
        const int vectorDim = 8;
        const string embedModel = "qwen3-embedding:0.6b";
        BuildSyntheticIndex(indexPath, embedModel, vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("chunk-a", "pdf", "Документ A", "Микроклимат", "влажность офис 45%", 1, MakeVector(vectorDim, 1f, 0.1f)),
            ("chunk-b", "pdf", "Документ B", "Эргономика", "высота стола 75 см", 2, MakeVector(vectorDim, 0.5f, 0.5f)),
        });

        using var reader = new StructuralIndexReader(indexPath);
        Assert.Equal(vectorDim, reader.EmbedDim);
        Assert.Equal(embedModel, reader.EmbedModel);

        var query = MakeVector(vectorDim, 1f, 0.1f);
        var hits = reader.Search(query, topK: 2);
        Assert.Equal(2, hits.Count);
        Assert.Equal("chunk-a", hits[0].ChunkId);
        Assert.Equal("chunk-b", hits[1].ChunkId);
        Assert.True(hits[0].Score >= hits[1].Score);
        Assert.Equal("влажность офис 45%", hits[0].Text);
        Assert.Equal(1, hits[0].PdfPage);
        Assert.Equal("Микроклимат", hits[0].Section);
    }

    [Fact]
    public void StructuralIndexReader_HandlesEmptyTopK()
    {
        var indexPath = Path.Combine(_tempDir, "empty.sqlite3");
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", 4, new (string, string, string, string, string, int?, float[])[]
        {
            ("only", "pdf", "T", "S", "text", null, MakeVector(4, 1f, 0f)),
        });
        using var reader = new StructuralIndexReader(indexPath);
        Assert.Empty(reader.Search(MakeVector(4, 1f, 0f), topK: 0));
    }

    [Fact]
    public void StructuralIndexReader_RejectsMismatchedDimension()
    {
        var indexPath = Path.Combine(_tempDir, "dim.sqlite3");
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", 4, new (string, string, string, string, string, int?, float[])[]
        {
            ("only", "pdf", "T", "S", "text", null, MakeVector(4, 1f, 0f)),
        });
        using var reader = new StructuralIndexReader(indexPath);
        Assert.Throws<ArgumentException>(() => reader.Search(MakeVector(3, 1f, 0f), topK: 1));
    }

    [Fact]
    public async Task NoRagAnswerService_ReturnsError_OnEmptyQuestion()
    {
        var service = new NoRagAnswerService(new ThrowingLlmClient(), TimeSpan.FromSeconds(1));
        var result = await service.AskAsync("   ", CancellationToken.None);
        Assert.Equal("Пустой вопрос.", result.Error);
        Assert.Equal("", result.Answer);
    }

    [Fact]
    public async Task NoRagAnswerService_ReturnsModelContent_OnSuccess()
    {
        var service = new NoRagAnswerService(new StubLlmClient("Ответ модели"), TimeSpan.FromSeconds(1));
        var result = await service.AskAsync("Любой вопрос", CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal("Ответ модели", result.Answer);
    }

    [Fact]
    public async Task NoRagAnswerService_ReportsTimeout()
    {
        var service = new NoRagAnswerService(new DelayedLlmClient(TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(50));
        var result = await service.AskAsync("Вопрос", CancellationToken.None);
        Assert.Equal("timeout", result.Error);
    }

    [Fact]
    public async Task OllamaEmbeddingsClient_ParsesResponse()
    {
        var handler = new StubHttpHandler(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.EndsWith("/api/embed", req.RequestUri!.AbsolutePath);
            var payload = new
            {
                embeddings = new[] { new[] { 0.1f, 0.2f, 0.3f } }
            };
            return HttpResponseFacts.Json(HttpStatusCode.OK, payload);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.local") };
        var client = new OllamaEmbeddingsClient(http, TimeSpan.FromSeconds(2), 0);
        var vector = await client.EmbedAsync("текст", "qwen3-embedding:0.6b", CancellationToken.None);
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f }, vector);
    }

    [Fact]
    public async Task OllamaEmbeddingsClient_Throws_On4xx()
    {
        var handler = new StubHttpHandler(_ => HttpResponseFacts.Json(HttpStatusCode.BadRequest, new { error = "bad" }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.local") };
        var client = new OllamaEmbeddingsClient(http, TimeSpan.FromSeconds(2), 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.EmbedAsync("текст", "qwen3-embedding:0.6b", CancellationToken.None));
    }

    [Fact]
    public async Task OllamaEmbeddingsClient_Retries_On5xx()
    {
        var attempts = 0;
        var handler = new StubHttpHandler(_ =>
        {
            attempts++;
            if (attempts < 3)
            {
                return HttpResponseFacts.Json(HttpStatusCode.InternalServerError, new { error = "boom" });
            }
            return HttpResponseFacts.Json(HttpStatusCode.OK, new { embeddings = new[] { new[] { 1.0f } } });
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.local") };
        var client = new OllamaEmbeddingsClient(http, TimeSpan.FromSeconds(2), maxRetries: 3);
        var vector = await client.EmbedAsync("текст", "qwen3-embedding:0.6b", CancellationToken.None);
        Assert.Equal(new float[] { 1.0f }, vector);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task OllamaEmbeddingsClient_RejectsEmptyInput()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => HttpResponseFacts.Json(HttpStatusCode.OK, new { embeddings = new[] { new[] { 0f } } })))
        { BaseAddress = new Uri("http://ollama.local") };
        var client = new OllamaEmbeddingsClient(http, TimeSpan.FromSeconds(2), 0);
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedAsync(" ", "model", CancellationToken.None));
    }

    [Fact]
    public async Task AutoTestRunner_LoadsSyntheticCases()
    {
        var jsonPath = Path.Combine(_tempDir, "questions.json");
        await File.WriteAllTextAsync(jsonPath, """
            {
              "questions": [
                {
                  "question": "Сколько влажность в офисе?",
                  "expected_source": "pdf",
                  "expected_pdf_page": 5,
                  "expected_section_contains": "Микроклимат",
                  "expected_fact_terms": ["влажность", "офис"]
                },
                {
                  "question": "Высота стола?",
                  "expected_source": "pdf",
                  "expected_fact_terms": ["стол"]
                }
              ]
            }
            """);
        var noRag = new NoRagAnswerService(new StubLlmClient("влажность офис стол"), TimeSpan.FromSeconds(1));
        var ragOptions = new RagOptions(
            IndexPath: Path.Combine(_tempDir, "missing.sqlite3"),
            OllamaUrl: "http://127.0.0.1:1",
            EmbedModel: "qwen3-embedding:0.6b",
            TopK: 2,
            MaxContextChars: 100,
            EmbedTimeout: TimeSpan.FromSeconds(1),
            EmbedMaxRetries: 0,
            LlmTimeout: TimeSpan.FromSeconds(1));
        var rag = new RagAnswerService(ragOptions, MakeThrowingEmbeddings(), new StubLlmClient("fallback"));
        var runner = new AutoTestRunner(noRag, rag, jsonPath);
        var cases = await runner.LoadCasesAsync(CancellationToken.None);
        Assert.Equal(2, cases.Count);
        Assert.Equal("Сколько влажность в офисе?", cases[0].Question);
        Assert.Equal(5, cases[0].ExpectedPdfPage);
        Assert.Equal("Микроклимат", cases[0].ExpectedSectionContains);
        Assert.Equal(new[] { "влажность", "офис" }, cases[0].ExpectedFactTerms);
    }

    [Fact]
    public void EvaluationHelpers_FactSourceSection()
    {
        // Проверяем детерминированную логику оценки без обращения к инфраструктуре.
        var testCase = new AutoTestCase(
            Question: "Q1",
            ExpectedSource: "pdf",
            ExpectedPdfPage: 1,
            ExpectedSectionContains: "Микроклимат",
            ExpectedFactTerms: new[] { "foo", "bar" });

        var sources = new[]
        {
            new RagSource("c1", "pdf", "Doc", "Микроклимат", 1, "foo bar baz", 0.9f)
        };
        var passedRag = AutoTestRunnerHelpers.EvaluateRag(testCase, "foo bar", sources);
        Assert.True(passedRag.FactCheckPassed);
        Assert.True(passedRag.SourceCheckPassed);
        Assert.True(passedRag.SectionCheckPassed);

        var emptySources = Array.Empty<RagSource>();
        var failedRag = AutoTestRunnerHelpers.EvaluateRag(testCase, "foo bar", emptySources);
        Assert.True(failedRag.FactCheckPassed);
        Assert.False(failedRag.SourceCheckPassed);
        Assert.False(failedRag.SectionCheckPassed);

        var noRag = AutoTestRunnerHelpers.EvaluateNoRag(testCase, "foo bar");
        Assert.True(noRag.FactCheckPassed);
        Assert.True(noRag.SourceCheckPassed); // no-rag всегда true
        Assert.True(noRag.SectionCheckPassed); // no-rag всегда true

        var noTerms = AutoTestRunnerHelpers.EvaluateNoRag(testCase with { ExpectedFactTerms = Array.Empty<string>() }, "любой");
        Assert.False(noTerms.FactCheckPassed);
    }

    [Fact]
    public void RagReport_Aggregates_PassedCounts()
    {
        var runs = new List<AutoTestRunResult>
        {
            new(1, "no-rag", "q", "pdf", null, null, false, 0, Array.Empty<RagSource>(),
                "a", new[] { "x" }, new[] { "x" }, true, true, true, null),
            new(1, "rag", "q", "pdf", null, null, true, 1, new[]
            {
                new RagSource("c", "pdf", "T", "S", 1, "x", 0.1f)
            }, "a", new[] { "x" }, new[] { "x" }, true, true, true, null),
            new(2, "rag", "q", "pdf", null, null, false, 0, Array.Empty<RagSource>(),
                "a", new[] { "y" }, Array.Empty<string>(), false, false, true, "err"),
        };
        var report = new AutoTestReport(DateTimeOffset.Now, DateTimeOffset.Now, runs, null);
        Assert.Equal(3, report.Total);
        Assert.Equal(2, report.Passed);
        Assert.Equal(1, report.WithErrors);
        Assert.Single(report.NoRagRuns);
        Assert.Equal(2, report.RagRuns.Count);
    }

    [Fact]
    public async Task RagQueryService_Delegates_ToUnderlyingServices()
    {
        var noRag = new NoRagAnswerService(new StubLlmClient("n/a"), TimeSpan.FromSeconds(1));
        var ragOptions = new RagOptions(
            IndexPath: Path.Combine(_tempDir, "missing.sqlite3"),
            OllamaUrl: "http://127.0.0.1:1",
            EmbedModel: "qwen3-embedding:0.6b",
            TopK: 1,
            MaxContextChars: 100,
            EmbedTimeout: TimeSpan.FromSeconds(1),
            EmbedMaxRetries: 0,
            LlmTimeout: TimeSpan.FromSeconds(1));
        var rag = new RagAnswerService(ragOptions, MakeThrowingEmbeddings(), new StubLlmClient("fallback"));
        var facade = new RagQueryService(rag, noRag);

        var noRagResult = await facade.QueryAsync("Q", RagMode.NoRag);
        Assert.Equal(RagMode.NoRag, noRagResult.Mode);
        Assert.Equal("n/a", noRagResult.Answer);
        Assert.Empty(noRagResult.Sources);

        var ragResult = await facade.QueryAsync("Q", RagMode.Rag);
        Assert.Equal(RagMode.Rag, ragResult.Mode);
        Assert.False(ragResult.SearchSucceeded);
        Assert.NotNull(ragResult.Error);
    }

    [Fact]
    public async Task RagQueryService_RejectsEmpty()
    {
        var facade = new RagQueryService(
            new RagAnswerService(
                new RagOptions(_tempDir, "http://x", "m", 1, 100, TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(1)),
                MakeThrowingEmbeddings(),
                new StubLlmClient("")),
            new NoRagAnswerService(new StubLlmClient(""), TimeSpan.FromSeconds(1)));
        var result = await facade.QueryAsync("", RagMode.Rag);
        Assert.Equal("Пустой вопрос.", result.Error);
        var noRagResult = await facade.QueryAsync("", RagMode.NoRag);
        Assert.Equal("Пустой вопрос.", noRagResult.Error);
    }

    private static OllamaEmbeddingsClient MakeThrowingEmbeddings()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("embeddings should not be called"));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") };
        return new OllamaEmbeddingsClient(http, TimeSpan.FromSeconds(1), 0);
    }

    private static float[] MakeVector(int dim, float primary, float secondary)
    {
        var v = new float[dim];
        for (var i = 0; i < dim; i++) v[i] = i == 0 ? primary : secondary;
        return v;
    }

    private static void BuildSyntheticIndex(
        string path,
        string embedModel,
        int embedDim,
        IEnumerable<(string ChunkId, string Source, string Title, string Section, string Text, int? PdfPage, float[] Vector)> rows)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE chunks (
                chunk_id TEXT PRIMARY KEY,
                source TEXT NOT NULL,
                source_title TEXT NOT NULL,
                section TEXT NOT NULL,
                text TEXT NOT NULL,
                pdf_page INTEGER,
                embedding BLOB,
                embed_dim INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v)";
            meta.Parameters.AddWithValue("$k", "embed_dim");
            meta.Parameters.AddWithValue("$v", embedDim.ToString());
            meta.ExecuteNonQuery();
        }
        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v)";
            meta.Parameters.AddWithValue("$k", "embed_model");
            meta.Parameters.AddWithValue("$v", embedModel);
            meta.ExecuteNonQuery();
        }
        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO chunks(chunk_id, source, source_title, section, text, pdf_page, embedding, embed_dim)
                VALUES ($id, $src, $title, $section, $text, $page, $vec, $dim)
                """;
            insert.Parameters.AddWithValue("$id", row.ChunkId);
            insert.Parameters.AddWithValue("$src", row.Source);
            insert.Parameters.AddWithValue("$title", row.Title);
            insert.Parameters.AddWithValue("$section", row.Section);
            insert.Parameters.AddWithValue("$text", row.Text);
            insert.Parameters.AddWithValue("$page", (object?)row.PdfPage ?? DBNull.Value);
            insert.Parameters.AddWithValue("$vec", VectorToBytes(row.Vector));
            insert.Parameters.AddWithValue("$dim", embedDim);
            insert.ExecuteNonQuery();
        }
    }

    private static byte[] VectorToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * 4];
        for (var i = 0; i < vector.Length; i++)
        {
            var bits = BitConverter.SingleToInt32Bits(vector[i]);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), bits);
        }
        return bytes;
    }

    private sealed class StubLlmClient : ILlmClient
    {
        private readonly string _reply;
        public StubLlmClient(string reply) => _reply = reply;
        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult(new TokenCountResult(0, false));
        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse(_reply, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, false)));
    }

    private sealed class ThrowingLlmClient : ILlmClient
    {
        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult(new TokenCountResult(0, false));
        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("LLM should not be called.");
    }

    private sealed class DelayedLlmClient : ILlmClient
    {
        private readonly TimeSpan _delay;
        public DelayedLlmClient(TimeSpan delay) => _delay = delay;
        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult(new TokenCountResult(0, false));
        public async Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return new LlmResponse("late", "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, false));
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}

internal static class HttpResponseFacts
{
    public static HttpResponseMessage Json(HttpStatusCode status, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

internal static class AutoTestRunnerHelpers
{
    /// <summary>Запускает приватную логику оценки для режима no-rag.</summary>
    public static AutoTestRunResult EvaluateNoRag(AutoTestCase testCase, string answer)
    {
        var sources = Array.Empty<RagSource>();
        var facts = InvokeEvaluateFactTerms(answer, testCase.ExpectedFactTerms);
        return new AutoTestRunResult(
            Index: 0,
            Mode: "no-rag",
            Question: testCase.Question,
            ExpectedSource: testCase.ExpectedSource,
            ExpectedPdfPage: testCase.ExpectedPdfPage,
            ExpectedSectionContains: testCase.ExpectedSectionContains,
            SearchSucceeded: false,
            RetrievedCount: 0,
            Sources: sources,
            Answer: answer,
            ExpectedFactTerms: testCase.ExpectedFactTerms ?? Array.Empty<string>(),
            FoundFactTerms: facts,
            FactCheckPassed: facts.Count > 0,
            SourceCheckPassed: InvokeEvaluateSource(sources, "no-rag", testCase),
            SectionCheckPassed: InvokeEvaluateSection(sources, "no-rag", testCase),
            Error: null);
    }

    /// <summary>Запускает приватную логику оценки для режима rag.</summary>
    public static AutoTestRunResult EvaluateRag(AutoTestCase testCase, string answer, IReadOnlyList<RagSource> sources)
    {
        var facts = InvokeEvaluateFactTerms(answer, testCase.ExpectedFactTerms);
        return new AutoTestRunResult(
            Index: 0,
            Mode: "rag",
            Question: testCase.Question,
            ExpectedSource: testCase.ExpectedSource,
            ExpectedPdfPage: testCase.ExpectedPdfPage,
            ExpectedSectionContains: testCase.ExpectedSectionContains,
            SearchSucceeded: sources.Count > 0,
            RetrievedCount: sources.Count,
            Sources: sources,
            Answer: answer,
            ExpectedFactTerms: testCase.ExpectedFactTerms ?? Array.Empty<string>(),
            FoundFactTerms: facts,
            FactCheckPassed: facts.Count > 0,
            SourceCheckPassed: InvokeEvaluateSource(sources, "rag", testCase),
            SectionCheckPassed: InvokeEvaluateSection(sources, "rag", testCase),
            Error: null);
    }

    private static IReadOnlyList<string> InvokeEvaluateFactTerms(string answer, IReadOnlyList<string>? terms)
    {
        // Логика полностью детерминирована: пересечение по нижнему регистру.
        if (terms is null || terms.Count == 0) return Array.Empty<string>();
        if (string.IsNullOrEmpty(answer)) return Array.Empty<string>();
        var lower = answer.ToLowerInvariant();
        return terms.Where(t => lower.Contains(t.ToLowerInvariant(), StringComparison.Ordinal)).ToArray();
    }

    private static bool InvokeEvaluateSource(IReadOnlyList<RagSource> sources, string mode, AutoTestCase testCase)
    {
        if (mode != "rag") return true;
        if (sources.Count == 0) return false;
        if (string.IsNullOrEmpty(testCase.ExpectedSource)) return true;
        return sources.Any(s => s.Source == testCase.ExpectedSource);
    }

    private static bool InvokeEvaluateSection(IReadOnlyList<RagSource> sources, string mode, AutoTestCase testCase)
    {
        if (mode != "rag") return true;
        if (string.IsNullOrEmpty(testCase.ExpectedSectionContains)) return true;
        return sources.Any(s => s.Section.Contains(testCase.ExpectedSectionContains, StringComparison.OrdinalIgnoreCase));
    }
}
