using System.Text;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Clodlogs.Desktop.Models;
using Clodlogs.Desktop.Services;
using Clodlogs.Desktop.ViewModels;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("response_item transcript parsing", TestResponseItemTranscriptAsync),
    ("markdown export response items and images", TestMarkdownExportResponseItemsAndImagesAsync),
    ("html export inlines response item images", TestHtmlExportInlineImagesAsync),
    ("native Claude export honors tool options without duplicates", TestNativeClaudeToolOptionsAsync),
    ("native Claude transcript preserves block order and string messages", TestNativeClaudeTranscriptAsync),
    ("native Claude images honor image and tool options", TestNativeClaudeImageOptionsAsync),
    ("api request rows stay out of transcripts, exports and tool counts", TestApiRequestRowsAreNotTranscriptAsync),
    ("opencode export maps prompts, responses, tool calls and turn ends", TestOpenCodeExportTranscriptAsync),
    ("opencode export leaves out images and tool calls unless requested", TestOpenCodeExportOptionsAsync),
    ("opencode export marks interrupted and failed turns, injected text and compaction", TestOpenCodeExportTurnsAsync),
    ("opencode export embeds only valid inline images", TestOpenCodeExportImagesAsync),
    ("opencode batch export names files and repeats identifiers", TestOpenCodeBatchExportAsync),
    ("opencode identifiers follow the layout of OpenCode 2.3", TestOpenCodeIdentifiers),
    ("single export avoids automatic filename collisions", TestSingleExportCollisionsAsync),
    ("failed export preserves existing output and reports image I/O errors", TestFailedExportPreservesOutputAsync),
    ("image asset names and links handle collisions and special characters", TestImageAssetLinksAsync),
    ("single export rejects unsupported formats", TestInvalidExportFormat),
    ("explicit export replacement preserves unrelated assets", TestExplicitExportReplacementAsync),
    ("cancelled export preserves previous output and assets", TestExportCancellationAsync),
    ("image URLs and invalid image data are handled consistently", TestImageSourcesAsync),
    ("batch export names files and avoids collisions", TestBatchExportNamingAndCollisionAsync),
    ("batch export reserves around external asset collisions", TestBatchExportAssetCollisionsAsync),
    ("batch path reservation preserves cancellation and I/O failures", TestBatchPathReservationFailures),
    ("batch export continues after a session failure", TestBatchExportPartialFailureAsync),
    ("legacy settings migrate once under concurrent reads", TestLegacySettingsMigrationAsync),
    ("corrupt current settings remain untouched", TestCorruptSettingsRemainUntouchedAsync),
    ("failed legacy migration retries", TestFailedLegacyMigrationRetriesAsync),
    ("settings persistence failure does not prevent startup", TestSettingsPersistenceFailureAsync),
    ("session scan returns partial results after cancellation", TestPartialSessionScanAsync),
    ("session probe preserves cancellation", TestSessionProbeCancellationAsync),
    ("sanitized copy strips response item images", TestSanitizedCopyAsync),
    ("token usage separates cache and calculates costs", TestTokenUsagePricingAsync),
    ("anthropic pricing parser maps cache read and output", TestAnthropicPricingParser),
    ("token summary exports csv and markdown", TestTokenSummaryExports),
    ("session scan reads many files and keeps every session", TestParallelSessionScanAsync),
    ("session list prefers the title set by the user", TestSessionTitlesAsync),
    ("refresh scan reports progress and clears stale results", TestRefreshScanReportsProgressAsync),
    // Keep this one last: it installs Avalonia's synchronization context, which no message loop serves here.
    ("native rendering packages shape text and encode PNG through Avalonia", TestNativeRendering)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static Task TestNativeRendering()
{
    AppBuilder.Configure<Clodlogs.Desktop.App>()
        .UseStandardRuntimePlatformSubsystem()
        .UseWindowingSubsystem(() => { }, "Offscreen")
        .UseSkia()
        .UseHarfBuzz()
        .WithInterFont()
        .SetupWithoutStarting();

    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
        new PixelSize(320, 80), new Vector(96, 96));
    var text = new Avalonia.Media.FormattedText(
        "clodlogs – Grüße fi",
        System.Globalization.CultureInfo.InvariantCulture,
        Avalonia.Media.FlowDirection.LeftToRight,
        Avalonia.Media.Typeface.Default,
        24,
        Avalonia.Media.Brushes.Black);
    AssertTrue(text.Width > 0 && text.Height > 0, "native text shaping produces metrics");

    using (var context = bitmap.CreateDrawingContext())
    {
        context.DrawRectangle(Avalonia.Media.Brushes.White, null, new Rect(0, 0, 320, 80));
        context.DrawText(text, new Point(10, 10));
    }

    using var png = new MemoryStream();
    bitmap.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    using var decoded = SkiaSharp.SKBitmap.Decode(png.ToArray());
    AssertTrue(decoded is not null, "native PNG encoding and decoding succeeds");
    AssertEqual(320, decoded!.Width, "rendered image width");
    AssertEqual(80, decoded.Height, "rendered image height");
    AssertTrue(decoded.Pixels.Any(pixel => pixel.Red < 200 && pixel.Green < 200 && pixel.Blue < 200),
        "rendered image contains visible text pixels");

    return Task.CompletedTask;
}

static async Task TestResponseItemTranscriptAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"session_meta","timestamp":"2026-03-16T12:00:00.000Z","payload":{"id":"session-a","timestamp":"2026-03-16T12:00:00.000Z","cwd":"C:\\repo","originator":"claude_code","source":"claude_code","model_provider":"anthropic"}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:01.000Z","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:02.000Z","payload":{"type":"function_call","name":"read_file","arguments":"{\"path\":\"README.md\"}"}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:03.000Z","payload":{"type":"function_call_output","call_id":"call-1","output":"done"}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:04.000Z","payload":{"type":"custom_tool_call","name":"shell","status":"completed","input":"ls"}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:05.000Z","payload":{"type":"reasoning","summary":[{"text":"checked the fixture"}]}}
        """);

    var service = new ClaudeSessionService();
    var transcript = await service.ReadSessionTranscriptAsync(sessionPath);

    AssertEqual(5, transcript.Entries.Count, "entry count");
    AssertEqual("User", transcript.Entries[0].Title, "message title");
    AssertEqual(SessionTranscriptEntryKind.ToolCall, transcript.Entries[1].Kind, "function call kind");
    AssertEqual(SessionTranscriptEntryKind.ToolOutput, transcript.Entries[2].Kind, "function output kind");
    AssertEqual(SessionTranscriptEntryKind.CustomToolCall, transcript.Entries[3].Kind, "custom tool kind");
    AssertEqual(SessionTranscriptEntryKind.Reasoning, transcript.Entries[4].Kind, "reasoning kind");
}

static async Task TestTokenUsagePricingAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"assistant","timestamp":"2026-07-14T10:15:00.000Z","uuid":"usage-row","message":{"id":"msg-usage","model":"claude-sonnet-4-5-20250929","usage":{"input_tokens":1000000,"cache_creation_input_tokens":2000000,"cache_creation":{"ephemeral_5m_input_tokens":1500000,"ephemeral_1h_input_tokens":500000},"cache_read_input_tokens":3000000,"output_tokens":4000000,"output_tokens_details":{"thinking_tokens":250000}}}}
        """, "token-usage.jsonl");

    var service = new ClaudeSessionService();
    var usage = await service.ReadSessionTokenUsageAsync(sessionPath);

    AssertTrue(usage.TokenUsage is not null, "session usage exists");
    AssertEqual(1_000_000L, usage.TokenUsage!.InputTokens, "input tokens");
    AssertEqual(2_000_000L, usage.TokenUsage.CacheCreationInputTokens, "cache write tokens");
    AssertEqual(1_500_000L, usage.TokenUsage.CacheCreation5MinuteInputTokens, "5-minute cache write tokens");
    AssertEqual(500_000L, usage.TokenUsage.CacheCreation1HourInputTokens, "1-hour cache write tokens");
    AssertEqual(3_000_000L, usage.TokenUsage.CacheReadInputTokens, "cache read tokens");
    AssertEqual(4_000_000L, usage.TokenUsage.OutputTokens, "output tokens");
    AssertEqual(250_000L, usage.TokenUsage.ReasoningOutputTokens, "thinking tokens");
    AssertEqual(10_000_000L, usage.TokenUsage.TotalTokens, "total tokens");

    var job = service.StartTokenUsageSummaryJob([sessionPath], AnthropicPricingService.DefaultPricing());
    var status = await WaitForTokenSummaryAsync(() => service.GetTokenUsageSummaryJobStatus(job.JobId));

    AssertEqual("success", status.Kind, "token summary status");
    AssertTrue(status.Result?.CostBreakdown is not null, "cost breakdown exists");
    AssertEqual(72.525m, status.Result!.CostBreakdown!.TotalCost, "estimated cost");
    AssertEqual(1, status.Result.DailyBreakdown.Count, "daily rows");
    AssertEqual(1, status.Result.ModelBreakdown.Count, "model rows");
}

