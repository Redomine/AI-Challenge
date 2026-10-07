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
        var facade = new RagQueryService(rag, noRag, ragOptions);

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
            new NoRagAnswerService(new StubLlmClient(""), TimeSpan.FromSeconds(1)),
            new RagOptions(_tempDir, "http://x", "m", 1, 100, TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(1)));
        var result = await facade.QueryAsync("", RagMode.Rag);
        Assert.Equal("Пустой вопрос.", result.Error);
        var noRagResult = await facade.QueryAsync("", RagMode.NoRag);
        Assert.Equal("Пустой вопрос.", noRagResult.Error);
    }

    // --- Расширенный RAG: переписывание запроса + pre/post фильтр ---

    [Fact]
    public void RagOptions_RejectsScoreThresholdOutOfRange()
    {
        var original = Environment.GetEnvironmentVariable("RAG_SCORE_THRESHOLD");
        try
        {
            Environment.SetEnvironmentVariable("RAG_SCORE_THRESHOLD", "1.5");
            Assert.Throws<InvalidOperationException>(() => RagOptions.FromEnvironment());
            Environment.SetEnvironmentVariable("RAG_SCORE_THRESHOLD", "-2");
            Assert.Throws<InvalidOperationException>(() => RagOptions.FromEnvironment());
            Environment.SetEnvironmentVariable("RAG_SCORE_THRESHOLD", "abc");
            Assert.Throws<InvalidOperationException>(() => RagOptions.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAG_SCORE_THRESHOLD", original);
        }
    }

    [Fact]
    public void RagOptions_RejectsPostFilterKGreaterThanPreFilterK()
    {
        var original = new Dictionary<string, string?>
        {
            ["RAG_PRE_FILTER_K"] = Environment.GetEnvironmentVariable("RAG_PRE_FILTER_K"),
            ["RAG_POST_FILTER_K"] = Environment.GetEnvironmentVariable("RAG_POST_FILTER_K"),
        };
        try
        {
            Environment.SetEnvironmentVariable("RAG_PRE_FILTER_K", "4");
            Environment.SetEnvironmentVariable("RAG_POST_FILTER_K", "8");
            Assert.Throws<InvalidOperationException>(() => RagOptions.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAG_PRE_FILTER_K", original["RAG_PRE_FILTER_K"]);
            Environment.SetEnvironmentVariable("RAG_POST_FILTER_K", original["RAG_POST_FILTER_K"]);
        }
    }

    [Fact]
    public void RagOptions_DefaultsForRagEnhanced()
    {
        var original = new Dictionary<string, string?>
        {
            ["RAG_REWRITE_ENABLED"] = Environment.GetEnvironmentVariable("RAG_REWRITE_ENABLED"),
            ["RAG_PRE_FILTER_K"] = Environment.GetEnvironmentVariable("RAG_PRE_FILTER_K"),
            ["RAG_POST_FILTER_K"] = Environment.GetEnvironmentVariable("RAG_POST_FILTER_K"),
            ["RAG_SCORE_THRESHOLD"] = Environment.GetEnvironmentVariable("RAG_SCORE_THRESHOLD"),
        };
        try
        {
            foreach (var key in original.Keys) Environment.SetEnvironmentVariable(key, null);
            var options = RagOptions.FromEnvironment();
            Assert.False(options.RewriteEnabled);
            Assert.Equal(RagOptions.DefaultPreFilterK, options.PreFilterK);
            Assert.Equal(RagOptions.DefaultPostFilterK, options.PostFilterK);
            Assert.Equal(RagOptions.DefaultScoreThreshold, options.ScoreThreshold);
            Assert.Equal(RagOptions.DefaultPostFilterK, options.EffectivePostFilterK);
        }
        finally
        {
            foreach (var (key, value) in original) Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Fact]
    public async Task RagAnswer_Baseline_UsesOriginalQuestion_NoRewrite()
    {
        var indexPath = Path.Combine(_tempDir, "baseline.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c1", "pdf", "Doc", "Микроклимат", "влажность офис 45%", 1, MakeVector(vectorDim, 1f, 0f)),
            ("c2", "pdf", "Doc", "Эргономика", "высота стола 75 см", 2, MakeVector(vectorDim, 0f, 1f)),
        });

        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new RecordingLlmClient(new[] { "baseline-ответ" });
        var options = MakeBaselineOptions(indexPath, preFilterK: 5, postFilterK: 1);
        var service = new RagAnswerService(options, embeddings, llm);

        var result = await service.AskAsync("Сколько влажность в офисе?", CancellationToken.None);

        Assert.True(result.SearchSucceeded);
        Assert.NotNull(result.Trace);
        Assert.Equal("Сколько влажность в офисе?", result.Trace!.OriginalQuestion);
        Assert.Equal("Сколько влажность в офисе?", result.Trace.SearchQuery);
        Assert.Equal(1, result.Trace.PreFilterCount);
        Assert.Equal(1, result.Trace.PostFilterCount);
        Assert.Single(result.Sources);
        Assert.Equal("c1", result.Sources[0].ChunkId);
        // Один вызов LLM в базовом режиме.
        Assert.Single(llm.Calls);
    }

    [Fact]
    public async Task RagAnswer_Enhanced_RewritesQuery_EmbedsRewritten_AnswersOriginal()
    {
        var indexPath = Path.Combine(_tempDir, "enhanced.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c1", "pdf", "Doc", "Микроклимат", "влажность 45%", 1, MakeVector(vectorDim, 1f, 0f)),
            ("c2", "pdf", "Doc", "Эргономика", "стол 75 см", 2, MakeVector(vectorDim, 0f, 1f)),
            ("c3", "pdf", "Doc", "Шум", "шум 55 дБ", 3, MakeVector(vectorDim, 0.5f, 0.5f)),
        });

        // HttpHandler возвращает вектор ТОЛЬКО если в payload указан ожидаемый текст.
        // Это позволяет проверить, что эмбеддинг считали именно для переписанного запроса.
        var (http, embeddedTexts) = MakeRecordingEmbeddingHttp(vectorDim, closeTo: MakeVector(vectorDim, 1f, 0f), expectText: "влажность офис");
        var embeddings = new OllamaEmbeddingsClient(http, TimeSpan.FromSeconds(2), 0);
        // LLM: первый вызов — переписывание, второй — ответ в формате ANSWER + QUOTES.
        // Структурированный ответ требуется на День 24: модель обязана
        // выдать цитату с дословным фрагментом из контекста.
        var llm = new RecordingLlmClient(new[]
        {
            "влажность офис",
            "ANSWER: влажность 45%\nQUOTES:\n[c1] source=pdf section=\"Микроклимат\" pdf_page=1 quote=\"влажность 45%\"\n",
        });
        var options = MakeBaselineOptions(indexPath, preFilterK: 6, postFilterK: 3);
        var optionsEnhanced = options with { RewriteEnabled = true };
        var service = new RagAnswerService(optionsEnhanced, embeddings, llm);

        var original = "Подскажи, какая влажность должна быть у нас в офисном помещении, согласно нормам?";
        var result = await service.AskAsync(original, optionsEnhanced.ToEnhancedSettings(), CancellationToken.None);

        Assert.True(result.SearchSucceeded);
        Assert.NotNull(result.Trace);
        Assert.Equal(original, result.Trace!.OriginalQuestion);
        Assert.Equal("влажность офис", result.Trace.SearchQuery);
        // Два вызова LLM: rewrite + answer.
        Assert.Equal(2, llm.Calls.Count);
        Assert.Equal("влажность 45%", result.Answer);
        // Ответ строится по оригинальному вопросу — он в промпте второго вызова LLM.
        Assert.Contains(original, llm.Calls[1].UserMessage);
        // Переписанный поисковый запрос НЕ должен попасть в финальный промпт модели.
        Assert.DoesNotContain("влажность офис", llm.Calls[1].UserMessage);
        // Эмбеддинг считали ровно один раз и для переписанного запроса.
        Assert.Single(embeddedTexts);
        Assert.Equal("влажность офис", embeddedTexts[0]);
    }

    [Fact]
    public async Task RagAnswer_Enhanced_FallsBackToOriginalQuery_OnRewriteFailure()
    {
        var indexPath = Path.Combine(_tempDir, "fallback.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c1", "pdf", "Doc", "Микроклимат", "влажность 45%", 1, MakeVector(vectorDim, 1f, 0f)),
        });

        var original = "Сколько влажность в офисе?";
        var (http, embeddedTexts) = MakeRecordingEmbeddingHttp(vectorDim, closeTo: MakeVector(vectorDim, 1f, 0f), expectText: original);
        var embeddings = new OllamaEmbeddingsClient(http, TimeSpan.FromSeconds(2), 0);
        // LLM кидает исключение на любой вызов.
        var llm = new ThrowOnInstructionsLlmClient("boom-rewrite");
        var options = MakeBaselineOptions(indexPath, preFilterK: 4, postFilterK: 2) with { RewriteEnabled = true };
        var service = new RagAnswerService(options, embeddings, llm);

        var result = await service.AskAsync(original, CancellationToken.None);

        Assert.True(result.SearchSucceeded);
        Assert.NotNull(result.Trace);
        Assert.Equal(original, result.Trace!.SearchQuery);
        Assert.NotNull(result.Error);
        Assert.Contains("rewrite", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(embeddedTexts);
        Assert.Equal(original, embeddedTexts[0]);
    }

    [Fact]
    public async Task RagAnswer_AppliesThresholdAndLimit_PopulatesRejections()
    {
        var indexPath = Path.Combine(_tempDir, "filter.sqlite3");
        const int vectorDim = 4;
        // 4 чанка с разной косинус-близостью к запросу (1, 0.1).
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c-top",    "pdf", "D", "S", "top",    1, MakeVector(vectorDim, 1.0f, 0.1f)),
            ("c-mid",    "pdf", "D", "S", "mid",    2, MakeVector(vectorDim, 0.7f, 0.7f)),
            ("c-low",    "pdf", "D", "S", "low",    3, MakeVector(vectorDim, 0.1f, 1.0f)),
            ("c-tiny",   "pdf", "D", "S", "tiny",   4, MakeVector(vectorDim, 0.0f, 0.5f)),
        });

        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0.1f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new RecordingLlmClient(new[] { "ok" });
        // threshold=0.2 чётко отсекает c-tiny (~0.17) по скору, не задевая c-low (~0.23);
        // post=2 оставляет c-top и c-mid, а c-low отбрасывается по лимиту.
        var options = MakeBaselineOptions(indexPath, preFilterK: 4, postFilterK: 2)
            with { ScoreThreshold = 0.2f, RewriteEnabled = true };
        var service = new RagAnswerService(options, embeddings, llm);

        var result = await service.AskAsync("Вопрос", options.ToEnhancedSettings(), CancellationToken.None);

        Assert.True(result.SearchSucceeded);
        Assert.NotNull(result.Trace);
        Assert.Equal(4, result.Trace!.PreFilterCount);
        Assert.Equal(2, result.Trace.PostFilterCount);
        Assert.Equal(2, result.Sources.Count);
        Assert.Equal("c-top", result.Sources[0].ChunkId);
        Assert.Equal("c-mid", result.Sources[1].ChunkId);
        // Два отклонённых: c-low — лимит, c-tiny — порог.
        Assert.Equal(2, result.Trace.Rejected.Count);
        var byReason = result.Trace.Rejected.ToDictionary(r => r.ChunkId, r => r.Reason);
        Assert.Contains("лимит", byReason["c-low"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("threshold", byReason["c-tiny"], StringComparison.OrdinalIgnoreCase);
        // Скоринговый список из pre-filter содержит все 4 значения в исходном порядке индекса.
        Assert.Equal(4, result.Trace.PreFilterScores.Count);
    }

    [Fact]
    public async Task RagAnswer_EmptyFilteredContext_ProducesEmptySourcesAndFallback()
    {
        var indexPath = Path.Combine(_tempDir, "empty.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c-low", "pdf", "D", "S", "low", 1, MakeVector(vectorDim, 0.1f, 1.0f)),
        });

        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0.1f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new RecordingLlmClient(new[] { "fallback-ответ" });
        // threshold = 0.9 отсекает единственный кандидат.
        var options = MakeBaselineOptions(indexPath, preFilterK: 1, postFilterK: 1)
            with { ScoreThreshold = 0.9f, RewriteEnabled = true };
        var service = new RagAnswerService(options, embeddings, llm);

        var result = await service.AskAsync("Вопрос", options.ToEnhancedSettings(), CancellationToken.None);

        Assert.True(result.SearchSucceeded);
        Assert.Empty(result.Sources);
        Assert.NotNull(result.Trace);
        Assert.Equal(1, result.Trace!.PreFilterCount);
        Assert.Equal(0, result.Trace.PostFilterCount);
        Assert.Single(result.Trace.Rejected);
        Assert.Contains("threshold", result.Trace.Rejected[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RagAnswer_BuildPrompt_CyrillicLongText_SmallBudget_NoException()
    {
        // Регрессионный тест на live-баг: BuildPrompt падал с
        // "Index and length must refer to a location within the string. (Parameter length)"
        // при формировании промпта с кириллическим текстом и маленьким MaxContextChars.
        // Корень — смешение UTF-8 байтов и char-индексов в text[..N].
        var indexPath = Path.Combine(_tempDir, "cyr.sqlite3");
        const int vectorDim = 4;
        // Длинный кириллический текст в каждом из 3 чанков (≈80 символов,
        // ≈160 байт в UTF-8). Это гарантирует, что старый byte/char микс
        // приводил к N > text.Length на повторной обрезке.
        var cyr1 = new string('А', 80); // "ААА...А"
        var cyr2 = new string('Б', 80); // "БББ...Б"
        var cyr3 = new string('В', 80); // "ВВВ...В"
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c1", "pdf", "Документ 1", "Микроклимат", cyr1, 1, MakeVector(vectorDim, 1f, 0f)),
            ("c2", "pdf", "Документ 2", "Эргономика",  cyr2, 2, MakeVector(vectorDim, 0.9f, 0.1f)),
            ("c3", "pdf", "Документ 3", "Шум",         cyr3, 3, MakeVector(vectorDim, 0.8f, 0.2f)),
        });

        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new RecordingLlmClient(new[] { "ok" });
        // MaxContextChars мал относительно суммы meta+text, чтобы:
        //  - первый источник прошёл с обрезкой текста (text.Length > budget);
        //  - второй получил остаток 0 или почти 0;
        //  - третий гарантированно не влез — должен быть break.
        // День 24: meta-строка длиннее (chunk_id/section/title), поэтому
        // бюджет увеличен, но по-прежнему гарантирует обрезку.
        const int maxContextChars = 130;
        var options = MakeBaselineOptions(indexPath, preFilterK: 4, postFilterK: 3)
            with { MaxContextChars = maxContextChars };
        var service = new RagAnswerService(options, embeddings, llm);

        var result = await service.AskAsync("Вопрос", CancellationToken.None);

        // Поиск успешен и все три источника возвращены — обрезка касается
        // только промпта, не результатов поиска.
        Assert.True(result.SearchSucceeded);
        Assert.Equal(3, result.Sources.Count);
        Assert.Single(llm.Calls);
        var prompt = llm.Calls[0].UserMessage;

        // Считаем фактически записанные в промпт meta+text для каждого
        // блока вида "[i] source=...\r\n<text>\r\n\r\n". Это ровно та
        // единица, которую ограничивает MaxContextChars.
        var written = MeasureSourceBlockChars(prompt, expectedCount: 3);
        Assert.True(written.BlockCount >= 1, "Должен быть записан хотя бы один источник.");
        Assert.True(written.TotalMetaPlusText <= maxContextChars,
            $"Сумма meta+text ({written.TotalMetaPlusText}) превысила MaxContextChars ({maxContextChars}).");

        // Первый источник начался писаться — проверим, что кириллица из него
        // действительно попала в промпт (а не была проглочена молча).
        Assert.Contains(cyr1[..20], prompt);
    }

    /// <summary>
    /// Извлекает из промпта блоки источников "[i] source=..." и возвращает
    /// сумму длин meta+text, а также количество найденных блоков.
    /// </summary>
    private static (int TotalMetaPlusText, int BlockCount) MeasureSourceBlockChars(
        string prompt, int expectedCount)
    {
        var total = 0;
        var blocks = 0;
        for (var i = 0; i < expectedCount; i++)
        {
            var header = $"[{i + 1}] source=";
            var headerIdx = prompt.IndexOf(header, StringComparison.Ordinal);
            if (headerIdx < 0) break;
            blocks++;
            // Найдём конец строки заголовка.
            var lineEnd = prompt.IndexOf('\n', headerIdx);
            if (lineEnd < 0) break;
            var metaLen = lineEnd - headerIdx; // включает ":\r" на Windows, но длина ASCII стабильна
            // Текст источника идёт до первой пустой строки ("\r\n\r\n" или "\n\n").
            var textStart = lineEnd + 1;
            int textEnd;
            var crlfBlk = prompt.IndexOf("\r\n\r\n", textStart, StringComparison.Ordinal);
            var lfBlk = prompt.IndexOf("\n\n", textStart, StringComparison.Ordinal);
            if (crlfBlk >= 0 && (lfBlk < 0 || crlfBlk < lfBlk)) textEnd = crlfBlk;
            else textEnd = lfBlk;
            if (textEnd < 0) textEnd = prompt.Length;
            var textLen = textEnd - textStart;
            // meta содержит "[i] source=...:\r" — в зачёт берём только "[i] source=...:"
            // (без CR), плюс сам текст. Это совпадает с headerLen + text.Length в BuildPrompt.
            var metaOnly = metaLen > 0 && prompt[headerIdx + metaLen - 1] == '\r'
                ? metaLen - 1
                : metaLen;
            total += metaOnly + textLen;
        }
        return (total, blocks);
    }

    [Fact]
    public async Task RagQueryService_ExposesTrace_OnRagMode()
    {
        var indexPath = Path.Combine(_tempDir, "facade.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c1", "pdf", "D", "S", "top", 1, MakeVector(vectorDim, 1f, 0f)),
        });
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new RecordingLlmClient(new[] { "ok" });
        var options = MakeBaselineOptions(indexPath, preFilterK: 4, postFilterK: 2);
        var facade = new RagQueryService(
            new RagAnswerService(options, embeddings, llm),
            new NoRagAnswerService(new StubLlmClient("n/a"), TimeSpan.FromSeconds(1)),
            options);

        var ragResult = await facade.QueryAsync("Вопрос", RagMode.Rag);
        Assert.NotNull(ragResult.Trace);
        Assert.Equal(1, ragResult.Trace!.PreFilterCount);
        Assert.Equal(1, ragResult.Trace.PostFilterCount);

        var noRagResult = await facade.QueryAsync("Вопрос", RagMode.NoRag);
        Assert.Null(noRagResult.Trace);
    }

    private RagOptions MakeBaselineOptions(string indexPath, int preFilterK, int postFilterK) =>
        new(
            IndexPath: indexPath,
            OllamaUrl: "http://ollama.local",
            EmbedModel: "qwen3-embedding:0.6b",
            TopK: postFilterK,
            MaxContextChars: 4000,
            EmbedTimeout: TimeSpan.FromSeconds(2),
            EmbedMaxRetries: 0,
            LlmTimeout: TimeSpan.FromSeconds(5),
            RewriteEnabled: false,
            RewriteTimeout: TimeSpan.FromSeconds(1),
            PreFilterK: preFilterK,
            ScoreThreshold: 0f,
            PostFilterK: postFilterK);

    // --- Day 23 semantic fixes: baseline score threshold, negative cases, rewrite cancellation ---

    [Fact]
    public void RagOptions_ToBaselineSettings_HasNoScoreFilter()
    {
        // Baseline-режим не должен фильтровать по score — иначе кандидаты
        // с отрицательной косинус-близостью будут отсечены (валидный
        // диапазон cosine ∈ [-1, 1]). Используем -1f как «без порога».
        var options = new RagOptions(
            IndexPath: Path.Combine(_tempDir, "x.sqlite3"),
            OllamaUrl: "http://ollama.local",
            EmbedModel: "qwen3-embedding:0.6b",
            TopK: 4,
            MaxContextChars: 1000,
            EmbedTimeout: TimeSpan.FromSeconds(1),
            EmbedMaxRetries: 0,
            LlmTimeout: TimeSpan.FromSeconds(1),
            RewriteEnabled: true,
            ScoreThreshold: 0.5f,
            PostFilterK: 2);
        var baseline = options.ToBaselineSettings();
        Assert.False(baseline.RewriteEnabled);
        Assert.Equal(-1f, baseline.ScoreThreshold);
    }

    [Fact]
    public async Task RagAnswer_Baseline_KeepsNegativeCosineCandidate()
    {
        // Доказательство, что baseline сохраняет top-K с отрицательным
        // косинусом. Симулируем индекс, где ближайший чанк имеет
        // отрицательный скор (противоположно направленный вектор).
        var indexPath = Path.Combine(_tempDir, "neg.sqlite3");
        const int vectorDim = 4;
        // Запрос = (1, 0, 0, 0); ближайший чанк = (-1, 0, 0, 0) → cos = -1.
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("neg", "pdf", "Doc", "Noise", "irrelevant text", 1, MakeVector(vectorDim, -1f, 0f)),
        });

        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new RecordingLlmClient(new[] { "baseline-ответ" });
        var options = MakeBaselineOptions(indexPath, preFilterK: 1, postFilterK: 1);
        var service = new RagAnswerService(options, embeddings, llm);

        var result = await service.AskAsync("Вопрос", CancellationToken.None);

        Assert.True(result.SearchSucceeded);
        Assert.NotNull(result.Trace);
        Assert.Equal(1, result.Trace!.PreFilterCount);
        Assert.Equal(1, result.Trace.PostFilterCount);
        Assert.Single(result.Sources);
        Assert.Equal("neg", result.Sources[0].ChunkId);
        Assert.True(result.Sources[0].Score < 0f,
            $"Ожидался отрицательный косинус, получено {result.Sources[0].Score}");
    }

    [Fact]
    public async Task AutoTestRunner_PreservesEnhancedTrace_AndNullsForBaselineAndNoRag()
    {
        // Day 23: AutoTestRunResult.Trace должен переносить фактический
        // RagSearchTrace из RagAnswerService для enhanced-прогона, и
        // оставаться null для baseline и no-rag (поиск не выполнялся).
        // Доказательство берётся из реального сервиса + эмбеддинга, а
        // не из заглушки — это требование AGENTS.md о фактических
        // значениях из тулзов/сервисов.
        var indexPath = Path.Combine(_tempDir, "trace.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c-top", "pdf", "D", "S", "top", 1, MakeVector(vectorDim, 1.0f, 0.1f)),
            ("c-low", "pdf", "D", "S", "low", 2, MakeVector(vectorDim, 0.1f, 1.0f)),
        });

        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0.1f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new RecordingLlmClient(new[] { "baseline-answer", "rewritten", "enhanced-answer" });
        var options = MakeBaselineOptions(indexPath, preFilterK: 4, postFilterK: 2)
            with { ScoreThreshold = 0.5f, RewriteEnabled = true };
        var rag = new RagAnswerService(options, embeddings, llm);
        var noRag = new NoRagAnswerService(new StubLlmClient("n/a"), TimeSpan.FromSeconds(1));
        var runner = new AutoTestRunner(noRag, rag, Path.Combine(_tempDir, "unused.json"));

        var cases = new List<AutoTestCase>
        {
            new(
                Question: "Вопрос",
                ExpectedSource: "pdf",
                ExpectedPdfPage: null,
                ExpectedSectionContains: null,
                ExpectedFactTerms: Array.Empty<string>(),
                ExpectedNoEvidence: false),
        };

        var baselineSettings = options.ToBaselineSettings() with { PostFilterK = options.TopK };
        var enhancedSettings = options.ToEnhancedSettings();
        var report = await runner.RunAggregateAsync(
            cases,
            baseline: baselineSettings,
            enhanced: enhancedSettings,
            includeNoRag: true,
            progress: null,
            cancellationToken: CancellationToken.None);

        var byMode = report.Runs.GroupBy(r => r.Mode).ToDictionary(g => g.Key, g => g.Single());

        // no-rag: поиска не было — Trace == null.
        Assert.Null(byMode["no-rag"].Trace);
        // baseline: Trace отражает исходный вопрос без переписывания,
        // SearchQuery == OriginalQuestion, поиск выполнен.
        var baselineRun = byMode["baseline"];
        Assert.NotNull(baselineRun.Trace);
        Assert.Equal("Вопрос", baselineRun.Trace!.OriginalQuestion);
        Assert.Equal("Вопрос", baselineRun.Trace.SearchQuery);
        Assert.True(baselineRun.SearchSucceeded);
        // enhanced: Trace содержит переписанный запрос и фактические скоринговые
        // данные из RagAnswerService (pre/post counts, rejected).
        var enhancedRun = byMode["enhanced"];
        Assert.NotNull(enhancedRun.Trace);
        Assert.Equal("Вопрос", enhancedRun.Trace!.OriginalQuestion);
        Assert.Equal("rewritten", enhancedRun.Trace.SearchQuery);
        Assert.Equal(2, enhancedRun.Trace.PreFilterCount);
        Assert.True(enhancedRun.Trace.PostFilterCount >= 1);
        Assert.NotEmpty(enhancedRun.Trace.PreFilterScores);
        // Отклонение по threshold 0.5: c-low не проходит порог.
        Assert.Contains(enhancedRun.Trace.Rejected, r => r.ChunkId == "c-low");
    }

    [Fact]
    public void AutoTestRunResult_OptionalTrace_DefaultsToNull_ForBackwardCompat()
    {
        // Совместимость со старыми позиционными вызовами: 16-й аргумент
        // (Error) — последний обязательный; 17-й Trace опционален и
        // по умолчанию null.
        var result = new AutoTestRunResult(
            Index: 1,
            Mode: "baseline",
            Question: "q",
            ExpectedSource: "pdf",
            ExpectedPdfPage: null,
            ExpectedSectionContains: null,
            ExpectedNoEvidence: false,
            SearchSucceeded: true,
            RetrievedCount: 1,
            Sources: Array.Empty<RagSource>(),
            Answer: "a",
            ExpectedFactTerms: new[] { "x" },
            FoundFactTerms: new[] { "x" },
            FactCheckPassed: true,
            SourceCheckPassed: true,
            SectionCheckPassed: true,
            Error: null);
        Assert.Null(result.Trace);
    }

    [Fact]
    public async Task AutoTestRunner_LoadCases_ParsesExpectedNoEvidence()
    {
        // Парсинг JSON-флага expected_no_evidence. Должен корректно
        // различать отсутствие флага (default false), true и false.
        var jsonPath = Path.Combine(_tempDir, "questions.json");
        await File.WriteAllTextAsync(jsonPath, """
            {
              "questions": [
                {
                  "question": "Что-то неизвестное?",
                  "expected_source": "",
                  "expected_fact_terms": [],
                  "expected_no_evidence": true
                },
                {
                  "question": "Обычный вопрос?",
                  "expected_source": "pdf",
                  "expected_fact_terms": ["foo"]
                },
                {
                  "question": "Пустой список без флага?",
                  "expected_source": "pdf",
                  "expected_fact_terms": []
                },
                {
                  "question": "Явный негатив без терминов?",
                  "expected_source": "",
                  "expected_fact_terms": [],
                  "expected_no_evidence": false
                }
              ]
            }
            """);
        var noRag = new NoRagAnswerService(new StubLlmClient("n/a"), TimeSpan.FromSeconds(1));
        var ragOptions = MakeBaselineOptions(
            Path.Combine(_tempDir, "missing.sqlite3"), preFilterK: 1, postFilterK: 1);
        var rag = new RagAnswerService(ragOptions, MakeThrowingEmbeddings(), new StubLlmClient("fallback"));
        var runner = new AutoTestRunner(noRag, rag, jsonPath);

        var cases = await runner.LoadCasesAsync(CancellationToken.None);
        Assert.Equal(4, cases.Count);
        Assert.True(cases[0].ExpectedNoEvidence);
        Assert.False(cases[1].ExpectedNoEvidence);
        Assert.False(cases[2].ExpectedNoEvidence); // пустой expected_fact_terms без флага — не негатив
        Assert.False(cases[3].ExpectedNoEvidence); // явный false — не негатив
    }

    [Fact]
    public void AutoTestCase_EmptyFactTerms_NotNegative()
    {
        // Семантика: пустой ExpectedFactTerms на обычном вопросе НЕ
        // означает «негативный» кейс. Негатив определяется только явным
        // флагом ExpectedNoEvidence.
        var normal = new AutoTestCase(
            Question: "Q",
            ExpectedSource: "pdf",
            ExpectedPdfPage: 1,
            ExpectedSectionContains: null,
            ExpectedFactTerms: Array.Empty<string>(),
            ExpectedNoEvidence: false);
        Assert.False(normal.ExpectedNoEvidence);

        var explicitNegative = normal with { ExpectedNoEvidence = true };
        Assert.True(explicitNegative.ExpectedNoEvidence);
    }

    [Fact]
    public void AutoTestRunResult_NegativeRag_SpuriousSourceFails()
    {
        // Негативный кейс в RAG-режиме должен проваливаться, если
        // поиск вернул хотя бы один источник — фактическая «ложная
        // находка» в индексе.
        var testCase = new AutoTestCase(
            Question: "Q",
            ExpectedSource: "",
            ExpectedPdfPage: null,
            ExpectedSectionContains: null,
            ExpectedFactTerms: null,
            ExpectedNoEvidence: true);

        var spurious = new[]
        {
            new RagSource("c1", "pdf", "T", "S", 1, "x", 0.1f),
        };
        var result = AutoTestRunnerHelpers.EvaluateRag(
            testCase,
            AutoTestRunResult.NoFactsAnswerPhrase,
            spurious);
        Assert.True(result.IsNegativeCase);
        Assert.False(result.Passed,
            "Негативный RAG с ненулевым Sources должен проваливаться.");
    }

    [Fact]
    public void AutoTestRunResult_NegativeRag_EmptySourcesAbstentionPasses()
    {
        // Негативный кейс в RAG-режиме проходит, если поиск успешен,
        // источников нет, и ответ содержит стандартную фразу abstention.
        var testCase = new AutoTestCase(
            Question: "Q",
            ExpectedSource: "",
            ExpectedPdfPage: null,
            ExpectedSectionContains: null,
            ExpectedFactTerms: null,
            ExpectedNoEvidence: true);

        var result = AutoTestRunnerHelpers.EvaluateRag(
            testCase,
            AutoTestRunResult.NoFactsAnswerPhrase,
            Array.Empty<RagSource>());
        Assert.True(result.IsNegativeCase);
        Assert.True(result.Passed);
    }

    [Fact]
    public void AutoTestRunResult_NegativeRag_FabricatedFactsFails()
    {
        // Негативный кейс в RAG-режиме проваливается, если LLM галлюцинирует
        // и приводит факты из ExpectedFactTerms.
        var testCase = new AutoTestCase(
            Question: "Q",
            ExpectedSource: "",
            ExpectedPdfPage: null,
            ExpectedSectionContains: null,
            ExpectedFactTerms: new[] { "foo" },
            ExpectedNoEvidence: true);

        var result = AutoTestRunnerHelpers.EvaluateRag(
            testCase,
            "Согласно нормам, foo составляет 45%.",
            Array.Empty<RagSource>());
        Assert.True(result.IsNegativeCase);
        Assert.False(result.Passed,
            "Негативный RAG с фактами в ответе должен проваливаться.");
    }

    [Fact]
    public void AutoTestRunResult_NegativeNoRag_AbstentionPasses()
    {
        // Негативный no-rag проходит, если модель воздержалась.
        var testCase = new AutoTestCase(
            Question: "Q",
            ExpectedSource: "",
            ExpectedPdfPage: null,
            ExpectedSectionContains: null,
            ExpectedFactTerms: null,
            ExpectedNoEvidence: true);

        var result = AutoTestRunnerHelpers.EvaluateNoRag(
            testCase,
            AutoTestRunResult.NoFactsAnswerPhrase);
        Assert.True(result.IsNegativeCase);
        Assert.True(result.Passed);
    }

    [Fact]
    public void AutoTestRunResult_NegativeNoRag_FactsAssertedFails()
    {
        // Негативный no-rag проваливается, если модель приводит факты.
        var testCase = new AutoTestCase(
            Question: "Q",
            ExpectedSource: "",
            ExpectedPdfPage: null,
            ExpectedSectionContains: null,
            ExpectedFactTerms: new[] { "влажность" },
            ExpectedNoEvidence: true);

        var result = AutoTestRunnerHelpers.EvaluateNoRag(
            testCase,
            "Влажность в офисе 45%.");
        Assert.True(result.IsNegativeCase);
        Assert.False(result.Passed);
    }

    [Fact]
    public void AutoTestRunResult_NormalEmptyTerms_FactsFound_Fails()
    {
        // Нормальный (не-негативный) вопрос с пустым ExpectedFactTerms
        // не становится негативным и должен проваливаться, если в ответе
        // есть контент (текущее поведение count == 0 → facts == 0).
        var testCase = new AutoTestCase(
            Question: "Q",
            ExpectedSource: "pdf",
            ExpectedPdfPage: 1,
            ExpectedSectionContains: null,
            ExpectedFactTerms: Array.Empty<string>(),
            ExpectedNoEvidence: false);

        var result = AutoTestRunnerHelpers.EvaluateNoRag(testCase, "любой текст");
        Assert.False(result.IsNegativeCase);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task RagAnswer_RewriteCancellation_PropagatesOperationCanceled()
    {
        // Отмена пользователя во время переписывания должна
        // пробрасываться как OperationCanceledException, а не
        // тихо подменяться фоллбэком на оригинальный вопрос.
        var indexPath = Path.Combine(_tempDir, "cancel.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c1", "pdf", "Doc", "S", "t", 1, MakeVector(vectorDim, 1f, 0f)),
        });

        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        // LLM зависает до отмены пользователя.
        var llm = new DelayedLlmClient(TimeSpan.FromSeconds(30));
        var options = MakeBaselineOptions(indexPath, preFilterK: 4, postFilterK: 2) with { RewriteEnabled = true };
        var service = new RagAnswerService(options, embeddings, llm);

        using var cts = new CancellationTokenSource();
        var askTask = service.AskAsync("Вопрос", options.ToEnhancedSettings(), cts.Token);
        // Дать LLM время стартовать.
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => askTask);
    }

    private static HttpClient MakeEmbeddingHttpForQuery(float[] expectedVector)
    {
        return new HttpClient(new StubHttpHandler(_ => HttpResponseFacts.Json(
            HttpStatusCode.OK,
            new { embeddings = new[] { expectedVector } })))
        {
            BaseAddress = new Uri("http://ollama.local"),
        };
    }

    /// <summary>
    /// HttpHandler для OllamaEmbeddingsClient, который возвращает успешный
    /// ответ ТОЛЬКО если текст в payload совпадает с <paramref name="expectText"/>.
    /// Иначе — 400. Записывает все присланные тексты в <c>embeddedTexts</c>.
    /// </summary>
    private static (HttpClient Http, List<string> EmbeddedTexts) MakeRecordingEmbeddingHttp(
        int vectorDim, float[] closeTo, string expectText)
    {
        var embedded = new List<string>();
        var handler = new RecordingEmbeddingHandler(embedded, expectText, closeTo);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.local") };
        return (http, embedded);
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

    private sealed record LlmCall(string Instructions, string UserMessage);

    /// <summary>Возвращает заранее заданные ответы по очереди и запоминает все вызовы.</summary>
    private sealed class RecordingLlmClient : ILlmClient
    {
        private readonly Queue<string> _replies;
        public List<LlmCall> Calls { get; } = new();

        public RecordingLlmClient(IEnumerable<string> replies)
        {
            _replies = new Queue<string>(replies);
        }

        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult(new TokenCountResult(0, false));

        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            var user = messages.LastOrDefault()?.Content ?? "";
            Calls.Add(new LlmCall(instructions, user));
            var reply = _replies.Count > 0 ? _replies.Dequeue() : "";
            return Task.FromResult(new LlmResponse(reply, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, false)));
        }
    }

    /// <summary>Кидает исключение на любой вызов LLM, чтобы проверить фоллбэк.</summary>
    private sealed class ThrowOnInstructionsLlmClient : ILlmClient
    {
        private readonly string _message;
        public ThrowOnInstructionsLlmClient(string message) => _message = message;
        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult(new TokenCountResult(0, false));
        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(_message);
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

    /// <summary>
    /// Читает тело POST /api/embed, достаёт первый input-текст и сравнивает с ожидаемым.
    /// Если совпадает — отвечает 200 с вектором, иначе — 400, чтобы эмбеддинг не прошёл.
    /// </summary>
    private sealed class RecordingEmbeddingHandler : HttpMessageHandler
    {
        private readonly List<string> _recorded;
        private readonly string _expected;
        private readonly float[] _vector;

        public RecordingEmbeddingHandler(List<string> recorded, string expected, float[] vector)
        {
            _recorded = recorded;
            _expected = expected;
            _vector = vector;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string? firstInput = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("input", out var input) &&
                    input.ValueKind == JsonValueKind.Array && input.GetArrayLength() > 0)
                {
                    firstInput = input[0].GetString();
                }
            }
            catch
            {
                // не JSON — трактуем как провал
            }

            if (firstInput is not null)
            {
                _recorded.Add(firstInput);
                if (firstInput == _expected)
                {
                    return HttpResponseFacts.Json(HttpStatusCode.OK, new { embeddings = new[] { _vector } });
                }
            }
            return HttpResponseFacts.Json(HttpStatusCode.BadRequest, new { error = $"ожидался текст '{_expected}', получен '{firstInput}'" });
        }
    }

    // ===== День 24: structured citations, abstention «Не знаю», production-настройки =====

    [Fact]
    public void RagCitationParser_ParsesStructuredAnswer()
    {
        var raw = """
            ANSWER: Влажность 45%.
            QUOTES:
            [c1] source=pdf section="Микроклимат" pdf_page=3 quote="Влажность 45%."
            [c2] source=confluence section="Климат" pdf_page=- quote="Норма 30-60%"
            """;
        var parsed = RagCitationParser.Parse(raw);
        Assert.True(parsed.HasStructuredOutput);
        Assert.Equal("Влажность 45%.", parsed.CleanAnswer);
        Assert.Equal(2, parsed.Citations.Count);
        Assert.Equal("c1", parsed.Citations[0].ChunkId);
        Assert.Equal("Микроклимат", parsed.Citations[0].Section);
        Assert.Equal(3, parsed.Citations[0].PdfPage);
        Assert.Equal("Влажность 45%.", parsed.Citations[0].Quote);
        Assert.Equal("confluence", parsed.Citations[1].Source);
        Assert.Null(parsed.Citations[1].PdfPage);
    }

    [Fact]
    public void RagCitationParser_ValidatesQuotesAgainstSources()
    {
        var raw = """
            ANSWER: Влажность 45%.
            QUOTES:
            [c1] source=pdf section="Микроклимат" pdf_page=3 quote="влажность офис 45%"
            [c-fake] source=pdf section="X" pdf_page=1 quote="несуществующий"
            """;
        var sources = new[]
        {
            new RagSource("c1", "pdf", "Doc", "Микроклимат", 3, "влажность офис 45%", 0.9f),
            new RagSource("c2", "pdf", "Doc", "Шум", 5, "55 дБ", 0.4f),
        };
        var validated = RagCitationParser.ValidateAgainstSources(
            RagCitationParser.Parse(raw), sources);
        Assert.True(validated.HasAnyVerifiedCitation);
        Assert.Equal(2, validated.Citations.Count);
        Assert.True(validated.Citations[0].Verified);
        Assert.False(validated.Citations[1].Verified);
    }

    [Fact]
    public void RagCitationParser_RejectsQuotesNotInSource()
    {
        var raw = """
            ANSWER: Текст.
            QUOTES:
            [c1] source=pdf section="X" pdf_page=1 quote="Совершенно другой текст"
            """;
        var sources = new[]
        {
            new RagSource("c1", "pdf", "Doc", "X", 1, "Содержимое чанка", 0.9f),
        };
        var validated = RagCitationParser.ValidateAgainstSources(
            RagCitationParser.Parse(raw), sources);
        Assert.False(validated.HasAnyVerifiedCitation);
    }

    [Fact]
    public void RagCitationParser_AnswerSupportedByQuotes()
    {
        var verified = new[]
        {
            new RagCitation("c1", "pdf", "S", 1, "влажность 45%", true),
        };
        Assert.True(RagCitationParser.AnswerSupportedByQuotes("влажность 45%", verified));
        Assert.False(RagCitationParser.AnswerSupportedByQuotes("высота стола 75 см", verified));
        // Пустой ответ не содержит подтверждённого утверждения.
        Assert.False(RagCitationParser.AnswerSupportedByQuotes("", verified));
        // Пустые цитаты — не поддержано.
        Assert.False(RagCitationParser.AnswerSupportedByQuotes("влажность 45%", Array.Empty<RagCitation>()));
    }

    [Fact]
    public void RagCitationParser_ParseRejectsLinesWithoutChunkId()
    {
        var raw = """
            ANSWER: Текст.
            QUOTES:
            source=pdf section="X" pdf_page=1 quote="no chunk id"
            """;
        var parsed = RagCitationParser.Parse(raw);
        Assert.Empty(parsed.Citations);
    }

    [Fact]
    public void RagCitationParser_ParseHandlesQuotedValuesWithSpaces()
    {
        var raw = """
            ANSWER: Текст
            QUOTES:
            [c1] source=pdf section="Раздел с пробелами" pdf_page=2 quote="дословная цитата с пробелами"
            """;
        var parsed = RagCitationParser.Parse(raw);
        Assert.Single(parsed.Citations);
        Assert.Equal("Раздел с пробелами", parsed.Citations[0].Section);
        Assert.Equal("дословная цитата с пробелами", parsed.Citations[0].Quote);
    }

    [Fact]
    public async Task RagAnswer_Day24_StructuredAnswer_HappyPath()
    {
        var indexPath = Path.Combine(_tempDir, "d24-happy.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c1", "pdf", "Doc", "Микроклимат", "влажность офис 45%", 1, MakeVector(vectorDim, 1f, 0f)),
        });
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        // LLM выдаёт структурированный ответ с цитатой.
        var structured = """
            ANSWER: Влажность 45%.
            QUOTES:
            [c1] source=pdf section="Микроклимат" pdf_page=1 quote="влажность офис 45%"
            """;
        var llm = new RecordingLlmClient(new[] { structured });
        var options = MakeBaselineOptions(indexPath, preFilterK: 4, postFilterK: 2);
        var service = new RagAnswerService(options, embeddings, llm);

        var result = await service.AskAsync("Какая влажность в офисе?", CancellationToken.None);

        Assert.True(result.SearchSucceeded);
        Assert.False(result.Abstained);
        Assert.Equal(RagAbstentionReason.None, result.AbstentionReason);
        Assert.Equal("Влажность 45%.", result.Answer);
        Assert.NotNull(result.Citations);
        Assert.Single(result.Citations!);
        Assert.True(result.Citations![0].Verified);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task RagAnswer_Day24_NoEvidence_AbstainsWithRussianPhrase()
    {
        var indexPath = Path.Combine(_tempDir, "d24-noev.sqlite3");
        const int vectorDim = 4;
        // Кандидат с отрицательным косинусом к запросу (1,0,0,0). При
        // production-настройках с порогом 0.9 отбрасывается → NoEvidence.
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c-low", "pdf", "Doc", "Шум", "55 дБ", 1, MakeVector(vectorDim, -1f, 0f)),
        });
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        // LLM не должен вызываться: после порога кандидатов нет.
        var llm = new ThrowOnInstructionsLlmClient("LLM не должен вызываться");
        var options = MakeBaselineOptions(indexPath, preFilterK: 1, postFilterK: 1)
            with { ScoreThreshold = 0.9f };
        var service = new RagAnswerService(options, embeddings, llm);
        // Передаём production-настройки с порогом явно.
        var production = options.ToProductionSettings();

        var result = await service.AskAsync(
            "Какая влажность в офисе?", production, CancellationToken.None);

        Assert.True(result.SearchSucceeded);
        Assert.Empty(result.Sources);
        Assert.True(result.Abstained);
        Assert.Equal(RagAbstentionReason.NoEvidence, result.AbstentionReason);
        Assert.Equal(RagAbstentionMessages.Russian, result.Answer);
        Assert.NotNull(result.ClarificationQuestion);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task RagAnswer_Day24_SearchError_AbstainsAsSearchError()
    {
        var indexPath = Path.Combine(_tempDir, "absent.sqlite3");
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(new float[0]),
            TimeSpan.FromSeconds(2), 0);
        var llm = new ThrowOnInstructionsLlmClient("LLM не должен вызываться");
        var options = new RagOptions(
            IndexPath: indexPath,
            OllamaUrl: "http://ollama.local",
            EmbedModel: "qwen3-embedding:0.6b",
            TopK: 2,
            MaxContextChars: 1000,
            EmbedTimeout: TimeSpan.FromSeconds(1),
            EmbedMaxRetries: 0,
            LlmTimeout: TimeSpan.FromSeconds(1));
        var service = new RagAnswerService(options, embeddings, llm);

        var result = await service.AskAsync("Любой вопрос", CancellationToken.None);

        Assert.False(result.SearchSucceeded);
        Assert.True(result.Abstained);
        Assert.Equal(RagAbstentionReason.SearchError, result.AbstentionReason);
        Assert.Equal(RagAbstentionMessages.Russian, result.Answer);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RagAnswer_Day24_FabricatedQuote_AbstainsAsUnsupportedAnswer()
    {
        var indexPath = Path.Combine(_tempDir, "d24-fake.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c1", "pdf", "Doc", "Микроклимат", "влажность офис 45%", 1, MakeVector(vectorDim, 1f, 0f)),
        });
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        // LLM выдаёт цитату, которой нет в источнике, и ответ, не
        // подтверждённый цитатами. Сервис обязан abstentionить.
        var fake = """
            ANSWER: Влажность 99%.
            QUOTES:
            [c1] source=pdf section="Микроклимат" pdf_page=1 quote="совершенно другой текст"
            """;
        var llm = new RecordingLlmClient(new[] { fake });
        var options = MakeBaselineOptions(indexPath, preFilterK: 2, postFilterK: 2);
        var service = new RagAnswerService(options, embeddings, llm);

        var result = await service.AskAsync("Какая влажность?", CancellationToken.None);

        Assert.True(result.SearchSucceeded);
        Assert.Single(result.Sources);
        Assert.True(result.Abstained);
        Assert.Equal(RagAbstentionReason.UnsupportedAnswer, result.AbstentionReason);
        Assert.Equal(RagAbstentionMessages.Russian, result.Answer);
        Assert.NotNull(result.Citations);
        Assert.Single(result.Citations!);
        Assert.False(result.Citations![0].Verified);
    }

    [Fact]
    public async Task RagAnswer_Day24_LlmError_AbstainsAsLlmError()
    {
        var indexPath = Path.Combine(_tempDir, "d24-llm.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c1", "pdf", "Doc", "Микроклимат", "влажность офис 45%", 1, MakeVector(vectorDim, 1f, 0f)),
        });
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new ThrowOnInstructionsLlmClient("boom");
        var options = MakeBaselineOptions(indexPath, preFilterK: 2, postFilterK: 2);
        var service = new RagAnswerService(options, embeddings, llm);

        var result = await service.AskAsync("Какая влажность?", CancellationToken.None);

        Assert.True(result.SearchSucceeded);
        Assert.True(result.Abstained);
        Assert.Equal(RagAbstentionReason.LlmError, result.AbstentionReason);
        Assert.Equal(RagAbstentionMessages.Russian, result.Answer);
        Assert.NotNull(result.Error);
        Assert.Contains("boom", result.Error!);
    }

    [Fact]
    public async Task RagAnswer_Day24_AnswerSupportedByVerifiedQuotes()
    {
        var indexPath = Path.Combine(_tempDir, "d24-support.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c1", "pdf", "Doc", "Микроклимат", "влажность 45 процентов", 1, MakeVector(vectorDim, 1f, 0f)),
        });
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        // Ответ использует термин из цитаты → поддержан.
        var structured = """
            ANSWER: влажность 45 процентов.
            QUOTES:
            [c1] source=pdf section="Микроклимат" pdf_page=1 quote="влажность 45 процентов"
            """;
        var llm = new RecordingLlmClient(new[] { structured });
        var options = MakeBaselineOptions(indexPath, preFilterK: 2, postFilterK: 2);
        var service = new RagAnswerService(options, embeddings, llm);

        var result = await service.AskAsync("Какая влажность?", CancellationToken.None);

        Assert.False(result.Abstained);
        Assert.True(result.Citations![0].Verified);
        Assert.NotEmpty(result.Citations!);
    }

    [Fact]
    public void RagOptions_ProductionSettings_HasThresholdFromOptions()
    {
        // День 24: production-настройки для чата используют score threshold,
        // чтобы продовый чат не отвечал на слабом контексте.
        var options = new RagOptions(
            IndexPath: Path.Combine(_tempDir, "x.sqlite3"),
            OllamaUrl: "http://x",
            EmbedModel: "m",
            TopK: 4,
            MaxContextChars: 1000,
            EmbedTimeout: TimeSpan.FromSeconds(1),
            EmbedMaxRetries: 0,
            LlmTimeout: TimeSpan.FromSeconds(1),
            RewriteEnabled: false,
            ScoreThreshold: 0.65f,
            PreFilterK: 8,
            PostFilterK: 4);
        var production = options.ToProductionSettings();
        Assert.False(production.RewriteEnabled);
        Assert.Equal(0.65f, production.ScoreThreshold);
        Assert.Equal(8, production.PreFilterK);
        Assert.Equal(4, production.PostFilterK);

        // legacy-baseline остаётся без threshold.
        var legacy = options.ToBaselineSettings();
        Assert.Equal(-1f, legacy.ScoreThreshold);
        Assert.Equal(4, legacy.PreFilterK); // legacy → TopK
    }

    [Fact]
    public async Task RagQueryService_DefaultForRag_UsesThreshold()
    {
        // Прод-режим (RagMode.Rag) использует ToProductionSettings, чтобы
        // чат не отвечал по слабому контексту.
        var indexPath = Path.Combine(_tempDir, "facade-prod.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c-low", "pdf", "Doc", "S", "шум", 1, MakeVector(vectorDim, 0.1f, 1f)),
        });
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new RecordingLlmClient(new[] { "ok" });
        var options = MakeBaselineOptions(indexPath, preFilterK: 4, postFilterK: 2)
            with { ScoreThreshold = 0.9f };
        var facade = new RagQueryService(
            new RagAnswerService(options, embeddings, llm),
            new NoRagAnswerService(new StubLlmClient("n/a"), TimeSpan.FromSeconds(1)),
            options);

        var result = await facade.QueryAsync("Вопрос", RagMode.Rag);
        Assert.True(result.Abstained);
        Assert.Equal(RagAbstentionReason.NoEvidence, result.AbstentionReason);
        Assert.Equal(0.9f, result.Settings!.ScoreThreshold);
    }

    [Fact]
    public async Task RagQueryService_LegacyBaseline_KeepsNoThreshold()
    {
        // Comparison-режим использует legacy-baseline без threshold.
        var indexPath = Path.Combine(_tempDir, "facade-legacy.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c-neg", "pdf", "Doc", "Noise", "irrelevant", 1, MakeVector(vectorDim, -1f, 0f)),
        });
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new RecordingLlmClient(new[]
        {
            """
            ANSWER: irrelevant
            QUOTES:
            [c-neg] source=pdf section="Noise" pdf_page=1 quote="irrelevant"
            """,
        });
        var options = MakeBaselineOptions(indexPath, preFilterK: 1, postFilterK: 1)
            with { ScoreThreshold = 0.99f };
        var facade = new RagQueryService(
            new RagAnswerService(options, embeddings, llm),
            new NoRagAnswerService(new StubLlmClient("n/a"), TimeSpan.FromSeconds(1)),
            options);

        var legacy = facade.LegacyBaselineSettings();
        Assert.Equal(-1f, legacy.ScoreThreshold);
        var result = await facade.QueryAsync("Вопрос", RagMode.Rag, legacy);
        // Legacy-baseline сохраняет отрицательный косинус-кандидат и не abstentionит.
        Assert.False(result.Abstained);
        Assert.Equal(1, result.RetrievedCount);
        Assert.Equal(-1f, result.Settings!.ScoreThreshold);
    }

    [Fact]
    public void RagCitation_Reference_FormatsSourceAndPage()
    {
        var pdf = new RagCitation("c1", "pdf", "S", 5, "q", true);
        Assert.Equal("[pdf, стр. 5, c1]", pdf.Reference);
        var conf = new RagCitation("c1", "confluence", "S", null, "q", true);
        Assert.Equal("[confluence, c1]", conf.Reference);
    }

    [Fact]
    public void RagAbstentionMessages_AreStable()
    {
        // Стандартные тексты abstention не должны меняться — UI и
        // автотест опираются на дословные строки.
        Assert.Equal("Не знаю", RagAbstentionMessages.Russian);
        Assert.False(string.IsNullOrWhiteSpace(RagAbstentionMessages.DefaultClarification));
    }

    [Fact]
    public void AutoTestRunResult_QuotesAcceptance_DefaultsFalse()
    {
        // Обратная совместимость: новые поля имеют дефолт false.
        var r = new AutoTestRunResult(
            Index: 1, Mode: "x", Question: "q",
            ExpectedSource: "", ExpectedPdfPage: null, ExpectedSectionContains: null,
            ExpectedNoEvidence: false,
            SearchSucceeded: true, RetrievedCount: 1, Sources: Array.Empty<RagSource>(),
            Answer: "a", ExpectedFactTerms: Array.Empty<string>(),
            FoundFactTerms: Array.Empty<string>(),
            FactCheckPassed: true, SourceCheckPassed: true, SectionCheckPassed: true,
            Error: null);
        Assert.False(r.QuoteCheckPassed);
        Assert.False(r.AnswerSupportedByQuotes);
        Assert.False(r.Abstained);
        Assert.Equal(RagAbstentionReason.None, r.AbstentionReason);
        Assert.Empty(r.CitationsSafe);
    }

    [Fact]
    public void AutoTestRunResult_Day24AbstentionPhrase_InIsAbstentionAnswer()
    {
        // IsAbstentionAnswer должен учитывать новую фразу «Не знаю».
        var r = new AutoTestRunResult(
            Index: 1, Mode: "x", Question: "q",
            ExpectedSource: "", ExpectedPdfPage: null, ExpectedSectionContains: null,
            ExpectedNoEvidence: false,
            SearchSucceeded: true, RetrievedCount: 1, Sources: Array.Empty<RagSource>(),
            Answer: "Не знаю", ExpectedFactTerms: Array.Empty<string>(),
            FoundFactTerms: Array.Empty<string>(),
            FactCheckPassed: false, SourceCheckPassed: true, SectionCheckPassed: true,
            Error: null);
        Assert.True(r.IsAbstentionAnswer);
    }

    [Fact]
    public async Task NoRagAnswerService_AbstainsForDocumentQuestions()
    {
        // Без RAG модель обязана abstentionить на вопросы про документ/страницу.
        var llm = new StubLlmClient("Не знаю. Уточните, пожалуйста, вопрос.");
        var service = new NoRagAnswerService(llm, TimeSpan.FromSeconds(1));
        var result = await service.AskAsync("Что на стр. 5?", CancellationToken.None);
        Assert.True(result.Abstained);
        Assert.Equal(RagAbstentionReason.NoEvidence, result.AbstentionReason);
        Assert.NotNull(result.ClarificationQuestion);
    }

    [Fact]
    public async Task NoRagAnswerService_LlmError_NotAbstention()
    {
        var service = new NoRagAnswerService(new ThrowingLlmClient(), TimeSpan.FromSeconds(1));
        var result = await service.AskAsync("Любой вопрос", CancellationToken.None);
        Assert.False(result.Abstained);
        Assert.Equal(RagAbstentionReason.LlmError, result.AbstentionReason);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AutoTestRunner_PopulatesCitationsAndAbstention_RagMode()
    {
        // День 24: AutoTestRunResult должен передавать цитаты и флаги
        // abstention из RagAnswerService. Доказательство из реального
        // сервиса, не из заглушки.
        var indexPath = Path.Combine(_tempDir, "autotest-citations.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c1", "pdf", "Doc", "Микроклимат", "влажность 45%", 1, MakeVector(vectorDim, 1f, 0f)),
        });
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new RecordingLlmClient(new[]
        {
            """
            ANSWER: влажность 45%
            QUOTES:
            [c1] source=pdf section="Микроклимат" pdf_page=1 quote="влажность 45%"
            """,
        });
        var options = MakeBaselineOptions(indexPath, preFilterK: 4, postFilterK: 2);
        var rag = new RagAnswerService(options, embeddings, llm);
        var noRag = new NoRagAnswerService(new StubLlmClient("n/a"), TimeSpan.FromSeconds(1));
        var runner = new AutoTestRunner(noRag, rag, Path.Combine(_tempDir, "unused.json"));

        var cases = new List<AutoTestCase>
        {
            new(
                Question: "Вопрос",
                ExpectedSource: "pdf",
                ExpectedPdfPage: 1,
                ExpectedSectionContains: "Микроклимат",
                ExpectedFactTerms: new[] { "влажность" },
                ExpectedNoEvidence: false),
        };
        var report = await runner.RunAggregateAsync(
            cases,
            baseline: options.ToBaselineSettings(),
            enhanced: null,
            includeNoRag: false,
            progress: null,
            cancellationToken: CancellationToken.None);

        var baselineRun = report.Runs.Single();
        Assert.NotNull(baselineRun.Citations);
        Assert.Single(baselineRun.Citations!);
        Assert.True(baselineRun.Citations![0].Verified);
        Assert.True(baselineRun.QuoteCheckPassed);
        Assert.True(baselineRun.AnswerSupportedByQuotes);
        Assert.False(baselineRun.Abstained);
    }

    [Fact]
    public async Task AutoTestRunner_Day24_Abstain_RagNegativeCase()
    {
        // Автотест: RAG-режим с негативным кейсом abstentionит «Не знаю»
        // и помечается как пройденный.
        var indexPath = Path.Combine(_tempDir, "autotest-noev.sqlite3");
        const int vectorDim = 4;
        // Единственный кандидат — с отрицательным косинусом к запросу
        // (1,0,0,0). При production-настройках (с порогом) отбрасывается.
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c-low", "pdf", "Doc", "Шум", "55 дБ", 1, MakeVector(vectorDim, -1f, 0f)),
        });
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        var llm = new ThrowOnInstructionsLlmClient("LLM не должен вызываться");
        var options = MakeBaselineOptions(indexPath, preFilterK: 1, postFilterK: 1)
            with { ScoreThreshold = 0.9f };
        var rag = new RagAnswerService(options, embeddings, llm);
        var noRag = new NoRagAnswerService(new StubLlmClient("Не знаю."), TimeSpan.FromSeconds(1));
        var runner = new AutoTestRunner(noRag, rag, Path.Combine(_tempDir, "unused.json"));

        var cases = new List<AutoTestCase>
        {
            new(
                Question: "Неизвестный вопрос",
                ExpectedSource: "",
                ExpectedPdfPage: null,
                ExpectedSectionContains: null,
                ExpectedFactTerms: null,
                ExpectedNoEvidence: true),
        };
        var report = await runner.RunAggregateAsync(
            cases,
            baseline: options.ToProductionSettings(),
            enhanced: null,
            includeNoRag: false,
            progress: null,
            cancellationToken: CancellationToken.None);

        var run = report.Runs.Single();
        Assert.True(run.IsNegativeCase);
        Assert.True(run.Abstained);
        Assert.Equal(RagAbstentionReason.NoEvidence, run.AbstentionReason);
        Assert.Equal(RagAbstentionMessages.Russian, run.Answer);
        Assert.True(run.Passed, "Негативный RAG-кейс с NoEvidence должен пройти.");
    }

    [Fact]
    public async Task AutoTestRunner_Day24_StlbOk1_MapsOnlyToRevit703()
    {
        // День 24: STLB-OK1 — это проект, связанный ТОЛЬКО с revit-703
        // (по Confluence-таблице). Source содержит идентификатор «revit-703»
        // в section/chunk, а цитата содержит дословный фрагмент проект/сервер.
        // Ожидаем, что:
        //   - источник найден и проверен;
        //   - chunk_id ссылается на revit-703 (не на revit-702/704);
        //   - source=confluence, что соответствует таблице Confluence.
        var indexPath = Path.Combine(_tempDir, "stlb-ok1.sqlite3");
        const int vectorDim = 4;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("revit-702", "confluence", "Серверы", "revit-702", "Проекты других годов", 1, MakeVector(vectorDim, 0.1f, 1f)),
            ("revit-703", "confluence", "Серверы", "revit-703", "Projects 2022: STLB-OK1; server revit-703", 2, MakeVector(vectorDim, 1f, 0f)),
            ("revit-704", "confluence", "Серверы", "revit-704", "Проекты других годов", 3, MakeVector(vectorDim, 0.5f, 0.5f)),
        });
        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        var structured = """
            ANSWER: STLB-OK1 находится на revit-703.
            QUOTES:
            [revit-703] source=confluence section="revit-703" pdf_page=2 quote="Projects 2022: STLB-OK1; server revit-703"
            """;
        var llm = new RecordingLlmClient(new[] { structured });
        var options = MakeBaselineOptions(indexPath, preFilterK: 3, postFilterK: 3);
        var rag = new RagAnswerService(options, embeddings, llm);
        var noRag = new NoRagAnswerService(new StubLlmClient("Не знаю."), TimeSpan.FromSeconds(1));
        var runner = new AutoTestRunner(noRag, rag, Path.Combine(_tempDir, "unused.json"));

        var cases = new List<AutoTestCase>
        {
            new(
                Question: "На каком сервере STLB-OK1?",
                ExpectedSource: "confluence",
                ExpectedPdfPage: 2,
                ExpectedSectionContains: "revit-703",
                ExpectedFactTerms: new[] { "revit-703", "STLB-OK1" },
                ExpectedNoEvidence: false),
        };
        var report = await runner.RunAggregateAsync(
            cases,
            baseline: options.ToBaselineSettings(),
            enhanced: null,
            includeNoRag: false,
            progress: null,
            cancellationToken: CancellationToken.None);

        var run = report.Runs.Single();
        Assert.True(run.SearchSucceeded);
        Assert.NotEmpty(run.Sources);
        // STLB-OK1 связан только с revit-703: в результатах поиска
        // revit-703 должен присутствовать, а chunk_id цитаты ссылается
        // именно на revit-703 (не на revit-702/704).
        Assert.Contains(run.Sources, s => s.ChunkId == "revit-703");
        Assert.Single(run.CitationsSafe);
        Assert.Equal("revit-703", run.CitationsSafe[0].ChunkId);
        Assert.NotEqual("revit-702", run.CitationsSafe[0].ChunkId);
        Assert.NotEqual("revit-704", run.CitationsSafe[0].ChunkId);
        Assert.True(run.QuoteCheckPassed);
        Assert.True(run.AnswerSupportedByQuotes);
        // Версия Revit НЕ заявляется из Projects 2022: в ответе есть «revit-703»,
        // но не должно быть «2022» как версии Revit (проекты года ≠ версия Revit).
        Assert.DoesNotContain("Revit 2022", run.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revit-703", run.Answer, StringComparison.OrdinalIgnoreCase);
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
            ExpectedNoEvidence: testCase.ExpectedNoEvidence,
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
        // SearchSucceeded моделирует фактическое поведение RagAnswerService:
        // поиск успешен, если индекс открыт и вернул результат без ошибок —
        // в т.ч. когда результаты пустые (валидный «нет находок»).
        var searchSucceeded = true;
        return new AutoTestRunResult(
            Index: 0,
            Mode: "rag",
            Question: testCase.Question,
            ExpectedSource: testCase.ExpectedSource,
            ExpectedPdfPage: testCase.ExpectedPdfPage,
            ExpectedSectionContains: testCase.ExpectedSectionContains,
            ExpectedNoEvidence: testCase.ExpectedNoEvidence,
            SearchSucceeded: searchSucceeded,
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
