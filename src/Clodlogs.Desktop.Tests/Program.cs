using System.Text;
using Clodlogs.Desktop.Models;
using Clodlogs.Desktop.Services;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("response_item transcript parsing", TestResponseItemTranscriptAsync),
    ("markdown export response items and images", TestMarkdownExportResponseItemsAndImagesAsync),
    ("html export inlines response item images", TestHtmlExportInlineImagesAsync),
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

sealed class TempFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "clodlogs-tests", Guid.NewGuid().ToString("N"));

    public TempFixture()
    {
        Directory.CreateDirectory(Root);
    }

    public string WriteSession(string jsonl)
    {
        var path = Path.Combine(Root, "sample.jsonl");
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