static Task TestAnthropicPricingParser()
{
    const string pricingDocument = """
        | Model | Base Input Tokens | 5m Cache Writes | 1h Cache Writes | Cache Hits & Refreshes | Output Tokens |
        | --- | --- | --- | --- | --- | --- |
        | Claude Sonnet 4.5 | $3 / MTok | $3.75 / MTok | $6 / MTok | $0.30 / MTok | $15 / MTok |
        """;

    var pricing = AnthropicPricingService.ParsePricingDocument(pricingDocument);
    AssertEqual(1, pricing.Models.Count, "parsed model count");
    AssertEqual("Claude Sonnet 4.5", pricing.Models[0].Model, "parsed model name");
    AssertEqual(3.75m, pricing.Models[0].CacheWrite5MinutePerMillionTokens, "5-minute cache write price");
    AssertEqual(6m, pricing.Models[0].CacheWrite1HourPerMillionTokens, "1-hour cache write price");
    AssertEqual(0.30m, pricing.Models[0].CacheReadPerMillionTokens, "cache read price");
    AssertEqual(15m, pricing.Models[0].OutputPerMillionTokens, "output price");
    AssertEqual<AnthropicModelPrice?>(null, AnthropicPricingService.FindPrice(pricing, "claude-3-5-sonnet-20241022"), "older model stays unpriced");
    return Task.CompletedTask;
}

static Task TestTokenSummaryExports()
{
    var usage = new SessionTokenUsage(1000, 200, 300, 400, 500, 100, 2400);
    var cost = new TokenUsageCostBreakdown(
        0.003m,
        0.004m,
        0.00012m,
        0.0075m,
        0.01462m,
        1,
        0,
        AnthropicPricingService.PricingSourceUrl,
        "2026-07-31T10:00:00.0000000Z");
    var summary = new TokenUsageSummaryResult(
        1,
        1,
        1,
        0,
        0,
        123,
        0,
        1,
        usage,
        cost,
        [new TokenUsageDailyBreakdown(new DateOnly(2026, 7, 14), usage, cost.TotalCost)],
        [new TokenUsageModelBreakdown("Claude Sonnet 4.5", usage, cost.TotalCost)]);
    var service = new ClaudeSessionService();

    var csv = service.FormatTokenUsageSummaryAsCsv(summary);
    AssertContains("\"Section\",\"Date\",\"Model\"", csv, "csv header");
    AssertContains("\"Daily\",\"2026-07-14\",\"\"", csv, "csv daily row");
    AssertContains("\"Model\",\"\",\"Claude Sonnet 4.5\"", csv, "csv model row");
    AssertContains("\"0.01462\"", csv, "csv invariant cost");

    var markdown = service.FormatTokenUsageSummaryAsMarkdown(summary);
    AssertContains("# Token usage summary", markdown, "markdown title");
    AssertContains("| 2026-07-14 |", markdown, "markdown daily row");
    AssertContains("| Claude Sonnet 4.5 |", markdown, "markdown model row");
    AssertContains(AnthropicPricingService.PricingSourceUrl, markdown, "markdown pricing source");
    return Task.CompletedTask;
}

