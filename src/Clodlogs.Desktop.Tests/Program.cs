using System.Text;
using System.Reflection;
using Avalonia;
using Clodlogs.Desktop.Models;
using Clodlogs.Desktop.Services;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("response_item transcript parsing", TestResponseItemTranscriptAsync),
    ("markdown export response items and images", TestMarkdownExportResponseItemsAndImagesAsync),
    ("html export inlines response item images", TestHtmlExportInlineImagesAsync),
    ("native Claude export honors tool options without duplicates", TestNativeClaudeToolOptionsAsync),
    ("native Claude transcript preserves block order and string messages", TestNativeClaudeTranscriptAsync),
    ("native Claude images honor image and tool options", TestNativeClaudeImageOptionsAsync),
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
