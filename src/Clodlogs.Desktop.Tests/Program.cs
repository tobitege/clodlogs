using System.Text;
using System.Reflection;
using Clodlogs.Desktop.Models;
using Clodlogs.Desktop.Services;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("response_item transcript parsing", TestResponseItemTranscriptAsync),
    ("markdown export response items and images", TestMarkdownExportResponseItemsAndImagesAsync),
    ("html export inlines response item images", TestHtmlExportInlineImagesAsync),
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
    ("sanitized copy strips response item images", TestSanitizedCopyAsync)
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
        "CreateAvailableBatchExportPath",
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