static async Task TestMarkdownExportResponseItemsAndImagesAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"session_meta","timestamp":"2026-03-16T12:00:00.000Z","payload":{"id":"session-b","timestamp":"2026-03-16T12:00:00.000Z","cwd":"C:\\repo","originator":"claude_code","source":"claude_code","model_provider":"anthropic"}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:01.000Z","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hello with image"},{"type":"input_image","image_url":"data:image/png;base64,aGVsbG8=","alt_text":"tiny"}]}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:02.000Z","payload":{"type":"function_call","name":"read_file","arguments":"{\"path\":\"README.md\"}"}}
        """);

    var outputDirectory = Path.Combine(fixture.Root, "export");
    Directory.CreateDirectory(outputDirectory);
    var service = new ClaudeSessionService();
    var job = service.StartExportJob("markdown", sessionPath, includeImages: true, inlineImages: false, includeToolCallResults: true, outputDirectory, outputPath: null);
    var status = await WaitForExportAsync(() => service.GetExportJobStatus(job.JobId));

    AssertEqual("success", status.Kind, "export status");
    AssertTrue(File.Exists(status.OutputPath), "markdown output exists");
    var markdown = await File.ReadAllTextAsync(status.OutputPath!, Encoding.UTF8);
    AssertContains("![tiny](./sample-assets/image-001.png)", markdown, "markdown image reference");
    AssertContains("Tool Call: read_file", markdown, "tool call export");
    AssertTrue(File.Exists(Path.Combine(outputDirectory, "sample-assets", "image-001.png")), "image asset exists");
}

static string WriteNativeClaudeSession(TempFixture fixture)
    => fixture.WriteSession("""
        {"type":"user","sessionId":"native-session","cwd":"C:\\repo","timestamp":"2026-09-14T10:00:00Z","message":{"role":"user","content":"STRING-USER"}}
        {"type":"assistant","timestamp":"2026-09-14T10:00:01Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"THINKING-MARKER"},{"type":"text","text":"BEFORE-TOOL"},{"type":"tool_use","id":"call-1","name":"shell","input":{"command":"TOOL-INPUT-MARKER"}},{"type":"text","text":"AFTER-TOOL"}]}}
        {"type":"user","timestamp":"2026-09-14T10:00:02Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"call-1","content":[{"type":"text","text":"TOOL-OUTPUT-MARKER"}]}]}}
        {"type":"assistant","timestamp":"2026-09-14T10:00:03Z","message":{"role":"assistant","content":[{"type":"text","text":"FINAL-ANSWER"}]}}
        """);

static async Task TestNativeClaudeToolOptionsAsync()
{
    foreach (var format in new[] { "markdown", "html" })
    foreach (var batch in new[] { false, true })
    foreach (var includeTools in new[] { false, true })
    {
        using var fixture = new TempFixture();
        var sessionPath = WriteNativeClaudeSession(fixture);
        var outputDirectory = Path.Combine(fixture.Root, "export");
        var service = new ClaudeSessionService();
        string outputPath;
        if (batch)
        {
            var job = service.StartBatchExportJob(format, [new(sessionPath, "Native", null)], false, false, includeTools, outputDirectory);
            var status = await WaitForBatchExportAsync(() => service.GetBatchExportJobStatus(job.JobId));
            AssertEqual("success", status.Kind, "native batch export");
            outputPath = Directory.GetFiles(outputDirectory).Single();
        }
        else
        {
            var job = service.StartExportJob(format, sessionPath, false, false, includeTools, outputDirectory, null);
            var status = await WaitForExportAsync(() => service.GetExportJobStatus(job.JobId));
            AssertEqual("success", status.Kind, "native single export");
            outputPath = status.OutputPath!;
        }

        var exported = await File.ReadAllTextAsync(outputPath);
        foreach (var marker in new[] { "TOOL-INPUT-MARKER", "TOOL-OUTPUT-MARKER" })
        {
            AssertEqual(includeTools ? 1 : 0, exported.Split(marker).Length - 1, $"{format}, batch={batch}, tools={includeTools}: {marker}");
        }
        foreach (var marker in new[] { "STRING-USER", "THINKING-MARKER", "BEFORE-TOOL", "AFTER-TOOL", "FINAL-ANSWER" })
        {
            AssertEqual(1, exported.Split(marker).Length - 1, $"message appears once: {marker}");
        }
        AssertTrue(exported.IndexOf("THINKING-MARKER", StringComparison.Ordinal) < exported.IndexOf("BEFORE-TOOL", StringComparison.Ordinal), "thinking precedes message");
        if (includeTools)
        {
            AssertTrue(exported.IndexOf("TOOL-INPUT-MARKER", StringComparison.Ordinal) < exported.IndexOf("AFTER-TOOL", StringComparison.Ordinal), "tool precedes following text");
        }
    }
}

static async Task TestNativeClaudeTranscriptAsync()
{
    using var fixture = new TempFixture();
    var service = new ClaudeSessionService();
    var transcript = await service.ReadSessionTranscriptAsync(WriteNativeClaudeSession(fixture));
    AssertEqual(7, transcript.Entries.Count, "no duplicate or empty message entries");
    AssertEqual("STRING-USER", transcript.Entries[0].Text, "string message");
    AssertEqual(SessionTranscriptEntryKind.Reasoning, transcript.Entries[1].Kind, "thinking block order");
    AssertEqual("BEFORE-TOOL", transcript.Entries[2].Text, "text before tool");
    AssertEqual(SessionTranscriptEntryKind.ToolCall, transcript.Entries[3].Kind, "tool block order");
    AssertEqual("AFTER-TOOL", transcript.Entries[4].Text, "text after tool");
    AssertEqual(SessionTranscriptEntryKind.ToolOutput, transcript.Entries[5].Kind, "tool-only message has no user bubble");
}

static async Task TestNativeClaudeImageOptionsAsync()
{
    foreach (var format in new[] { "markdown", "html" })
    foreach (var inlineImages in new[] { false, true })
    foreach (var includeImages in new[] { false, true })
    foreach (var includeTools in new[] { false, true })
    {
        using var fixture = new TempFixture();
        var sessionPath = fixture.WriteSession("""
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"IMAGE-MESSAGE","url":"https://example.com/reference"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"aGVsbG8="}},{"type":"text","text":"AFTER-IMAGE"}]}}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"image-call","content":[{"type":"text","text":"IMAGE-TOOL-RESULT"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"d29ybGQ="}}]}]}}
            """);
        var service = new ClaudeSessionService();
        var job = service.StartExportJob(format, sessionPath, includeImages, inlineImages, includeTools, fixture.Root, null);
        var status = await WaitForExportAsync(() => service.GetExportJobStatus(job.JobId));
        AssertEqual("success", status.Kind, "native image export");
        var exported = await File.ReadAllTextAsync(status.OutputPath!);
        AssertContains("IMAGE-MESSAGE", exported, "text with a URL is preserved");
        AssertEqual(includeTools, exported.Contains("IMAGE-TOOL-RESULT", StringComparison.Ordinal), "tool image output follows tool option");
        var expectedImages = includeImages ? includeTools ? 2 : 1 : 0;
        var imageMarker = format == "html" ? "<img " : "![";
        AssertEqual(expectedImages, exported.Split(imageMarker).Length - 1, "rendered image count");
        if (includeImages)
        {
            AssertTrue(exported.IndexOf(imageMarker, StringComparison.Ordinal) < exported.IndexOf("AFTER-IMAGE", StringComparison.Ordinal), "image stays between surrounding text blocks");
        }
        var externalImages = Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories);
        AssertEqual(format == "html" && inlineImages ? 0 : expectedImages, externalImages.Length, "only included images create assets");
        if (!includeImages)
        {
            AssertTrue(!exported.Contains("aGVsbG8=", StringComparison.Ordinal) && !exported.Contains("d29ybGQ=", StringComparison.Ordinal), "excluded image data never leaks");
        }
    }
}

static async Task TestApiRequestRowsAreNotTranscriptAsync()
{
    using var fixture = new TempFixture();
    // Claude Code also logs the API requests it sends; those rows repeat the conversation and the system prompt.
    var sessionPath = fixture.WriteSession("""
        {"type":"user","sessionId":"native-session","cwd":"C:\\repo","timestamp":"2026-09-14T10:00:00Z","message":{"role":"user","content":"STRING-USER"}}
        {"type":"assistant","timestamp":"2026-09-14T10:00:01Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"call-1","name":"shell","input":{"command":"TOOL-INPUT-MARKER"}}]}}
        {"type":"api-request-blob","sessionId":"native-session","hash":"a","message":{"role":"system","content":[{"type":"text","text":"SYSTEM-BLOB-MARKER"}]}}
        {"type":"api-request-blob","sessionId":"native-session","hash":"b","message":{"role":"assistant","content":[{"type":"tool_use","id":"call-1","name":"shell","input":{"command":"TOOL-INPUT-MARKER"}}]}}
        {"type":"api-request-blob","sessionId":"native-session","hash":"c","message":{"role":"user","content":"STRING-USER"}}
        {"type":"ai-title","sessionId":"native-session","aiTitle":"Session title"}
        """);
    var service = new ClaudeSessionService();

    var transcript = await service.ReadSessionTranscriptAsync(sessionPath);
    AssertEqual(2, transcript.Entries.Count, "transcript holds only conversation rows");
    var metrics = await service.GetSessionDetailMetricsAsync(sessionPath, false);
    AssertEqual(1, metrics.ToolCallCount, "tool calls are counted once");
    AssertEqual(1, metrics.InteractionCount, "a prompt written as plain text is counted, and counted once");

    foreach (var format in new[] { "markdown", "html", "opencode" })
    {
        var job = service.StartExportJob(format, sessionPath, false, false, true, Path.Combine(fixture.Root, format), null);
        var status = await WaitForExportAsync(() => service.GetExportJobStatus(job.JobId));
        AssertEqual("success", status.Kind, $"{format} export");
        var exported = await File.ReadAllTextAsync(status.OutputPath!);
        AssertTrue(!exported.Contains("SYSTEM-BLOB-MARKER", StringComparison.Ordinal), $"{format}: system prompt of an API request is not exported");
        AssertEqual(1, exported.Split("STRING-USER").Length - 1, $"{format}: prompt appears once");
        AssertEqual(1, exported.Split("TOOL-INPUT-MARKER").Length - 1, $"{format}: tool call appears once");
    }
}

static long OpenCodeTime(int seconds)
    => new DateTimeOffset(2026, 11, 17, 10, 0, seconds, TimeSpan.Zero).ToUnixTimeMilliseconds();

// Fills @tN@ with the test time of second N and @model@ with the model reference.
static string OpenCodeJson(string template)
    => System.Text.RegularExpressions.Regex.Replace(template, "@t(\\d+)@", match => OpenCodeTime(int.Parse(match.Groups[1].Value)).ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("@model@", """{"id":"claude-test","providerID":"anthropic"}""");

static string WriteOpenCodeSourceSession(TempFixture fixture)
{
    const string usage = """
        "usage":{"input_tokens":10,"cache_creation_input_tokens":20,"cache_read_input_tokens":30,"output_tokens":40}
        """;
    const string image = """
        {"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}}
        """;
    return fixture.WriteSession(OpenCodeJson("""
        {"type":"user","sessionId":"claude-session-1","cwd":"C:\\repo","version":"2.9.1","timestamp":"2026-11-17T10:00:02Z","message":{"role":"user","content":[{"type":"text","text":"Fix the bug"},@image@]}}
        {"type":"assistant","timestamp":"2026-11-17T10:00:03Z","message":{"id":"msg_a","role":"assistant","model":"claude-test","stop_reason":"tool_use",@usage@,"content":[{"type":"thinking","thinking":"Plan the fix"}]}}
        {"type":"assistant","timestamp":"2026-11-17T10:00:04Z","message":{"id":"msg_a","role":"assistant","model":"claude-test","stop_reason":"tool_use",@usage@,"content":[{"type":"text","text":"Looking at the file."}]}}
        {"type":"assistant","timestamp":"2026-11-17T10:00:05Z","message":{"id":"msg_a","role":"assistant","model":"claude-test","stop_reason":"tool_use",@usage@,"content":[{"type":"tool_use","id":"call_1","name":"Bash","input":{"command":"ls"}}]}}
        {"type":"user","timestamp":"2026-11-17T10:00:06Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"file.txt"}]}}
        {"type":"assistant","timestamp":"2026-11-17T10:00:07Z","message":{"id":"msg_a","role":"assistant","model":"claude-test","stop_reason":"tool_use",@usage@,"content":[{"type":"tool_use","id":"call_2","name":"Read","input":{"file_path":"file.txt"}}]}}
        {"type":"user","timestamp":"2026-11-17T10:00:08Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_2","is_error":true,"content":[{"type":"text","text":"File not found"},@image@]}]}}
        {"type":"assistant","timestamp":"2026-11-17T10:00:09Z","message":{"id":"msg_b","role":"assistant","model":"claude-test","stop_reason":"end_turn","usage":{"input_tokens":1,"cache_read_input_tokens":2,"output_tokens":3},"content":[{"type":"text","text":"Fixed."}]}}
        {"type":"ai-title","sessionId":"claude-session-1","aiTitle":"Fix a bug"}
        {"type":"custom-title","sessionId":"claude-session-1","customTitle":"Bug fix session"}
        """)
        .Replace("@usage@", usage.Trim())
        .Replace("@image@", image.Trim()));
}

static async Task<JsonObject> ExportOpenCodeAsync(string sessionPath, string outputDirectory, bool includeImages, bool includeTools)
{
    var service = new ClaudeSessionService();
    var job = service.StartExportJob("opencode", sessionPath, includeImages, false, includeTools, outputDirectory, null);
    var status = await WaitForExportAsync(() => service.GetExportJobStatus(job.JobId));
    AssertEqual("success", status.Kind, $"opencode export: {status.Message}");
    AssertTrue(status.OutputPath!.EndsWith(".opencode.json", StringComparison.Ordinal), "opencode export file suffix");
    AssertContains("OpenCode session file written to", status.Message, "opencode export message");
    var data = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(status.OutputPath))!;
    AssertOpenCodeImportable(data);
    return data;
}

// Mirrors what the OpenCode 2.3 importer requires of SessionTransfer.Data.
static void AssertOpenCodeImportable(JsonObject data)
{
    static bool IsTime(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var time) && time >= 0;
    static bool IsText(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out _);
    static bool Matches(JsonNode? node, string pattern)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && System.Text.RegularExpressions.Regex.IsMatch(text, pattern);

    AssertEqual("info,messages", string.Join(",", data.Select(pair => pair.Key).Order()), "top-level keys");
    var info = (JsonObject)data["info"]!;
    AssertTrue(Matches(info["id"], "^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$"), "session id layout");
    AssertTrue(IsText(info["projectID"]) && IsText(info["location"]!["directory"]) && IsText(info["title"]), "session info texts");
    AssertTrue(IsTime(info["time"]!["created"]) && IsTime(info["time"]!["updated"]), "session times");
    foreach (var count in new[] { info["cost"], info["tokens"]!["input"], info["tokens"]!["output"], info["tokens"]!["reasoning"], info["tokens"]!["cache"]!["read"], info["tokens"]!["cache"]!["write"] })
    {
        AssertTrue(IsTime(count), "session counts are numbers");
    }

    var ids = new List<string>();
    foreach (var message in ((JsonArray)data["messages"]!).Cast<JsonObject>())
    {
        AssertTrue(Matches(message["id"], "^msg_[0-9a-f]{12}[0-9A-Za-z]{14}$"), "message id layout");
        ids.Add(message["id"]!.GetValue<string>());
        AssertTrue(IsTime(message["time"]!["created"]), "message time");
        switch (message["type"]!.GetValue<string>())
        {
            case "user":
                AssertTrue(IsText(message["text"]), "user text");
                foreach (var file in (message["files"] as JsonArray ?? new JsonArray()).Cast<JsonObject>())
                {
                    AssertTrue(Matches(file["data"], "^[A-Za-z0-9+/]*={0,2}$") && file["data"]!.GetValue<string>().Length % 4 == 0, "attachment is padded base64");
                    AssertTrue(IsText(file["mime"]) && file["source"]!["type"]!.GetValue<string>() == "inline", "attachment source");
                }
                break;
            case "assistant":
                AssertTrue(IsText(message["agent"]) && IsText(message["model"]!["id"]) && IsText(message["model"]!["providerID"]), "assistant agent and model");
                // The importer drops an assistant message that has no completion time.
                AssertTrue(IsTime(message["time"]!["completed"]), "assistant completion time");
                AssertTrue(Matches(message["finish"], "^(stop|length|tool-calls|content-filter|error|unknown)$"), "assistant finish reason");
                AssertTrue(((JsonArray)message["content"]!).Count > 0, "assistant message has content");
                foreach (var part in ((JsonArray)message["content"]!).Cast<JsonObject>())
                {
                    if (part["type"]!.GetValue<string>() != "tool")
                    {
                        AssertTrue(Matches(part["type"], "^(text|reasoning)$") && IsText(part["text"]), "text part");
                        continue;
                    }

                    AssertTrue(IsText(part["id"]) && IsText(part["name"]) && IsTime(part["time"]!["created"]), "tool part");
                    var state = (JsonObject)part["state"]!;
                    AssertTrue(state["input"] is JsonObject, "tool input is an object");
                    if (state["status"]!.GetValue<string>() == "completed")
                    {
                        AssertTrue(((JsonArray)state["content"]!).Count > 0, "completed tool call has content");
                    }
                    else
                    {
                        AssertEqual("error", state["status"]!.GetValue<string>(), "tool state");
                        AssertTrue(IsText(state["error"]!["type"]) && IsText(state["error"]!["message"]), "tool error");
                        AssertTrue(state["content"] is null or JsonArray { Count: > 0 }, "tool error content is absent or filled");
                    }
                }
                break;
            case "synthetic":
                AssertTrue(IsText(message["text"]), "synthetic text");
                break;
            case "compaction":
                AssertEqual("completed", message["status"]!.GetValue<string>(), "compaction status");
                AssertTrue(Matches(message["reason"], "^(auto|manual)$") && IsText(message["summary"]), "compaction fields");
                break;
            case "idle":
                AssertTrue(Matches(message["outcome"], "^(succeeded|failed|interrupted)$"), "idle outcome");
                break;
            default:
                throw new InvalidOperationException($"unexpected message type {message["type"]}");
        }
    }

    AssertEqual(ids.Count, ids.Distinct().Count(), "message ids are unique");
    AssertTrue(ids.SequenceEqual(ids.Order(StringComparer.Ordinal)), "message ids ascend");
}

static void AssertJsonEqual(string expectedJson, JsonNode? actual, string label)
{
    var expected = JsonNode.Parse(expectedJson);
    if (!JsonNode.DeepEquals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected {expected?.ToJsonString()}, got {actual?.ToJsonString()}");
    }
}

static JsonArray OpenCodeMessagesWithoutIds(JsonObject data)
{
    var messages = (JsonArray)data["messages"]!.DeepClone();
    foreach (var message in messages.Cast<JsonObject>())
    {
        message.Remove("id");
    }
    return messages;
}

static async Task TestOpenCodeExportTranscriptAsync()
{
    using var fixture = new TempFixture();
    var data = await ExportOpenCodeAsync(WriteOpenCodeSourceSession(fixture), fixture.Root, includeImages: true, includeTools: true);

    AssertJsonEqual(OpenCodeJson("""
        [
          {"type":"user","time":{"created":@t2@},"text":"Fix the bug",
           "files":[{"data":"iVBORw0KGgo=","mime":"image/png","source":{"type":"inline"},"name":"image-001.png"}]},
          {"type":"assistant","time":{"created":@t3@,"completed":@t8@},"agent":"build","model":@model@,
           "content":[
             {"type":"reasoning","text":"Plan the fix"},
             {"type":"text","text":"Looking at the file."},
             {"type":"tool","id":"call_1","name":"Bash",
              "state":{"status":"completed","input":{"command":"ls"},"content":[{"type":"text","text":"file.txt"}]},
              "time":{"created":@t5@,"ran":@t5@,"completed":@t6@}},
             {"type":"tool","id":"call_2","name":"Read",
              "state":{"status":"error","input":{"file_path":"file.txt"},"error":{"type":"tool.execution","message":"File not found"},
                       "content":[{"type":"text","text":"File not found"},{"type":"file","uri":"data:image/png;base64,iVBORw0KGgo=","mime":"image/png","name":"image-002.png"}]},
              "time":{"created":@t7@,"ran":@t7@,"completed":@t8@}}
           ],
           "finish":"tool-calls","tokens":{"input":10,"output":40,"reasoning":0,"cache":{"read":30,"write":20}}},
          {"type":"assistant","time":{"created":@t9@,"completed":@t9@},"agent":"build","model":@model@,
           "content":[{"type":"text","text":"Fixed."}],
           "finish":"stop","tokens":{"input":1,"output":3,"reasoning":0,"cache":{"read":2,"write":0}}},
          {"type":"idle","time":{"created":@t9@},"outcome":"succeeded"}
        ]
        """), OpenCodeMessagesWithoutIds(data), "opencode messages");

    var info = (JsonObject)data["info"]!.DeepClone();
    info.Remove("id");
    AssertJsonEqual(OpenCodeJson("""
        {"projectID":"global","model":@model@,"cost":0,
         "tokens":{"input":11,"output":43,"reasoning":0,"cache":{"read":32,"write":20}},
         "outcome":"succeeded",
         "time":{"created":@t2@,"updated":@t9@,"idle":@t9@,"viewed":@t9@},
         "title":"Bug fix session","location":{"directory":"C:\\repo"},
         "metadata":{"source":"claude-code","claudeSessionId":"claude-session-1","claudeCodeVersion":"2.9.1","exportedBy":"clodlogs"}}
        """), info, "opencode session info");
}

static async Task TestOpenCodeExportOptionsAsync()
{
    using var fixture = new TempFixture();
    var data = await ExportOpenCodeAsync(WriteOpenCodeSourceSession(fixture), fixture.Root, includeImages: false, includeTools: false);
    var exported = data.ToJsonString();

    AssertTrue(!exported.Contains("iVBORw0KGgo=", StringComparison.Ordinal), "excluded image data never leaks");
    AssertTrue(!exported.Contains("call_1", StringComparison.Ordinal) && !exported.Contains("file.txt", StringComparison.Ordinal), "excluded tool calls and results never leak");
    var messages = (JsonArray)data["messages"]!;
    AssertEqual("user,assistant,assistant,idle", string.Join(",", messages.Select(message => message!["type"]!.GetValue<string>())), "message types without tools");
    AssertTrue(messages[0]!["files"] is null, "prompt has no attachments");
    AssertEqual("reasoning,text", string.Join(",", ((JsonArray)messages[1]!["content"]!).Select(part => part!["type"]!.GetValue<string>())), "response keeps reasoning and text");
    // The token totals do not depend on the options.
    AssertEqual(43L, data["info"]!["tokens"]!["output"]!.GetValue<long>(), "output tokens");
}

static async Task TestOpenCodeExportTurnsAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"user","sessionId":"claude-session-2","cwd":"C:\\repo","timestamp":"2026-11-17T10:00:01Z","message":{"role":"user","content":"First\nprompt"}}
        {"type":"assistant","timestamp":"2026-11-17T10:00:02Z","message":{"id":"msg_a","role":"assistant","model":"claude-test","stop_reason":"tool_use","content":[{"type":"tool_use","id":"call_1","name":"Bash","input":"not an object"}]}}
        {"type":"assistant","isSidechain":true,"timestamp":"2026-11-17T10:00:02Z","message":{"id":"msg_side","role":"assistant","model":"claude-test","content":[{"type":"text","text":"SIDECHAIN"}]}}
        {"type":"user","timestamp":"2026-11-17T10:00:03Z","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}}
        {"type":"user","isMeta":true,"timestamp":"2026-11-17T10:00:04Z","message":{"role":"user","content":"<local-command-caveat>Caveat</local-command-caveat>"}}
        {"type":"system","subtype":"compact_boundary","timestamp":"2026-11-17T10:00:05Z","compactMetadata":{"trigger":"manual"},"content":"Conversation compacted"}
        {"type":"user","isCompactSummary":true,"timestamp":"2026-11-17T10:00:06Z","message":{"role":"user","content":"Summary of the work so far"}}
        {"type":"user","timestamp":"2026-11-17T10:00:07Z","message":{"role":"user","content":"Second"}}
        {"type":"assistant","isApiErrorMessage":true,"timestamp":"2026-11-17T10:00:08Z","message":{"id":"msg_error","role":"assistant","model":"<synthetic>","stop_reason":"stop_sequence","content":[{"type":"text","text":"API Error: 500"}]}}
        {"type":"user","timestamp":"2026-11-17T10:00:09Z","message":{"role":"user","content":"Third"}}
        """);
    var data = await ExportOpenCodeAsync(sessionPath, fixture.Root, includeImages: false, includeTools: true);

    AssertTrue(!data.ToJsonString().Contains("SIDECHAIN", StringComparison.Ordinal), "subagent rows are not part of the conversation");
    AssertJsonEqual(OpenCodeJson("""
        [
          {"type":"user","time":{"created":@t1@},"text":"First\nprompt"},
          {"type":"assistant","time":{"created":@t2@,"completed":@t2@},"agent":"build","model":@model@,
           "content":[{"type":"tool","id":"call_1","name":"Bash",
             "state":{"status":"error","input":{"input":"not an object"},"error":{"type":"aborted","message":"No tool result was recorded in the Claude session."}},
             "time":{"created":@t2@,"ran":@t2@,"completed":@t2@}}],
           "finish":"tool-calls"},
          {"type":"idle","time":{"created":@t2@},"outcome":"interrupted"},
          {"type":"user","time":{"created":@t3@},"text":"[Request interrupted by user for tool use]"},
          {"type":"synthetic","time":{"created":@t4@},"text":"<local-command-caveat>Caveat</local-command-caveat>"},
          {"type":"compaction","time":{"created":@t6@},"status":"completed","reason":"manual","summary":"Summary of the work so far"},
          {"type":"user","time":{"created":@t7@},"text":"Second"},
          {"type":"assistant","time":{"created":@t8@,"completed":@t8@},"agent":"build","model":@model@,
           "content":[{"type":"text","text":"API Error: 500"}],"finish":"error"},
          {"type":"idle","time":{"created":@t8@},"outcome":"failed"},
          {"type":"user","time":{"created":@t9@},"text":"Third"},
          {"type":"idle","time":{"created":@t9@},"outcome":"interrupted"}
        ]
        """), OpenCodeMessagesWithoutIds(data), "opencode turn messages");
    AssertEqual("interrupted", data["info"]!["outcome"]!.GetValue<string>(), "last outcome");
    AssertEqual("First", data["info"]!["title"]!.GetValue<string>(), "title falls back to the first prompt line");
}

static async Task TestOpenCodeExportImagesAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"user","sessionId":"claude-session-3","cwd":"C:\\repo","timestamp":"2026-11-17T10:00:01Z","message":{"role":"user","content":[{"type":"text","text":"Look"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"aGVsbG8"}},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"@@@@"}},{"type":"image","source":{"type":"url","url":"https://example.com/picture.png"}},{"type":"image","source":{"type":"base64","media_type":"text/html","data":"PGI+"}},{"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"/9j/\n4AAQ"}}]}}
        """);
    var data = await ExportOpenCodeAsync(sessionPath, fixture.Root, includeImages: true, includeTools: false);

    AssertJsonEqual("""
        [
          {"data":"aGVsbG8=","mime":"image/png","source":{"type":"inline"},"name":"image-001.png"},
          {"data":"/9j/4AAQ","mime":"image/jpeg","source":{"type":"inline"},"name":"image-002.jpg"}
        ]
        """, data["messages"]![0]!["files"], "embedded images");
}

static async Task TestOpenCodeBatchExportAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = WriteOpenCodeSourceSession(fixture);
    var outputDirectory = Path.Combine(fixture.Root, "export");
    var service = new ClaudeSessionService();
    AssertEqual("Bug fix - 2026-11-17T10-00-02.000Z.opencode.json", ClaudeSessionService.BuildBatchExportFileName("Bug fix", "2026-11-17T10:00:02Z", "opencode"), "batch file name");

    // Images are embedded, so an asset folder of the same name does not force another file name.
    Directory.CreateDirectory(Path.Combine(outputDirectory, "Bug fix - 2026-11-17T10-00-02.000Z.opencode-assets"));
    var job = service.StartBatchExportJob("opencode", [new(sessionPath, "Bug fix", "2026-11-17T10:00:02Z"), new(sessionPath, "Bug fix", "2026-11-17T10:00:02Z")], true, false, true, outputDirectory);
    var status = await WaitForBatchExportAsync(() => service.GetBatchExportJobStatus(job.JobId));
    AssertEqual("success", status.Kind, $"opencode batch export: {status.Message}");
    var files = Directory.GetFiles(outputDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
    AssertEqual("Bug fix - 2026-11-17T10-00-02.000Z.opencode-2.json,Bug fix - 2026-11-17T10-00-02.000Z.opencode.json", string.Join(",", files), "batch output files");

    var exports = files.Select(file => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(outputDirectory, file!)))!).ToArray();
    foreach (var export in exports) AssertOpenCodeImportable(export);
    // OpenCode reports the second import of a session as a conflict instead of adding a copy.
    AssertTrue(JsonNode.DeepEquals(exports[0], exports[1]), "the same session always exports with the same identifiers");
}

static Task TestOpenCodeIdentifiers()
{
    // Timestamp and counter prefixes of identifiers that OpenCode 2.3.0 created.
    var message = ClaudeSessionService.BuildOpenCodeIdentifier("msg", 1794882744955, 1, false, "seed");
    var session = ClaudeSessionService.BuildOpenCodeIdentifier("ses", 1794882744921, 1, true, "seed");
    AssertTrue(System.Text.RegularExpressions.Regex.IsMatch(message, "^msg_1e759327b001[0-9A-Za-z]{14}$"), $"message identifier layout: {message}");
    AssertTrue(System.Text.RegularExpressions.Regex.IsMatch(session, "^ses_e18a6cda6ffe[0-9A-Za-z]{14}$"), $"session identifier layout: {session}");
    AssertTrue(message != ClaudeSessionService.BuildOpenCodeIdentifier("msg", 1794882744955, 1, false, "other"), "the seed decides the suffix");
    return Task.CompletedTask;
}

static async Task TestSingleExportCollisionsAsync()
{
    foreach (var format in new[] { "markdown", "html" })
    {
        using var fixture = new TempFixture();
        var sessionPath = WriteNativeClaudeSession(fixture);
        var originalPath = Path.Combine(fixture.Root, format == "html" ? "sample.html" : "sample.md");
        await File.WriteAllTextAsync(originalPath, "EXISTING-EXPORT");
        var service = new ClaudeSessionService();
        var job = service.StartExportJob(format, sessionPath, false, false, false, fixture.Root, null);
        var status = await WaitForExportAsync(() => service.GetExportJobStatus(job.JobId));
        AssertEqual("success", status.Kind, "collision export status");
        AssertEqual("EXISTING-EXPORT", await File.ReadAllTextAsync(originalPath), "automatic export preserves existing file");
        AssertTrue(status.OutputPath != originalPath, "new output has a distinct filename");
    }
}

static async Task TestFailedExportPreservesOutputAsync()
{
    foreach (var format in new[] { "markdown", "html" })
    {
        using var fixture = new TempFixture();
        var sessionPath = fixture.WriteSession("""
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_image","image_url":"data:image/png;base64,aGVsbG8="}]}}
            """);
        var outputPath = Path.Combine(fixture.Root, format == "html" ? "existing.html" : "existing.md");
        await File.WriteAllTextAsync(outputPath, "EXISTING-EXPORT");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "existing-assets"), "ASSET-PATH-BLOCKED");
        var service = new ClaudeSessionService();
        var job = service.StartExportJob(format, sessionPath, true, false, true, null, outputPath);
        var status = await WaitForExportAsync(() => service.GetExportJobStatus(job.JobId));
        AssertEqual("error", status.Kind, "image I/O failure is reported");
        AssertEqual("EXISTING-EXPORT", await File.ReadAllTextAsync(outputPath), "failed replacement preserves existing export");
        AssertEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length, "partial export is cleaned up");
    }
}

static async Task TestImageAssetLinksAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_image","image_url":"data:image/png;base64,aGVsbG8="}]}}
        """);
    var outputPath = Path.Combine(fixture.Root, "image path (one)#.md");
    var assetName = "image path (one)#-assets";
    Directory.CreateDirectory(Path.Combine(fixture.Root, assetName, "image-001.png"));
    var service = new ClaudeSessionService();
    var job = service.StartExportJob("markdown", sessionPath, true, false, false, null, outputPath);
    var status = await WaitForExportAsync(() => service.GetExportJobStatus(job.JobId));
    AssertEqual("success", status.Kind, "asset directory collision is skipped");
    var exported = await File.ReadAllTextAsync(outputPath);
    AssertContains($"./{Uri.EscapeDataString(assetName)}/image-002.png", exported, "asset link is URL encoded");
    AssertEqual("hello", await File.ReadAllTextAsync(Path.Combine(fixture.Root, assetName, "image-002.png")), "asset bytes are preserved");
}

static Task TestInvalidExportFormat()
{
    using var fixture = new TempFixture();
    var service = new ClaudeSessionService();
    try
    {
        service.StartExportJob("invalid", WriteNativeClaudeSession(fixture), false, false, false, fixture.Root, null);
    }
    catch (ArgumentException)
    {
        AssertEqual(1, Directory.GetFiles(fixture.Root).Length, "invalid format creates no output");
        return Task.CompletedTask;
    }
    throw new InvalidOperationException("unsupported format must be rejected before starting a job");
}

static async Task TestExplicitExportReplacementAsync()
{
    foreach (var format in new[] { "markdown", "html" })
    {
        using var fixture = new TempFixture();
        var sessionPath = WriteNativeClaudeSession(fixture);
        var originalSource = await File.ReadAllTextAsync(sessionPath);
        var outputPath = Path.Combine(fixture.Root, format == "html" ? "existing.html" : "existing.md");
        await File.WriteAllTextAsync(outputPath, "PREVIOUS-EXPORT");
        var assets = Path.Combine(fixture.Root, "existing-assets");
        Directory.CreateDirectory(assets);
        var unrelatedAsset = Path.Combine(assets, "image-001.png");
        await File.WriteAllTextAsync(unrelatedAsset, "PREVIOUS-ASSET");
        var service = new ClaudeSessionService();
        var job = service.StartExportJob(format, sessionPath, true, false, false, null, outputPath);
        var status = await WaitForExportAsync(() => service.GetExportJobStatus(job.JobId));
        AssertEqual("success", status.Kind, "explicit replacement succeeds");
        var exported = await File.ReadAllTextAsync(outputPath);
        AssertContains("FINAL-ANSWER", exported, "explicit destination contains completed export");
        AssertTrue(!exported.Contains("PREVIOUS-EXPORT", StringComparison.Ordinal), "old content is replaced");
        AssertEqual("PREVIOUS-ASSET", await File.ReadAllTextAsync(unrelatedAsset), "unrelated asset remains unchanged");
        AssertEqual(originalSource, await File.ReadAllTextAsync(sessionPath), "source session remains unchanged");
        AssertEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length, "no temporary output remains");
    }
}

static async Task TestExportCancellationAsync()
{
    foreach (var format in new[] { "markdown", "html" })
    {
        using var fixture = new TempFixture();
        var sessionPath = fixture.WriteSession("""
            {"type":"user","message":{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"aGVsbG8="}}]}}
            {"type":"assistant","message":{"role":"assistant","content":"AFTER-CANCELLATION"}}
            """);
        var outputPath = Path.Combine(fixture.Root, format == "html" ? "existing.html" : "existing.md");
        await File.WriteAllTextAsync(outputPath, "PREVIOUS-EXPORT");
        var assets = Path.Combine(fixture.Root, "existing-assets");
        Directory.CreateDirectory(assets);
        var existingAsset = Path.Combine(assets, "image-001.png");
        await File.WriteAllTextAsync(existingAsset, "PREVIOUS-ASSET");
        using var cancellation = new CancellationTokenSource();
        Action<ExportJobStatus> progress = status =>
        {
            if (status.Stage == "rendering") cancellation.Cancel();
        };
        var method = typeof(ClaudeSessionService).GetMethod("ExportSessionJsonlAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Export method not found.");
        var export = (Task<string>)method.Invoke(new ClaudeSessionService(),
            [format, sessionPath, true, false, true, null, outputPath, progress, cancellation.Token])!;
        try
        {
            await export;
            throw new InvalidOperationException("Export should be cancelled after the first rendered record.");
        }
        catch (OperationCanceledException)
        {
        }
        AssertEqual("PREVIOUS-EXPORT", await File.ReadAllTextAsync(outputPath), "cancelled replacement preserves previous export");
        AssertEqual("PREVIOUS-ASSET", await File.ReadAllTextAsync(existingAsset), "cancelled replacement preserves previous asset");
        AssertEqual(1, Directory.GetFiles(assets).Length, "only newly created assets are removed");
        AssertEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length, "cancelled output is cleaned up");
    }
}

static async Task TestImageSourcesAsync()
{
    foreach (var format in new[] { "markdown", "html" })
    foreach (var inline in new[] { false, true })
    {
        using var fixture = new TempFixture();
        var sessionPath = fixture.WriteSession("""
            {"type":"user","message":{"role":"user","content":[{"type":"image","source":{"type":"url","url":"https://example.com/image%20name%20(one).png"}}]}}
            """);
        var service = new ClaudeSessionService();
        var job = service.StartExportJob(format, sessionPath, true, inline, false, fixture.Root, null);
        var status = await WaitForExportAsync(() => service.GetExportJobStatus(job.JobId));
        AssertEqual("success", status.Kind, "native URL image exports");
        AssertContains("https://example.com/image%20name%20%28one%29.png", await File.ReadAllTextAsync(status.OutputPath!), "URL is safe in an image link");

        var invalidPath = fixture.WriteSession("""
            {"type":"user","message":{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"invalid!"}}]}}
            """, "invalid-image.jsonl");
        var invalidJob = service.StartExportJob(format, invalidPath, true, inline, false, fixture.Root, null);
        var invalidStatus = await WaitForExportAsync(() => service.GetExportJobStatus(invalidJob.JobId));
        AssertEqual("error", invalidStatus.Kind, "invalid image data is reported");
        AssertContains("base64", invalidStatus.Message, "image error identifies invalid data");
        AssertEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length, "invalid image leaves no temporary file");
    }
}

static async Task TestSanitizedCopyAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"session_meta","timestamp":"2026-03-16T12:00:00.000Z","payload":{"id":"session-c","timestamp":"2026-03-16T12:00:00.000Z","cwd":"C:\\repo","originator":"claude_code","source":"claude_code","model_provider":"anthropic"}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:01.000Z","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"show this"},{"type":"input_image","image_url":"data:image/png;base64,aGVsbG8="}]}}
        """);

    var service = new ClaudeSessionService();
    var job = service.StartSanitizedCopyJob(sessionPath, claudeHome: null, chatName: "Test Session", stripImageContent: true, stripBlobContent: true, createJsonlCopy: true, reAddToCurrentDay: false);
    var status = await WaitForExportAsync(() => service.GetSanitizedCopyJobStatus(job.JobId));

    AssertEqual("success", status.Kind, "sanitize status");
    var sanitizedPath = Path.Combine(status.OutputPath!, "sanitized-session.jsonl");
    AssertTrue(File.Exists(sanitizedPath), "sanitized jsonl exists");
    var sanitized = await File.ReadAllTextAsync(sanitizedPath, Encoding.UTF8);
    AssertContains("<image removed>", sanitized, "image placeholder");
    AssertTrue(!sanitized.Contains("data:image/png", StringComparison.Ordinal), "image data removed");
}

static async Task TestHtmlExportInlineImagesAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"session_meta","timestamp":"2026-03-16T12:00:00.000Z","payload":{"id":"session-html","timestamp":"2026-03-16T12:00:00.000Z","cwd":"C:\\repo","originator":"claude_code","source":"claude_code","model_provider":"anthropic"}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:01.000Z","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hello html"},{"type":"input_image","image_url":"data:image/png;base64,aGVsbG8=","alt_text":"inline"}]}}
        """);

    var outputPath = Path.Combine(fixture.Root, "session.html");
    var service = new ClaudeSessionService();
    var job = service.StartExportJob("html", sessionPath, includeImages: true, inlineImages: true, includeToolCallResults: false, outputDirectory: null, outputPath);
    var status = await WaitForExportAsync(() => service.GetExportJobStatus(job.JobId));

    AssertEqual("success", status.Kind, "html export status");
    var html = await File.ReadAllTextAsync(outputPath, Encoding.UTF8);
    AssertContains("data:image/png;base64,aGVsbG8=", html, "inline image data");
    AssertContains("alt=\"inline\"", html, "inline image alt text");
}

static async Task TestBatchExportNamingAndCollisionAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"session_meta","timestamp":"2026-03-16T12:00:00.000+02:00","payload":{"id":"session-batch","timestamp":"2026-03-16T12:00:00.000+02:00","cwd":"C:\\repo","originator":"claude_code","source":"claude_code","model_provider":"anthropic"}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:01.000+02:00","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"batch markdown"}]}}
        """, "batch.jsonl");
    var outputDirectory = Path.Combine(fixture.Root, "batch-output");
    Directory.CreateDirectory(outputDirectory);
    var proposedName = ClaudeSessionService.BuildBatchExportFileName("Quarter: report / alpha", "2026-03-16T12:00:00.000+02:00", "markdown");
    AssertContains("2026-03-16T10-00-00.000Z.md", proposedName, "UTC timestamp filename");
    AssertTrue(proposedName.IndexOfAny([':', '/', '\\']) < 0, "filename excludes invalid separators");
    await File.WriteAllTextAsync(Path.Combine(outputDirectory, proposedName), "existing");

    var service = new ClaudeSessionService();
    var jobOne = service.StartBatchExportJob(
        "markdown",
        [new BatchExportSessionRequest(sessionPath, "Quarter: report / alpha", "2026-03-16T12:00:00.000+02:00")],
        includeImages: false,
        inlineImages: false,
        includeToolCallResults: false,
        outputDirectory);
    var jobTwo = service.StartBatchExportJob(
        "markdown",
        [new BatchExportSessionRequest(sessionPath, "Quarter: report / alpha", "2026-03-16T12:00:00.000+02:00")],
        includeImages: false,
        inlineImages: false,
        includeToolCallResults: false,
        outputDirectory);
    var statuses = await Task.WhenAll(
        WaitForBatchExportAsync(() => service.GetBatchExportJobStatus(jobOne.JobId)),
        WaitForBatchExportAsync(() => service.GetBatchExportJobStatus(jobTwo.JobId)));

    AssertTrue(statuses.All(status => status.Kind == "success"), "concurrent batch export status");
    AssertTrue(File.Exists(Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(proposedName)}-2.md")), "first collision output exists");
    AssertTrue(File.Exists(Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(proposedName)}-3.md")), "second collision output exists");
    AssertEqual("existing", await File.ReadAllTextAsync(Path.Combine(outputDirectory, proposedName)), "existing export remains unchanged");
}

static async Task TestBatchExportAssetCollisionsAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"session_meta","timestamp":"2026-03-16T12:00:00.000Z","payload":{"id":"session-assets","timestamp":"2026-03-16T12:00:00.000Z","cwd":"C:\\repo","originator":"claude_code","source":"claude_code","model_provider":"anthropic"}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:01.000Z","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"asset collision"}]}}
        """, "assets.jsonl");
    var outputDirectory = Path.Combine(fixture.Root, "asset-output");
    Directory.CreateDirectory(outputDirectory);
    var proposedName = ClaudeSessionService.BuildBatchExportFileName("Assets", "2026-03-16T12:00:00.000Z", "markdown");
    var stem = Path.GetFileNameWithoutExtension(proposedName);
    var assetFile = Path.Combine(outputDirectory, $"{stem}-assets");
    await File.WriteAllTextAsync(assetFile, "occupied asset path");
    Directory.CreateDirectory(Path.Combine(outputDirectory, $"{stem}-2-assets"));

    var service = new ClaudeSessionService();
    var externalJob = service.StartBatchExportJob(
        "markdown",
        [new BatchExportSessionRequest(sessionPath, "Assets", "2026-03-16T12:00:00.000Z")],
        includeImages: true,
        inlineImages: false,
        includeToolCallResults: false,
        outputDirectory);
    var externalStatus = await WaitForBatchExportAsync(() => service.GetBatchExportJobStatus(externalJob.JobId));

    AssertEqual("success", externalStatus.Kind, "external asset batch status");
    AssertTrue(File.Exists(Path.Combine(outputDirectory, $"{stem}-3.md")), "file and directory asset collisions skipped");

    var noAssetsJob = service.StartBatchExportJob(
        "markdown",
        [new BatchExportSessionRequest(sessionPath, "Assets", "2026-03-16T12:00:00.000Z")],
        includeImages: false,
        inlineImages: false,
        includeToolCallResults: false,
        outputDirectory);
    var noAssetsStatus = await WaitForBatchExportAsync(() => service.GetBatchExportJobStatus(noAssetsJob.JobId));

    AssertEqual("success", noAssetsStatus.Kind, "no-assets batch status");
    AssertTrue(File.Exists(Path.Combine(outputDirectory, proposedName)), "asset occupancy ignored without external assets");
    AssertEqual("occupied asset path", await File.ReadAllTextAsync(assetFile), "occupied asset file remains unchanged");
}

static Task TestBatchPathReservationFailures()
{
    using var fixture = new TempFixture();
    var method = typeof(ClaudeSessionService).GetMethod(
        "CreateAvailableExportPath",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Batch path reservation method not found.");
    var missingDirectory = Path.Combine(fixture.Root, "missing");

    AssertInvocationThrows<DirectoryNotFoundException>(
        () => method.Invoke(null, [missingDirectory, "session.md", false, CancellationToken.None]),
        "non-collision I/O is rethrown");

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    AssertInvocationThrows<OperationCanceledException>(
        () => method.Invoke(null, [fixture.Root, "session.md", false, cancellation.Token]),
        "reservation cancellation is preserved");
    return Task.CompletedTask;
}

static async Task TestBatchExportPartialFailureAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"session_meta","timestamp":"2026-03-16T12:00:00.000Z","payload":{"id":"session-batch-html","timestamp":"2026-03-16T12:00:00.000Z","cwd":"C:\\repo","originator":"claude_code","source":"claude_code","model_provider":"anthropic"}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:01.000Z","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"batch html"}]}}
        """, "batch-html.jsonl");
    var missingPath = Path.Combine(fixture.Root, "missing.jsonl");
    var outputDirectory = Path.Combine(fixture.Root, "partial-output");
    var service = new ClaudeSessionService();
    var requests = new[]
    {
        new BatchExportSessionRequest(missingPath, "Missing", null),
        new BatchExportSessionRequest(sessionPath, "Working Session", "2026-03-16T12:00:00.000Z")
    };
    var job = service.StartBatchExportJob("html", requests, includeImages: false, inlineImages: true, includeToolCallResults: false, outputDirectory);
    var status = await WaitForBatchExportAsync(() => service.GetBatchExportJobStatus(job.JobId));

    AssertEqual("partial", status.Kind, "partial batch status");
    AssertEqual(1, status.Result?.Failures.Count, "partial failure count");
    AssertTrue(File.Exists(Path.Combine(outputDirectory, "Working Session - 2026-03-16T12-00-00.000Z.html")), "later session exports after failure");
}

static async Task TestCorruptSettingsRemainUntouchedAsync()
{
    using var fixture = new TempFixture();
    var settingsPath = Path.Combine(fixture.Root, "current", "clodlogs-settings.json");
    var legacyPath = Path.Combine(fixture.Root, "legacy", "clodlogs-settings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
    const string corruptSettings = "{ this is not valid json";
    await File.WriteAllTextAsync(settingsPath, corruptSettings);
    await File.WriteAllTextAsync(legacyPath, """{"lastOpenedFolder":"D:\\repos\\legacy"}""");

    var service = new AppSettingsService(settingsPath, legacyPath);
    var settings = await service.ReadAsync();
    await service.UpdateAsync(value => value.ExportDirectory = "D:\\exports");

    AssertEqual<string?>(null, settings.LastOpenedFolder, "corrupt current settings do not migrate");
    AssertTrue(!settings.LegacySettingsMigrated, "corrupt current settings do not set marker");
    AssertEqual(corruptSettings, await File.ReadAllTextAsync(settingsPath), "corrupt current settings preserved");
}

static async Task TestFailedLegacyMigrationRetriesAsync()
{
    using var fixture = new TempFixture();
    var settingsPath = Path.Combine(fixture.Root, "current", "clodlogs-settings.json");
    var legacyPath = Path.Combine(fixture.Root, "legacy", "clodlogs-settings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
    await File.WriteAllTextAsync(legacyPath, "not-json");

    var first = await new AppSettingsService(settingsPath, legacyPath).ReadAsync();
    AssertTrue(!first.LegacySettingsMigrated, "failed migration leaves marker clear");
    AssertTrue(!File.Exists(settingsPath), "failed migration is not persisted");

    await File.WriteAllTextAsync(legacyPath, """{"lastOpenedFolder":"D:\\repos\\recovered"}""");
    var recovered = await new AppSettingsService(settingsPath, legacyPath).ReadAsync();
    AssertTrue(recovered.LegacySettingsMigrated, "fixed migration sets marker");
    AssertEqual("D:\\repos\\recovered", recovered.LastOpenedFolder, "fixed migration is retried");
}

static async Task TestSettingsPersistenceFailureAsync()
{
    using var fixture = new TempFixture();
    var blockingParent = Path.Combine(fixture.Root, "not-a-directory");
    await File.WriteAllTextAsync(blockingParent, "keep me");
    var settingsPath = Path.Combine(blockingParent, "clodlogs-settings.json");

    var settings = await new AppSettingsService(settingsPath).ReadAsync();

    AssertTrue(settings.LegacySettingsMigrated, "startup returns settings despite persistence failure");
    AssertEqual("keep me", await File.ReadAllTextAsync(blockingParent), "failed persistence preserves existing path");
}

static async Task TestLegacySettingsMigrationAsync()
{
    using var fixture = new TempFixture();
    var settingsPath = Path.Combine(fixture.Root, "current", "clodlogs-settings.json");
    var legacyPath = Path.Combine(fixture.Root, "legacy", "clodlogs-settings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
    await File.WriteAllTextAsync(legacyPath, """
        {
          "lastOpenedFolder": "D:\\repos\\remembered",
          "windowFrame": { "x": 10, "y": 20, "width": 1200, "height": 800 }
        }
        """);

    var service = new AppSettingsService(settingsPath, legacyPath);
    var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.ReadAsync()));
    AssertTrue(reads.All(settings => settings.LastOpenedFolder == "D:\\repos\\remembered"), "all concurrent reads see migrated folder");
    AssertTrue(reads.All(settings => settings.LegacySettingsMigrated), "migration marker set");

    await service.UpdateAsync(settings => settings.LastOpenedFolder = null);
    var reloaded = await new AppSettingsService(settingsPath, legacyPath).ReadAsync();
    AssertEqual<string?>(null, reloaded.LastOpenedFolder, "cleared folder is not migrated again");
}

static async Task TestPartialSessionScanAsync()
{
    using var fixture = new TempFixture();
    var claudeHome = Path.Combine(fixture.Root, ".claude");
    const string jsonl = """
        {"type":"session_meta","timestamp":"2026-03-16T12:00:00.000Z","payload":{"id":"session-scan","timestamp":"2026-03-16T12:00:00.000Z","cwd":"C:\\repo","originator":"claude_code","source":"claude_code","model_provider":"anthropic"}}
        {"type":"response_item","timestamp":"2026-03-16T12:00:01.000Z","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"scan fixture"}]}}
        """;
    for (var index = 0; index < 3; index++)
    {
        fixture.WriteSession(jsonl, Path.Combine(".claude", "projects", "repo", $"session-{index}.jsonl"));
    }

    using var cancellation = new CancellationTokenSource();
    var matched = 0;
    var progress = new InlineProgress<SessionScanProgress>(value =>
    {
        if (value.Match is not null && ++matched == 1)
        {
            cancellation.Cancel();
        }
    });
    var service = new ClaudeSessionService();
    var partial = await service.FindClaudeSessionsAsync(
        claudeHome,
        targetDirectory: null,
        cwdOnly: false,
        dateFrom: null,
        dateTo: null,
        includeCrossSessionWrites: false,
        progress: progress,
        cancellationToken: cancellation.Token);

    AssertTrue(!partial.IsComplete, "cancelled scan is partial");
    AssertEqual(1, partial.ScannedFileCount, "partial scanned file count");
    AssertEqual(3, partial.TotalFileCount, "partial total file count");
    AssertEqual(1, partial.Sessions.Count, "partial session count");

    var complete = await service.FindClaudeSessionsAsync(claudeHome, null, false, null, null, false);
    AssertTrue(complete.IsComplete, "refresh scan is complete");
    AssertEqual(3, complete.Sessions.Count, "complete session count");
}

static async Task TestSessionProbeCancellationAsync()
{
    using var fixture = new TempFixture();
    var sessionPath = fixture.WriteSession("""
        {"type":"session_meta","timestamp":"2026-03-16T12:00:00.000Z","payload":{"id":"session-cancel","timestamp":"2026-03-16T12:00:00.000Z","cwd":"C:\\repo"}}
        """);
    var method = typeof(ClaudeSessionService).GetMethod(
        "ProbeSessionFileAsync",
        BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Session probe method not found.");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var task = (Task)(method.Invoke(new ClaudeSessionService(), [sessionPath, cancellation.Token])
        ?? throw new InvalidOperationException("Session probe did not return a task."));

    try
    {
        await task;
        throw new InvalidOperationException("session probe cancellation: expected OperationCanceledException");
    }
    catch (OperationCanceledException)
    {
    }
}

static void WriteScanFixtureSessions(TempFixture fixture, int count)
{
    for (var index = 0; index < count; index++)
    {
        fixture.WriteSession("""
            {"type":"user","sessionId":"scan-@index@","cwd":"C:\\repo","timestamp":"2026-03-16T12:@time@.000Z","message":{"role":"user","content":"prompt @index@"}}
            {"type":"ai-title","sessionId":"scan-@index@","aiTitle":"Session @index@"}
            """.Replace("@index@", index.ToString()).Replace("@time@", $"{index / 60:00}:{index % 60:00}"),
            Path.Combine(".claude", "projects", $"repo-{index % 7}", $"session-{index}.jsonl"));
    }
}

static async Task TestParallelSessionScanAsync()
{
    using var fixture = new TempFixture();
    WriteScanFixtureSessions(fixture, 120);
    var reports = new List<SessionScanProgress>();
    var progress = new InlineProgress<SessionScanProgress>(value => reports.Add(value));
    var service = new ClaudeSessionService();

    var result = await service.FindClaudeSessionsAsync(Path.Combine(fixture.Root, ".claude"), null, false, null, null, false, progress: progress);

    AssertTrue(result.IsComplete, "scan is complete");
    AssertEqual(120, result.ScannedFileCount, "scanned file count");
    AssertEqual(120, result.Sessions.Count, "session count");
    AssertEqual(120, result.Sessions.Select(session => session.Id).Distinct().Count(), "every session appears once");
    AssertEqual("Session 119,Session 118,Session 117", string.Join(",", result.Sessions.Take(3).Select(session => session.ThreadName)), "sessions are sorted by newest first");
    // The files are probed on several threads; the reports still count up one file at a time.
    AssertEqual(0, reports[0].ScannedFileCount, "first report announces the total");
    AssertEqual(string.Join(",", Enumerable.Range(1, 120)), string.Join(",", reports.Skip(1).Select(report => report.ScannedFileCount)), "reports arrive in order");
    AssertEqual(120, reports.Count(report => report.Match is not null), "every match is reported once");
}

static async Task TestSessionTitlesAsync()
{
    using var fixture = new TempFixture();
    const string prompt = """{"type":"user","sessionId":"@id@","cwd":"C:\\repo","timestamp":"2026-03-16T12:00:0@second@.000Z","message":{"role":"user","content":"prompt"}}""";
    string Session(string id, int second, params string[] titleRows)
        => string.Join('\n', new[] { prompt.Replace("@id@", id).Replace("@second@", second.ToString()) }.Concat(titleRows));
    // Claude Code repeats title rows through the file; the last one of each kind is current.
    fixture.WriteSession(Session("custom-only", 1,
        """{"type":"custom-title","sessionId":"custom-only","customTitle":"Old name"}""",
        """{"type":"custom-title","sessionId":"custom-only","customTitle":"Bathroom 2"}"""), Path.Combine(".claude", "projects", "repo", "custom-only.jsonl"));
    fixture.WriteSession(Session("both", 2,
        """{"type":"custom-title","sessionId":"both","customTitle":"Named by the user"}""",
        """{"type":"ai-title","sessionId":"both","aiTitle":"Generated title"}"""), Path.Combine(".claude", "projects", "repo", "both.jsonl"));
    fixture.WriteSession(Session("generated", 3,
        """{"type":"ai-title","sessionId":"generated","aiTitle":"Generated title"}""",
        """{"type":"custom-title","sessionId":"generated","customTitle":"  "}"""), Path.Combine(".claude", "projects", "repo", "generated.jsonl"));
    fixture.WriteSession(Session("untitled", 4), Path.Combine(".claude", "projects", "repo", "untitled.jsonl"));

    var result = await new ClaudeSessionService().FindClaudeSessionsAsync(Path.Combine(fixture.Root, ".claude"), null, false, null, null, false);

    var titles = result.Sessions.ToDictionary(session => session.Id, session => session.ThreadName);
    AssertEqual("Bathroom 2", titles["custom-only"], "a title set by the user names the session");
    AssertEqual("Named by the user", titles["both"], "a title set by the user wins over the generated one");
    AssertEqual("Generated title", titles["generated"], "a blank custom title does not hide the generated one");
    AssertEqual(null, titles["untitled"], "a session without title rows has no title");
}

static async Task TestRefreshScanReportsProgressAsync()
{
    using var fixture = new TempFixture();
    Directory.CreateDirectory(Path.Combine(fixture.Root, ".claude", "projects"));
    var previousHome = Environment.GetEnvironmentVariable("CLAUDE_HOME");
    Environment.SetEnvironmentVariable("CLAUDE_HOME", Path.Combine(fixture.Root, ".claude"));
    try
    {
        var viewModel = new MainWindowViewModel(new ClaudeSessionService(), new NoUiService(), new AppSettingsService(Path.Combine(fixture.Root, "settings.json")));
        await WaitUntilAsync(() => !viewModel.StartupLoadingVisible && !viewModel.StartupScanActive && !viewModel.IsLoading, "startup scan of an empty history");

        WriteScanFixtureSessions(fixture, 60);
        var statuses = new List<string>();
        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(MainWindowViewModel.StatusMessage))
            {
                lock (statuses) statuses.Add(viewModel.StatusMessage);
            }
        };
        // Progress<T> posts to the context it was created in; running the posts inline delivers every report before the scan ends.
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        viewModel.ShowAllSessionsCommand.Execute(null);
        SynchronizationContext.SetSynchronizationContext(null);
        await WaitUntilAsync(() => !viewModel.IsLoading, "All Sessions scan");

        string[] seen;
        lock (statuses) seen = statuses.ToArray();
        AssertEqual("Scanning session files...", seen[0], "the status line switches to the scan at once");
        AssertContains("Scanning session files: 0 of 60, 0 sessions found...", string.Join("\n", seen), "the scan announces its size");
        AssertContains("Scanning session files: 1 of 60, 1 session found...", string.Join("\n", seen), "the scan reports each found session");
        AssertContains("Scanning session files: 60 of 60, 60 sessions found...", string.Join("\n", seen), "the scan reports its last file");
        AssertEqual("Loaded 60 sessions after scanning 60 session files.", seen[^1], "final status");
        AssertEqual(60, viewModel.Sessions.Count, "loaded sessions");
        AssertEqual(100d, viewModel.ScanProgressPercent, "progress bar reaches the end");
        AssertTrue(!viewModel.ScanProgressIndeterminate, "progress bar shows a known amount");
        AssertTrue(!viewModel.HasPartialSessionResults, "no partial-results notice after a complete scan");
    }
    finally
    {
        SynchronizationContext.SetSynchronizationContext(null);
        Environment.SetEnvironmentVariable("CLAUDE_HOME", previousHome);
    }
}

static async Task WaitUntilAsync(Func<bool> condition, string label)
{
    for (var attempt = 0; attempt < 600; attempt++)
    {
        if (condition())
        {
            return;
        }

        await Task.Delay(25);
    }

    throw new TimeoutException($"Timed out waiting for: {label}");
}

static async Task<ExportJobStatus> WaitForExportAsync(Func<ExportJobStatus> readStatus)
{
    for (var attempt = 0; attempt < 200; attempt++)
    {
        var status = readStatus();
        if (status.Kind != "working")
        {
            return status;
        }

        await Task.Delay(25);
    }

    throw new TimeoutException("Timed out waiting for job completion.");
}

static async Task<BatchExportJobStatus> WaitForBatchExportAsync(Func<BatchExportJobStatus> readStatus)
{
    for (var attempt = 0; attempt < 200; attempt++)
    {
        var status = readStatus();
        if (status.Kind != "working")
        {
            return status;
        }

        await Task.Delay(25);
    }

    throw new TimeoutException("Timed out waiting for batch export completion.");
}

static async Task<TokenUsageSummaryJobStatus> WaitForTokenSummaryAsync(Func<TokenUsageSummaryJobStatus> readStatus)
{
    for (var attempt = 0; attempt < 200; attempt++)
    {
        var status = readStatus();
        if (status.Kind != "working")
        {
            return status;
        }

        await Task.Delay(25);
    }

    throw new TimeoutException("Timed out waiting for token summary completion.");
}

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}

static void AssertContains(string expected, string actual, string label)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{label}: expected to find {expected}");
    }
}

static void AssertTrue(bool condition, string label)
{
    if (!condition)
    {
        throw new InvalidOperationException(label);
    }
}

static void AssertInvocationThrows<TException>(Action action, string label)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TargetInvocationException ex) when (ex.InnerException is TException)
    {
        return;
    }

    throw new InvalidOperationException($"{label}: expected {typeof(TException).Name}");
}

sealed class TempFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "clodlogs-tests", Guid.NewGuid().ToString("N"));

    public TempFixture()
    {
        Directory.CreateDirectory(Root);
    }

    public string WriteSession(string jsonl, string fileName = "sample.jsonl")
    {
        var path = Path.Combine(Root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, NormalizeJsonl(jsonl), new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
        }
    }

    private static string NormalizeJsonl(string jsonl)
        => string.Join('\n', jsonl.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)) + "\n";
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class InlineSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) => callback(state);
}

sealed class NoUiService : IUiService
{
    public Task<string?> PickDirectoryAsync(string? startingFolder) => Task.FromResult<string?>(null);
    public Task<string?> PickExportDirectoryAsync(string sessionFilePath) => Task.FromResult<string?>(null);
    public Task<string?> PickExportDirectoryFromAsync(string? startingFolder) => Task.FromResult<string?>(null);
    public Task<string?> PickHtmlExportDestinationAsync(string sessionFilePath, bool includeImages, bool inlineImages) => Task.FromResult<string?>(null);
    public Task<string?> SaveStatisticsTextAsync(string suggestedFileName, string extension, string content) => Task.FromResult<string?>(null);
    public Task<string?> SaveStatisticsImageAsync(string suggestedFileName) => Task.FromResult<string?>(null);
    public Task CopyTextAsync(string text) => Task.CompletedTask;
    public Task<bool> OpenPathAsync(string path) => Task.FromResult(false);
    public Task<bool> RevealPathAsync(string path) => Task.FromResult(false);
    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
}
