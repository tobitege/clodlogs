using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clodlogs.Desktop.Models;

namespace Clodlogs.Desktop.Services;

public sealed class ClaudeSessionService
{
    public const long LargeSessionWarningBytes = 64L * 1024 * 1024;
    public const long AutoDetailParseLimitBytes = 128L * 1024 * 1024;
    public const int MaxJsonlLineBytesHard = 32 * 1024 * 1024;
    public const int ExportMaxJsonlLineBytes = 128 * 1024 * 1024;
    private const int BlobTextThresholdBytes = 4096;
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly Dictionary<string, JobRecord<ExportJobStatus>> _exportJobs = new();
    private readonly Dictionary<string, JobRecord<ExportJobStatus>> _sanitizedJobs = new();
    private readonly Dictionary<string, JobRecord<TokenUsageSummaryJobStatus>> _tokenSummaryJobs = new();
    private readonly object _gate = new();

    public string DefaultClaudeHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude");

    public async Task<FindClaudeSessionsResult> FindClaudeSessionsAsync(
        string? claudeHome,
        string? targetDirectory,
        bool cwdOnly,
        string? dateFrom,
        string? dateTo,
        bool includeCrossSessionWrites,
        string? currentWorkingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var currentDirectory = Path.GetFullPath(currentWorkingDirectory ?? Environment.CurrentDirectory);
        var resolvedClaudeHome = ResolveFilesystemPath(
            NormalizeOptionalPathInput(claudeHome) ?? Environment.GetEnvironmentVariable("CLAUDE_HOME") ?? DefaultClaudeHome);
        var normalizedTarget = NormalizeOptionalPathInput(targetDirectory);
        string? requestedDirectory = null;
        ScopeMode scopeMode = ScopeMode.All;
        string? targetRoot = null;
        List<ComparablePathAlias>? targetRootAliases = null;

        if (normalizedTarget is not null)
        {
            requestedDirectory = normalizedTarget.Length == 0 ? currentDirectory : ResolveFilesystemPath(normalizedTarget);
            var repoRoot = cwdOnly ? null : FindRepoRoot(requestedDirectory);
            targetRoot = repoRoot ?? requestedDirectory;
            targetRootAliases = GetComparablePathAliases(targetRoot);
            scopeMode = repoRoot is null ? ScopeMode.Cwd : ScopeMode.Repo;
        }

        var projectsDirectory = Path.Combine(resolvedClaudeHome, "projects");
        var files = Directory.Exists(projectsDirectory)
            ? Directory.EnumerateFiles(projectsDirectory, "*.jsonl", SearchOption.AllDirectories).ToList()
            : [];
        var dateRange = DateRange.Create(dateFrom, dateTo);
        HashSet<string>? candidateFiles = null;
        if (targetRootAliases is not null)
        {
            candidateFiles = await FindCandidateSessionFilesAsync(files, targetRootAliases, cancellationToken);
        }

        var matches = new List<SessionMetaMatch>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidateFiles is not null && !candidateFiles.Contains(NormalizeFileLookupPath(file)))
            {
                continue;
            }

            var probe = await ProbeSessionFileAsync(file, cancellationToken);
            if (probe is null || !dateRange.Matches(probe.StartedAt, probe.UpdatedAt))
            {
                continue;
            }

            if (targetRootAliases is not null)
            {
                var cwdMatches = MatchesRootAliases(probe.Cwd, targetRootAliases);
                if (!cwdMatches && (!includeCrossSessionWrites || !await SessionTouchesRootAsync(file, targetRootAliases, probe.Cwd, cancellationToken)))
                {
                    continue;
                }
            }

            matches.Add(new SessionMetaMatch(
                SessionKind.Live,
                probe.Id,
                file,
                probe.FileSizeBytes,
                probe.Cwd,
                probe.StartedAt,
                probe.UpdatedAt,
                probe.ThreadName,
                probe.Source));
        }

        matches.Sort((left, right) => CompareByNewestDesc(left, right));
        var liveCount = matches.Count(session => session.Kind == SessionKind.Live);
        return new FindClaudeSessionsResult(
            resolvedClaudeHome,
            currentDirectory,
            requestedDirectory,
            scopeMode,
            targetRoot,
            matches.Count,
            liveCount,
            matches.Count - liveCount,
            matches);
    }

    public async Task<SessionDetailMetrics> GetSessionDetailMetricsAsync(
        string inputPath,
        bool forceDeepAnalysis,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveFilesystemPath(inputPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Session file not found: {path}", path);
        }

        var fileSize = new FileInfo(path).Length;
        if (fileSize > AutoDetailParseLimitBytes && !forceDeepAnalysis)
        {
            return new SessionDetailMetrics(
                0,
                0,
                null,
                fileSize,
                "skipped",
                $"Automatic analysis is disabled for sessions larger than {FormatByteCount(AutoDetailParseLimitBytes)}. Use Analyze Anyway to run a bounded scan.",
                null,
                0);
        }

        var interactions = 0;
        var toolCalls = 0;
        var usageByMessage = new Dictionary<string, SessionTokenUsage>();
        long largestParsedLine = 0;
        var oversizedLineCount = 0;

        await foreach (var evt in StreamJsonlRecordsAsync(path, MaxJsonlLineBytesHard, false, cancellationToken))
        {
            if (evt.Oversized)
            {
                largestParsedLine = Math.Max(largestParsedLine, evt.ByteLength);
                oversizedLineCount++;
                continue;
            }

            largestParsedLine = Math.Max(largestParsedLine, evt.ByteLength);
            if (evt.Record is null)
            {
                continue;
            }

            if (IsHumanClaudeUserMessage(evt.Record))
            {
                interactions++;
            }

            toolCalls += CountClaudeToolUses(evt.Record);
            AddClaudeUsageFromRecord(evt.Record, usageByMessage);
        }

        return new SessionDetailMetrics(
            interactions,
            toolCalls,
            SumClaudeTokenUsage(usageByMessage),
            fileSize,
            oversizedLineCount > 0 ? "partial" : "full",
            oversizedLineCount > 0
                ? $"{oversizedLineCount} oversized row{(oversizedLineCount == 1 ? "" : "s")} exceeded the detail-analysis limit of {FormatByteCount(MaxJsonlLineBytesHard)} and were skipped."
                : null,
            largestParsedLine > 0 ? largestParsedLine : null,
            oversizedLineCount);
    }

    public async Task<(SessionTokenUsage? TokenUsage, long FileSizeBytes, int OversizedLineCount, int TokenCountRows)> ReadSessionTokenUsageAsync(
        string inputPath,
        IProgress<(long BytesProcessed, long FileSizeBytes)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveFilesystemPath(inputPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Session file not found: {path}", path);
        }

        var fileSize = new FileInfo(path).Length;
        var usageByMessage = new Dictionary<string, SessionTokenUsage>();
        var oversizedLineCount = 0;

        await foreach (var evt in StreamJsonlRecordsAsync(path, MaxJsonlLineBytesHard, false, cancellationToken))
        {
            if (evt.Oversized)
            {
                oversizedLineCount++;
            }
            else if (evt.Record is not null)
            {
                AddClaudeUsageFromRecord(evt.Record, usageByMessage);
            }

            progress?.Report((Math.Min(evt.BytesProcessed, fileSize), fileSize));
        }

        progress?.Report((fileSize, fileSize));
        return (SumClaudeTokenUsage(usageByMessage), fileSize, oversizedLineCount, usageByMessage.Count);
    }

    public async Task<SessionTranscriptResult> ReadSessionTranscriptAsync(
        string inputPath,
        int maxEntries = 5000,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveFilesystemPath(inputPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Session file not found: {path}", path);
        }

        var entries = new List<SessionTranscriptEntry>();
        string? sessionId = null;
        string? cwd = null;
        string? startedAt = null;
        var truncated = false;
        var omittedBootstrapMessages = 0;
        var oversizedLineCount = 0;
        var sawFirstUserMessage = false;

        await foreach (var evt in StreamJsonlRecordsAsync(path, MaxJsonlLineBytesHard, false, cancellationToken))
        {
            if (evt.Oversized)
            {
                oversizedLineCount++;
                continue;
            }

            if (evt.Record is null)
            {
                continue;
            }

            var record = evt.Record;
            sessionId ??= GetString(record, "sessionId");
            cwd ??= GetString(record, "cwd");
            startedAt ??= GetString(record, "timestamp");

            var result = BuildTranscriptEntries(record, entries.Count, sawFirstUserMessage);
            sawFirstUserMessage = result.SawFirstUserMessage;
            omittedBootstrapMessages += result.OmittedBootstrapMessages;
            foreach (var entry in result.Entries)
            {
                entries.Add(entry);
                if (entries.Count >= Math.Max(1, maxEntries))
                {
                    truncated = true;
                    break;
                }
            }

            if (truncated)
            {
                break;
            }
        }

        return new SessionTranscriptResult(
            sessionId,
            cwd,
            startedAt,
            new FileInfo(path).Length,
            entries,
            truncated,
            omittedBootstrapMessages,
            oversizedLineCount);
    }

    public async Task<EnvironmentCapabilities> GetEnvironmentCapabilitiesAsync(string? claudeHome)
    {
        var resolvedClaudeHome = ResolveFilesystemPath(
            NormalizeOptionalPathInput(claudeHome) ?? Environment.GetEnvironmentVariable("CLAUDE_HOME") ?? DefaultClaudeHome);
        var claudeHomeReadable = CanReadDirectory(resolvedClaudeHome);
        var claudeHomeWritable = CanWriteDirectoryOrParent(resolvedClaudeHome);
        var gitAvailable = IsCommandAvailable("git", "--version");
        var rgAvailable = IsCommandAvailable("rg", "--version");
        var notes = new List<string>();

        if (!claudeHomeReadable)
        {
            notes.Add("Claude home is missing or not readable. Session browsing will stay empty until this path becomes available.");
        }
        if (!claudeHomeWritable)
        {
            notes.Add("Claude home is not writable. Temporary sanitized copies may still be available.");
        }
        if (!gitAvailable)
        {
            notes.Add("git is not available. Repo-root detection falls back to walking parent folders.");
        }
        if (!rgAvailable)
        {
            notes.Add("rg is not available. Content scans still work, but they fall back to a slower file-by-file search.");
        }

        var overall = !claudeHomeReadable
            ? "error"
            : !claudeHomeWritable || !gitAvailable || !rgAvailable
                ? "warning"
                : "success";
        var summary = overall switch
        {
            "success" => "All runtime capabilities are available.",
            "error" => "Claude home is not ready for normal session browsing.",
            _ => "Some runtime capabilities are limited, but clodlogs can still run."
        };

        await Task.CompletedTask;
        return new EnvironmentCapabilities(
            resolvedClaudeHome,
            claudeHomeReadable,
            claudeHomeWritable,
            gitAvailable,
            rgAvailable,
            overall,
            summary,
            notes);
    }

    public JobStartResult StartExportJob(
        string format,
        string sessionFilePath,
        bool includeImages,
        bool inlineImages,
        bool includeToolCallResults,
        string? outputDirectory,
        string? outputPath)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var record = new JobRecord<ExportJobStatus>(
            new CancellationTokenSource(),
            new ExportJobStatus("working", 2, "reading", $"Preparing {format} export...", null));
        lock (_gate)
        {
            _exportJobs[jobId] = record;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var path = format == "markdown"
                    ? await ExportSessionJsonlToMarkdownAsync(sessionFilePath, includeImages, includeToolCallResults, outputDirectory, progress => SetExportStatus(jobId, progress), record.Cancellation.Token)
                    : await ExportSessionJsonlToHtmlAsync(sessionFilePath, includeImages, inlineImages, includeToolCallResults, outputDirectory, outputPath, progress => SetExportStatus(jobId, progress), record.Cancellation.Token);
                SetExportStatus(jobId, new ExportJobStatus("success", 100, "done", $"{ToTitleCase(format)} written to {path}", path));
            }
            catch (OperationCanceledException)
            {
                var current = GetExportJobStatus(jobId);
                SetExportStatus(jobId, current with { Kind = "cancelled", Stage = "cancelled", Message = "Export cancelled.", OutputPath = null });
            }
            catch (Exception ex)
            {
                var current = GetExportJobStatus(jobId);
                SetExportStatus(jobId, current with { Kind = "error", Stage = "error", Message = ex.Message, OutputPath = null });
            }
        });

        return new JobStartResult(jobId);
    }

    public ExportJobStatus GetExportJobStatus(string jobId)
    {
        lock (_gate)
        {
            return _exportJobs.TryGetValue(jobId, out var record)
                ? record.Status
                : new ExportJobStatus("error", 0, "missing", "The export job is no longer available.", null);
        }
    }

    public bool CancelExportJob(string jobId)
    {
        lock (_gate)
        {
            if (!_exportJobs.TryGetValue(jobId, out var record) || record.Status.Kind != "working")
            {
                return false;
            }

            record.Cancellation.Cancel();
            record.Status = record.Status with { Message = "Cancelling export..." };
            return true;
        }
    }

    public JobStartResult StartTokenUsageSummaryJob(IReadOnlyList<string> sessionFilePaths)
    {
        var distinctPaths = sessionFilePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var jobId = Guid.NewGuid().ToString("N");
        var record = new JobRecord<TokenUsageSummaryJobStatus>(
            new CancellationTokenSource(),
            new TokenUsageSummaryJobStatus("working", 1, "starting", $"Preparing {distinctPaths.Length} session{(distinctPaths.Length == 1 ? "" : "s")}...", 0, distinctPaths.Length, null, null));
        lock (_gate)
        {
            _tokenSummaryJobs[jobId] = record;
        }

        _ = Task.Run(async () =>
        {
            var totalUsage = SessionTokenUsage.Empty;
            var scanned = 0;
            var withUsage = 0;
            var withoutUsage = 0;
            var failed = 0;
            long fileSizeBytes = 0;
            var oversizedLineCount = 0;
            var tokenRows = 0;

            try
            {
                for (var index = 0; index < distinctPaths.Length; index++)
                {
                    record.Cancellation.Token.ThrowIfCancellationRequested();
                    var path = distinctPaths[index];
                    SetTokenSummaryStatus(jobId, new TokenUsageSummaryJobStatus(
                        "working",
                        Math.Max(2, (int)Math.Round(index / Math.Max(1d, distinctPaths.Length) * 96)),
                        "scanning",
                        $"Scanning {Path.GetFileName(path)}...",
                        scanned,
                        distinctPaths.Length,
                        path,
                        null));

                    try
                    {
                        var progressReporter = new Progress<(long BytesProcessed, long FileSizeBytes)>(progress =>
                        {
                            var ratio = (index + Math.Min(1d, progress.BytesProcessed / (double)Math.Max(1, progress.FileSizeBytes))) / Math.Max(1d, distinctPaths.Length);
                            SetTokenSummaryStatus(jobId, new TokenUsageSummaryJobStatus(
                                "working",
                                Math.Max(2, Math.Min(98, (int)Math.Round(ratio * 98))),
                                "scanning",
                                $"Scanning {Path.GetFileName(path)}...",
                                scanned,
                                distinctPaths.Length,
                                path,
                                null));
                        });
                        var result = await ReadSessionTokenUsageAsync(path, progressReporter, record.Cancellation.Token);

                        scanned++;
                        fileSizeBytes += result.FileSizeBytes;
                        oversizedLineCount += result.OversizedLineCount;
                        tokenRows += result.TokenCountRows;
                        if (result.TokenUsage is not null)
                        {
                            totalUsage = totalUsage.Add(result.TokenUsage);
                            withUsage++;
                        }
                        else
                        {
                            withoutUsage++;
                        }
                    }
                    catch
                    {
                        record.Cancellation.Token.ThrowIfCancellationRequested();
                        scanned++;
                        failed++;
                    }
                }

                var summary = new TokenUsageSummaryResult(
                    distinctPaths.Length,
                    scanned,
                    withUsage,
                    withoutUsage,
                    failed,
                    fileSizeBytes,
                    oversizedLineCount,
                    tokenRows,
                    totalUsage);
                SetTokenSummaryStatus(jobId, new TokenUsageSummaryJobStatus(
                    "success",
                    100,
                    "done",
                    $"Summarized {withUsage} session{(withUsage == 1 ? "" : "s")} with token data.",
                    scanned,
                    distinctPaths.Length,
                    null,
                    summary));
            }
            catch (OperationCanceledException)
            {
                var current = GetTokenUsageSummaryJobStatus(jobId);
                SetTokenSummaryStatus(jobId, current with { Kind = "cancelled", Stage = "cancelled", Message = "Token summary cancelled.", CurrentSessionPath = null });
            }
            catch (Exception ex)
            {
                var current = GetTokenUsageSummaryJobStatus(jobId);
                SetTokenSummaryStatus(jobId, current with { Kind = "error", Stage = "error", Message = ex.Message, CurrentSessionPath = null });
            }
        });

        return new JobStartResult(jobId);
    }

    public TokenUsageSummaryJobStatus GetTokenUsageSummaryJobStatus(string jobId)
    {
        lock (_gate)
        {
            return _tokenSummaryJobs.TryGetValue(jobId, out var record)
                ? record.Status
                : new TokenUsageSummaryJobStatus("error", 0, "missing", "The token summary job is no longer available.", 0, 0, null, null);
        }
    }

    public bool CancelTokenUsageSummaryJob(string jobId)
    {
        lock (_gate)
        {
            if (!_tokenSummaryJobs.TryGetValue(jobId, out var record) || record.Status.Kind != "working")
            {
                return false;
            }

            record.Cancellation.Cancel();
            record.Status = record.Status with { Message = "Cancelling token summary..." };
            return true;
        }
    }

    public JobStartResult StartSanitizedCopyJob(
        string sessionFilePath,
        string? claudeHome,
        string? chatName,
        bool stripImageContent,
        bool stripBlobContent,
        bool createJsonlCopy,
        bool reAddToCurrentDay)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var record = new JobRecord<ExportJobStatus>(
            new CancellationTokenSource(),
            new ExportJobStatus("working", 2, "reading", "Preparing sanitized session output...", null));
        lock (_gate)
        {
            _sanitizedJobs[jobId] = record;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var output = await CreateSanitizedSessionCopyAsync(
                    sessionFilePath,
                    claudeHome,
                    chatName,
                    stripImageContent,
                    stripBlobContent,
                    createJsonlCopy,
                    reAddToCurrentDay,
                    progress => SetSanitizedStatus(jobId, progress),
                    record.Cancellation.Token);
                SetSanitizedStatus(jobId, new ExportJobStatus("success", 100, "done", $"Sanitized session output written into {output}", output));
            }
            catch (OperationCanceledException)
            {
                var current = GetSanitizedCopyJobStatus(jobId);
                SetSanitizedStatus(jobId, current with { Kind = "cancelled", Stage = "cancelled", Message = "Sanitized session job cancelled.", OutputPath = null });
            }
            catch (Exception ex)
            {
                var current = GetSanitizedCopyJobStatus(jobId);
                SetSanitizedStatus(jobId, current with { Kind = "error", Stage = "error", Message = ex.Message, OutputPath = null });
            }
        });

        return new JobStartResult(jobId);
    }

    public ExportJobStatus GetSanitizedCopyJobStatus(string jobId)
    {
        lock (_gate)
        {
            return _sanitizedJobs.TryGetValue(jobId, out var record)
                ? record.Status
                : new ExportJobStatus("error", 0, "missing", "The text-only copy job is no longer available.", null);
        }
    }

    public bool CancelSanitizedCopyJob(string jobId)
    {
        lock (_gate)
        {
            if (!_sanitizedJobs.TryGetValue(jobId, out var record) || record.Status.Kind != "working")
            {
                return false;
            }

            record.Cancellation.Cancel();
            record.Status = record.Status with { Message = "Cancelling sanitization..." };
            return true;
        }
    }

    public async Task RenameSessionThreadNameAsync(string? claudeHome, string threadId, string threadName)
    {
        await Task.CompletedTask;
        throw new NotSupportedException("Renaming Claude project-log sessions is not supported.");
    }

    public string FormatTokenUsageForClipboard(string sessionTitle, SessionTokenUsage usage)
        => string.Join(Environment.NewLine, [
            $"Session: {sessionTitle}",
            $"Total tokens: {usage.TotalTokens}",
            $"Input tokens: {usage.InputTokens}",
            $"Cached input tokens: {usage.CachedInputTokens}",
            $"Uncached input tokens: {Math.Max(0, usage.InputTokens - usage.CachedInputTokens)}",
            $"Output tokens: {usage.OutputTokens}",
            $"Reasoning output tokens: {usage.ReasoningOutputTokens}"
        ]);

    public string FormatTokenUsageSummaryForClipboard(TokenUsageSummaryResult summary)
        => string.Join(Environment.NewLine, [
            "Filtered sessions token summary",
            $"Sessions: {summary.SessionCount}",
            $"Scanned sessions: {summary.ScannedSessionCount}",
            $"Sessions with token usage: {summary.SessionsWithTokenUsage}",
            $"Sessions without token usage: {summary.SessionsWithoutTokenUsage}",
            $"Failed sessions: {summary.FailedSessionCount}",
            $"Total file size: {summary.FileSizeBytes}",
            $"Oversized JSONL rows: {summary.OversizedLineCount}",
            "",
            $"Total tokens: {summary.TokenUsage.TotalTokens}",
            $"Input tokens: {summary.TokenUsage.InputTokens}",
            $"Cached input tokens: {summary.TokenUsage.CachedInputTokens}",
            $"Uncached input tokens: {Math.Max(0, summary.TokenUsage.InputTokens - summary.TokenUsage.CachedInputTokens)}",
            $"Output tokens: {summary.TokenUsage.OutputTokens}",
            $"Reasoning output tokens: {summary.TokenUsage.ReasoningOutputTokens}"
        ]);

    public static string ResolveFilesystemPath(string inputPath)
    {
        var trimmed = inputPath.Trim();
        if (OperatingSystem.IsWindows())
        {
            return NormalizeWindowsPath(trimmed)
                ?? WslMountPathToWindowsDrive(NormalizeWslMountPath(trimmed))
                ?? ParseWslUncPath(trimmed)?.WinPath
                ?? Path.GetFullPath(trimmed);
        }

        return NormalizeAbsolutePosixPath(trimmed)
            ?? WindowsDrivePathToWslMount(NormalizeWindowsPath(trimmed))
            ?? ParseWslUncPath(trimmed)?.PosixPath
            ?? Path.GetFullPath(trimmed);
    }

    public static string FormatByteCount(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = -1;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{(value >= 100 ? value.ToString("0") : value >= 10 ? value.ToString("0.0") : value.ToString("0.00"))} {units[index]}";
    }

    public static string SanitizeSessionTitleInput(string? value)
    {
        var normalized = (value ?? "").Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsControl(ch) || ch is '\u2028' or '\u2029')
            {
                builder.Append(' ');
            }
            else if (ch is '\u200E' or '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
            {
            }
            else
            {
                builder.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
            }
        }

        var collapsed = string.Join(" ", builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 160 ? collapsed : collapsed[..160].Trim();
    }

    private async Task<string> ExportSessionJsonlToMarkdownAsync(
        string inputPath,
        bool includeImages,
        bool includeToolCallResults,
        string? outputDirectory,
        Action<ExportJobStatus> progress,
        CancellationToken cancellationToken)
    {
        var sessionFilePath = ResolveFilesystemPath(inputPath);
        EnsureJsonl(sessionFilePath);
        var outputDirectoryPath = ResolveOutputDirectory(sessionFilePath, outputDirectory);
        Directory.CreateDirectory(outputDirectoryPath);
        var outputPath = Path.Combine(outputDirectoryPath, $"{Path.GetFileNameWithoutExtension(sessionFilePath)}.md");
        var meta = await ReadSessionExportMetadataAsync(sessionFilePath, cancellationToken);
        var fileSize = new FileInfo(sessionFilePath).Length;
        progress(new ExportJobStatus("working", 4, "reading", "Preparing Markdown export...", null));

        var assetDirectoryName = $"{Path.GetFileNameWithoutExtension(outputPath)}-assets";
        var imageContext = new ExportImageContext(ExportImageRenderMode.Markdown, includeImages, false, outputDirectoryPath, assetDirectoryName);
        try
        {
            await using var stream = File.Create(outputPath);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync("# Claude Session Export");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync($"- Source JSONL: `{sessionFilePath}`");
            await writer.WriteLineAsync($"- Session ID: `{meta.Id}`");
            await writer.WriteLineAsync($"- Started: {meta.StartedAt ?? "unknown"}");
            await writer.WriteLineAsync($"- CWD: `{meta.Cwd}`");
            await writer.WriteLineAsync($"- Originator: {meta.Originator ?? "unknown"}");
            await writer.WriteLineAsync($"- CLI Version: {meta.CliVersion ?? "unknown"}");
            await writer.WriteLineAsync($"- Source: {meta.Source ?? "unknown"}");
            await writer.WriteLineAsync($"- Model Provider: {meta.ModelProvider ?? "unknown"}");
            await writer.WriteLineAsync($"- Included images: {(includeImages ? "yes" : "no")}");
            await writer.WriteLineAsync($"- Included tool calls and results: {(includeToolCallResults ? "yes" : "no")}");
            await writer.WriteLineAsync($"- Exported: {DateTimeOffset.Now:O}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("## Transcript");
            await writer.WriteLineAsync();

            var transcriptCount = 0;
            var omittedBootstrapMessages = 0;
            var sawFirstUser = false;
            await foreach (var evt in StreamJsonlRecordsAsync(sessionFilePath, ExportMaxJsonlLineBytes, true, cancellationToken))
            {
                if (evt.Record is null)
                {
                    continue;
                }

                var built = BuildTranscriptEntries(evt.Record, transcriptCount, sawFirstUser, imageContext);
                sawFirstUser = built.SawFirstUserMessage;
                omittedBootstrapMessages += built.OmittedBootstrapMessages;
                foreach (var entry in built.Entries.Where(entry => ShouldIncludeTranscriptEntry(entry, includeToolCallResults)))
                {
                    if (transcriptCount > 0)
                    {
                        await writer.WriteLineAsync();
                        await writer.WriteLineAsync();
                    }

                    await writer.WriteAsync(RenderMarkdownTranscriptEntry(entry));
                    transcriptCount++;
                }

                ReportByteProgress(progress, evt.BytesProcessed, fileSize, "rendering", 10, 82, "Rendering Markdown transcript...");
            }

            if (omittedBootstrapMessages > 0)
            {
                await writer.WriteLineAsync();
                await writer.WriteLineAsync($"_Omitted bootstrap messages: {omittedBootstrapMessages}_");
            }

            if (transcriptCount == 0)
            {
                await writer.WriteLineAsync("_No transcript items were found in the response stream._");
            }
        }
        catch
        {
            TryDeleteFile(outputPath);
            imageContext.CleanupAssetDirectory();
            throw;
        }

        progress(new ExportJobStatus("working", 100, "writing", "Markdown export ready.", outputPath));
        return outputPath;
    }

    private async Task<string> ExportSessionJsonlToHtmlAsync(
        string inputPath,
        bool includeImages,
        bool inlineImages,
        bool includeToolCallResults,
        string? outputDirectory,
        string? outputPath,
        Action<ExportJobStatus> progress,
        CancellationToken cancellationToken)
    {
        var sessionFilePath = ResolveFilesystemPath(inputPath);
        EnsureJsonl(sessionFilePath);
        var resolvedOutputPath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(ResolveOutputDirectory(sessionFilePath, outputDirectory), $"{Path.GetFileNameWithoutExtension(sessionFilePath)}.html")
            : EnsureHtmlOutputPath(ResolveFilesystemPath(outputPath));
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedOutputPath)!);
        var meta = await ReadSessionExportMetadataAsync(sessionFilePath, cancellationToken);
        var fileSize = new FileInfo(sessionFilePath).Length;
        progress(new ExportJobStatus("working", 4, "reading", "Preparing HTML export...", null));

        var htmlOutputDirectory = Path.GetDirectoryName(resolvedOutputPath)!;
        var htmlAssetDirectoryName = $"{Path.GetFileNameWithoutExtension(resolvedOutputPath)}-assets";
        var htmlImageContext = new ExportImageContext(ExportImageRenderMode.Html, includeImages, inlineImages, htmlOutputDirectory, htmlAssetDirectoryName);
        try
        {
            await using var stream = File.Create(resolvedOutputPath);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(BuildHtmlExportPrefix(sessionFilePath, meta, includeImages, inlineImages, includeToolCallResults));

            var transcriptCount = 0;
            var sawFirstUser = false;
            await foreach (var evt in StreamJsonlRecordsAsync(sessionFilePath, ExportMaxJsonlLineBytes, true, cancellationToken))
            {
                if (evt.Record is null)
                {
                    continue;
                }

                var built = BuildTranscriptEntries(evt.Record, transcriptCount, sawFirstUser, htmlImageContext);
                sawFirstUser = built.SawFirstUserMessage;
                foreach (var entry in built.Entries.Where(entry => ShouldIncludeTranscriptEntry(entry, includeToolCallResults)))
                {
                    await writer.WriteLineAsync(RenderHtmlTranscriptEntry(entry));
                    transcriptCount++;
                }

                ReportByteProgress(progress, evt.BytesProcessed, fileSize, "rendering", 10, 82, "Rendering HTML transcript...");
            }

            if (transcriptCount == 0)
            {
                await writer.WriteLineAsync("<p class=\"empty\">No transcript items were found in the response stream.</p>");
            }

            await writer.WriteLineAsync("</main></body></html>");
        }
        catch
        {
            TryDeleteFile(resolvedOutputPath);
            htmlImageContext.CleanupAssetDirectory();
            throw;
        }

        progress(new ExportJobStatus("working", 100, "writing", "HTML export ready.", resolvedOutputPath));
        return resolvedOutputPath;
    }

    private async Task<string> CreateSanitizedSessionCopyAsync(
        string sessionFilePath,
        string? claudeHome,
        string? chatName,
        bool stripImageContent,
        bool stripBlobContent,
        bool createJsonlCopy,
        bool reAddToCurrentDay,
        Action<ExportJobStatus> progress,
        CancellationToken cancellationToken)
    {
        if (reAddToCurrentDay)
        {
            throw new NotSupportedException("Re-adding sessions is not supported for Claude project-log storage.");
        }

        var source = ResolveFilesystemPath(sessionFilePath);
        EnsureJsonl(source);
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH-mm-ss");
        var safeStem = SanitizeFileStem(Path.GetFileNameWithoutExtension(source));
        var root = Path.Combine(Path.GetTempPath(), "clodlogs", "sanitized-sessions");
        Directory.CreateDirectory(root);
        var outputDirectory = Path.Combine(root, $"{safeStem}-{timestamp}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var sanitizedPath = Path.Combine(outputDirectory, "sanitized-session.jsonl");
        var reportPath = Path.Combine(outputDirectory, "sanitization-report.json");
        progress(new ExportJobStatus("working", 6, "reading", "Preparing temporary output folder...", null));

        SanitizedSessionStats stats = new();
        if (createJsonlCopy)
        {
            await using var readerStream = File.OpenRead(source);
            using var reader = new StreamReader(readerStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            await using var writerStream = File.Create(sanitizedPath);
            await using var writer = new StreamWriter(writerStream, new UTF8Encoding(false));
            string? line;
            var lineNumber = 0;
            var fileSize = new FileInfo(source).Length;
            long processed = 0;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                processed += Encoding.UTF8.GetByteCount(line) + 1;
                var outputLine = lineNumber == 1
                    ? BuildSanitizedSessionMetaRecord(source, claudeHome, chatName, stripImageContent, stripBlobContent, createJsonlCopy, reAddToCurrentDay)
                    : SanitizeJsonlLine(line, stripImageContent, stripBlobContent, stats) ?? line;
                await writer.WriteLineAsync(outputLine);
                if (lineNumber % 25 == 0)
                {
                    var percent = fileSize > 0 ? 20 + (int)Math.Floor(processed / (double)fileSize * 72) : 92;
                    progress(new ExportJobStatus("working", Math.Min(95, percent), "writing", "Writing sanitized JSONL in original line order...", null));
                }
            }
        }

        var report = new JsonObject
        {
            ["generatedAt"] = DateTimeOffset.Now.ToString("O"),
            ["originalSessionPath"] = source,
            ["sanitizedSessionPath"] = createJsonlCopy ? sanitizedPath : null,
            ["chatName"] = NormalizeSessionTitle(chatName),
            ["options"] = new JsonObject
            {
                ["claudeHome"] = ResolveFilesystemPath(NormalizeOptionalPathInput(claudeHome) ?? Environment.GetEnvironmentVariable("CLAUDE_HOME") ?? DefaultClaudeHome),
                ["stripImageContent"] = stripImageContent,
                ["stripBlobContent"] = stripBlobContent,
                ["createJsonlCopy"] = createJsonlCopy,
                ["reAddToCurrentDay"] = reAddToCurrentDay
            },
            ["reconstructionStats"] = stats.ToJson()
        };
        await File.WriteAllTextAsync(reportPath, report.ToJsonString(IndentedJsonOptions), Encoding.UTF8, cancellationToken);
        progress(new ExportJobStatus("working", 100, "done", "Sanitized session copy is ready.", outputDirectory));
        return outputDirectory;
    }

    private static void EnsureJsonl(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Session file not found: {path}", path);
        }
        if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected a .jsonl file: {path}");
        }
    }

    private void SetExportStatus(string jobId, ExportJobStatus status)
    {
        lock (_gate)
        {
            if (_exportJobs.TryGetValue(jobId, out var record))
            {
                record.Status = status;
            }
        }
    }

    private void SetExportStatus(string jobId, ExportProgress progress)
        => SetExportStatus(jobId, new ExportJobStatus("working", progress.ProgressPercent, progress.Stage, progress.Message, null));

    private void SetSanitizedStatus(string jobId, ExportJobStatus status)
    {
        lock (_gate)
        {
            if (_sanitizedJobs.TryGetValue(jobId, out var record))
            {
                record.Status = status;
            }
        }
    }

    private void SetTokenSummaryStatus(string jobId, TokenUsageSummaryJobStatus status)
    {
        lock (_gate)
        {
            if (_tokenSummaryJobs.TryGetValue(jobId, out var record))
            {
                record.Status = status;
            }
        }
    }

    private static void ReportByteProgress(Action<ExportJobStatus> progress, long processed, long total, string stage, int start, int end, string message)
    {
        var ratio = processed / (double)Math.Max(1, total);
        var percent = Math.Clamp(start + (int)Math.Round(ratio * (end - start)), start, end);
        progress(new ExportJobStatus("working", percent, stage, message, null));
    }

    private async IAsyncEnumerable<JsonlRecordEvent> StreamJsonlRecordsAsync(
        string filePath,
        int maxLineBytes,
        bool throwOnOversized,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
        string? line;
        var lineNumber = 0;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var byteLength = Encoding.UTF8.GetByteCount(line);
            var bytesProcessed = stream.Position;
            if (byteLength > maxLineBytes)
            {
                if (throwOnOversized)
                {
                    throw new InvalidOperationException($"Line {lineNumber} in {filePath} is {FormatByteCount(byteLength)}, which exceeds the limit of {FormatByteCount(maxLineBytes)}.");
                }

                yield return JsonlRecordEvent.AsOversized(lineNumber, byteLength, bytesProcessed);
                continue;
            }

            if (lineNumber == 1)
            {
                line = line.TrimStart('\uFEFF');
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? record = null;
            try
            {
                record = JsonNode.Parse(line) as JsonObject;
            }
            catch
            {
            }

            if (record is not null)
            {
                yield return JsonlRecordEvent.AsRecord(lineNumber, byteLength, bytesProcessed, record);
            }
        }
    }

    private async Task<SessionProbe?> ProbeSessionFileAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return null;
            }

            string? id = null;
            string? cwd = null;
            string? startedAt = null;
            string? updatedAt = null;
            string? threadName = null;
            string? source = "claude";

            await foreach (var evt in StreamJsonlRecordsAsync(filePath, MaxJsonlLineBytesHard, false, cancellationToken))
            {
                if (evt.Record is null)
                {
                    continue;
                }

                var record = evt.Record;
                var timestamp = GetString(record, "timestamp");
                if (!string.IsNullOrWhiteSpace(timestamp))
                {
                    startedAt ??= timestamp;
                    updatedAt = timestamp;
                }

                id ??= GetString(record, "sessionId");
                cwd ??= GetString(record, "cwd");
                var type = GetString(record, "type");
                if (type == "ai-title")
                {
                    var title = GetString(record, "aiTitle")?.Trim();
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        threadName = title;
                    }
                }

                var payload = record["payload"] as JsonObject;
                source ??= GetString(payload, "source");
            }

            id ??= Path.GetFileNameWithoutExtension(filePath);
            cwd ??= DecodeClaudeProjectFolderPath(Path.GetFileName(Path.GetDirectoryName(filePath) ?? "")) ?? Path.GetDirectoryName(filePath) ?? "";
            updatedAt ??= startedAt ?? info.LastWriteTimeUtc.ToString("O");
            return new SessionProbe(id, info.Length, cwd, startedAt, updatedAt, threadName, source);
        }
        catch
        {
            return null;
        }
    }

    private static TranscriptBuildResult BuildTranscriptEntries(
        JsonObject record,
        int nextIndex,
        bool sawFirstUserMessage,
        ExportImageContext? imageContext = null)
    {
        if (GetString(record, "type") == "response_item")
        {
            return BuildResponseItemTranscriptEntries(record, nextIndex, sawFirstUserMessage, imageContext);
        }

        return BuildClaudeMessageTranscriptEntries(record, nextIndex, sawFirstUserMessage, imageContext);
    }

    private static TranscriptBuildResult BuildClaudeMessageTranscriptEntries(
        JsonObject record,
        int nextIndex,
        bool sawFirstUserMessage,
        ExportImageContext? imageContext)
    {
        var message = record["message"] as JsonObject;
        var role = GetString(message, "role");
        var content = message?["content"] as JsonArray;
        if (message is null || role is null || content is null)
        {
            return new TranscriptBuildResult([], sawFirstUserMessage, 0);
        }

        var timestamp = GetString(record, "timestamp");
        var entries = new List<SessionTranscriptEntry>();
        var omitted = 0;

        void Push(SessionTranscriptEntryKind kind, string? entryRole, string title, string text, string language)
        {
            entries.Add(new SessionTranscriptEntry(nextIndex + entries.Count, kind, entryRole, timestamp, title, text, language));
        }

        var renderedContent = BuildMessageContent(content, imageContext);
        if ((!string.IsNullOrWhiteSpace(renderedContent.Text) || renderedContent.HasImages) && role != "developer")
        {
            if (!sawFirstUserMessage && role == "user" && LooksLikeBootstrapContext(renderedContent.Text))
            {
                sawFirstUserMessage = true;
                omitted++;
            }
            else
            {
                if (role == "user")
                {
                    sawFirstUserMessage = true;
                }

                Push(SessionTranscriptEntryKind.Message, role, ToTitleCase(role), renderedContent.Body, renderedContent.Language);
            }
        }

        foreach (var raw in content)
        {
            var item = raw as JsonObject;
            if (item is null)
            {
                continue;
            }

            var type = GetString(item, "type") ?? "";
            if (type == "thinking")
            {
                var thinking = GetString(item, "thinking")?.Trim();
                if (!string.IsNullOrWhiteSpace(thinking))
                {
                    Push(SessionTranscriptEntryKind.Reasoning, null, "Thinking", thinking, "markdown");
                }
            }
            else if (type == "tool_use")
            {
                var name = GetString(item, "name") ?? "unknown-tool";
                Push(SessionTranscriptEntryKind.ToolCall, null, $"Tool Call: {name}", PrettyStructuredText(item["input"]), "json");
            }
            else if (type == "tool_result")
            {
                var id = GetString(item, "tool_use_id");
                Push(SessionTranscriptEntryKind.ToolOutput, null, string.IsNullOrWhiteSpace(id) ? "Tool Output" : $"Tool Output ({id})", PrettyStructuredText(item["content"]), "text");
            }
        }

        return new TranscriptBuildResult(entries, sawFirstUserMessage, omitted);
    }

    private static TranscriptBuildResult BuildResponseItemTranscriptEntries(
        JsonObject record,
        int nextIndex,
        bool sawFirstUserMessage,
        ExportImageContext? imageContext)
    {
        var payload = record["payload"] as JsonObject;
        var type = GetString(payload, "type");
        if (payload is null || string.IsNullOrWhiteSpace(type))
        {
            return new TranscriptBuildResult([], sawFirstUserMessage, 0);
        }

        var timestamp = GetString(record, "timestamp");
        var entry = BuildResponseItemTranscriptEntry(payload, timestamp, nextIndex, sawFirstUserMessage, imageContext, out var updatedSawFirstUserMessage, out var omitted);
        return new TranscriptBuildResult(entry is null ? [] : [entry], updatedSawFirstUserMessage, omitted ? 1 : 0);
    }

    private static SessionTranscriptEntry? BuildResponseItemTranscriptEntry(
        JsonObject item,
        string? timestamp,
        int index,
        bool sawFirstUserMessage,
        ExportImageContext? imageContext,
        out bool updatedSawFirstUserMessage,
        out bool omittedBootstrap)
    {
        updatedSawFirstUserMessage = sawFirstUserMessage;
        omittedBootstrap = false;
        var type = GetString(item, "type");
        switch (type)
        {
            case "message":
            {
                var role = GetString(item, "role") ?? "unknown";
                if (role == "developer")
                {
                    return null;
                }

                var content = item["content"] as JsonArray;
                if (content is null)
                {
                    return null;
                }

                var renderedContent = BuildMessageContent(content, imageContext);
                if (string.IsNullOrWhiteSpace(renderedContent.Text) && !renderedContent.HasImages)
                {
                    return null;
                }

                if (!sawFirstUserMessage && role == "user" && LooksLikeBootstrapContext(renderedContent.Text))
                {
                    updatedSawFirstUserMessage = true;
                    omittedBootstrap = true;
                    return null;
                }

                if (role == "user")
                {
                    updatedSawFirstUserMessage = true;
                }

                var phase = GetString(item, "phase");
                var title = string.IsNullOrWhiteSpace(phase) ? ToTitleCase(role) : $"{ToTitleCase(role)} [{phase}]";
                return new SessionTranscriptEntry(index, SessionTranscriptEntryKind.Message, role, timestamp, title, renderedContent.Body, renderedContent.Language);
            }
            case "function_call":
            {
                var toolName = GetString(item, "name") ?? "unknown-tool";
                return new SessionTranscriptEntry(index, SessionTranscriptEntryKind.ToolCall, null, timestamp, $"Tool Call: {toolName}", PrettyStructuredText(item["arguments"]), "json");
            }
            case "function_call_output":
            {
                var callId = GetString(item, "call_id");
                var title = string.IsNullOrWhiteSpace(callId) ? "Tool Output" : $"Tool Output ({callId})";
                return new SessionTranscriptEntry(index, SessionTranscriptEntryKind.ToolOutput, null, timestamp, title, PrettyStructuredText(item["output"]), "text");
            }
            case "custom_tool_call":
            {
                var toolName = GetString(item, "name") ?? "custom-tool";
                var status = GetString(item, "status");
                var title = string.IsNullOrWhiteSpace(status) ? $"Custom Tool Call: {toolName}" : $"Custom Tool Call: {toolName} [{status}]";
                return new SessionTranscriptEntry(index, SessionTranscriptEntryKind.CustomToolCall, null, timestamp, title, PrettyStructuredText(item["input"]), "text");
            }
            case "custom_tool_call_output":
            {
                var callId = GetString(item, "call_id");
                var title = string.IsNullOrWhiteSpace(callId) ? "Custom Tool Output" : $"Custom Tool Output ({callId})";
                return new SessionTranscriptEntry(index, SessionTranscriptEntryKind.CustomToolOutput, null, timestamp, title, PrettyStructuredText(item["output"]), "text");
            }
            case "reasoning":
            {
                var summaries = ExtractReasoningSummaries(item["summary"]);
                if (summaries.Count == 0)
                {
                    return null;
                }

                return new SessionTranscriptEntry(index, SessionTranscriptEntryKind.Reasoning, null, timestamp, "Reasoning Summary", string.Join("\n", summaries.Select(summary => $"- {summary}")), "markdown");
            }
            default:
                return null;
        }
    }

    private static bool ShouldIncludeTranscriptEntry(SessionTranscriptEntry entry, bool includeToolCallResults)
        => includeToolCallResults || entry.Kind is not (
            SessionTranscriptEntryKind.ToolCall
            or SessionTranscriptEntryKind.ToolOutput
            or SessionTranscriptEntryKind.CustomToolCall
            or SessionTranscriptEntryKind.CustomToolOutput);

    private static string RenderMarkdownTranscriptEntry(SessionTranscriptEntry entry)
    {
        var timestamp = entry.Timestamp ?? "unknown-time";
        if (entry.Kind == SessionTranscriptEntryKind.Message)
        {
            return $"### {timestamp} {entry.Title}\n\n{entry.Text}";
        }

        var language = entry.Language == "json" ? "json" : "text";
        return $"### {timestamp} {entry.Title}\n\n{RenderCodeBlock(entry.Text, language)}";
    }

    private static string RenderHtmlTranscriptEntry(SessionTranscriptEntry entry)
    {
        var roleClass = entry.Kind == SessionTranscriptEntryKind.Message && entry.Role is not null
            ? $"bubble-{HtmlEncoder.Default.Encode(entry.Role)}"
            : entry.Kind == SessionTranscriptEntryKind.Reasoning ? "bubble-reasoning" : "bubble-tool";
        var body = entry.Language == "html" && entry.Kind == SessionTranscriptEntryKind.Message
            ? entry.Text
            : entry.Language == "markdown" && entry.Kind == SessionTranscriptEntryKind.Message
            ? string.Join("", entry.Text.Split("\n\n").Select(paragraph => $"<p>{HtmlEncoder.Default.Encode(paragraph)}</p>"))
            : $"<pre><code>{HtmlEncoder.Default.Encode(entry.Text)}</code></pre>";
        return $"""
        <div class="bubble {roleClass}">
          <div class="bubble-header"><span>{HtmlEncoder.Default.Encode(entry.Title)}</span><span>{HtmlEncoder.Default.Encode(entry.Timestamp ?? "unknown-time")}</span></div>
          <div class="bubble-content">{body}</div>
        </div>
        """;
    }

    private static string BuildHtmlExportPrefix(string sessionFilePath, SessionExportMetadata meta, bool includeImages, bool inlineImages, bool includeToolCallResults)
        => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Claude Session Export</title>
          <style>
          body { margin: 0; font-family: "Segoe UI", sans-serif; color: #1e2430; background: #f7f0e3; }
          main { max-width: 1180px; margin: 0 auto; padding: 32px; }
          header { margin-bottom: 24px; }
          h1 { margin: 0 0 12px; font-size: 36px; }
          .meta { display: grid; gap: 6px; padding: 16px; border: 1px solid rgba(99,82,58,.18); border-radius: 12px; background: rgba(255,255,255,.72); }
          .bubble { margin: 16px 0; border: 1px solid rgba(99,82,58,.18); border-radius: 14px; overflow: hidden; background: white; }
          .bubble-header { display: flex; justify-content: space-between; gap: 16px; padding: 10px 14px; font-weight: 700; background: rgba(214,197,168,.35); }
          .bubble-user .bubble-header { color: #1f5d65; background: rgba(53,123,132,.14); }
          .bubble-assistant .bubble-header { color: #8a3f1d; background: rgba(176,82,47,.12); }
          .bubble-content { padding: 14px; }
          pre { white-space: pre-wrap; word-break: break-word; overflow: auto; }
          code, pre { font-family: "Cascadia Code", Consolas, monospace; }
          </style>
        </head>
        <body>
        <main>
        <header>
          <h1>Claude Session Export</h1>
          <div class="meta">
            <span><strong>Source JSONL:</strong> {{HtmlEncoder.Default.Encode(sessionFilePath)}}</span>
            <span><strong>Session ID:</strong> {{HtmlEncoder.Default.Encode(meta.Id)}}</span>
            <span><strong>Started:</strong> {{HtmlEncoder.Default.Encode(meta.StartedAt ?? "unknown")}}</span>
            <span><strong>CWD:</strong> {{HtmlEncoder.Default.Encode(meta.Cwd)}}</span>
            <span><strong>Originator:</strong> {{HtmlEncoder.Default.Encode(meta.Originator ?? "unknown")}}</span>
            <span><strong>CLI Version:</strong> {{HtmlEncoder.Default.Encode(meta.CliVersion ?? "unknown")}}</span>
            <span><strong>Source:</strong> {{HtmlEncoder.Default.Encode(meta.Source ?? "unknown")}}</span>
            <span><strong>Model Provider:</strong> {{HtmlEncoder.Default.Encode(meta.ModelProvider ?? "unknown")}}</span>
            <span><strong>Included images:</strong> {{(includeImages ? "yes" : "no")}}</span>
            <span><strong>Inline images:</strong> {{(inlineImages ? "yes" : "no")}}</span>
            <span><strong>Included tool calls and results:</strong> {{(includeToolCallResults ? "yes" : "no")}}</span>
            <span><strong>Exported:</strong> {{HtmlEncoder.Default.Encode(DateTimeOffset.Now.ToString("O"))}}</span>
          </div>
        </header>
        """;

    private static string RenderCodeBlock(string text, string language)
    {
        var stabilized = StabilizeExportText(string.IsNullOrEmpty(text) ? "(empty)" : text);
        var longest = 0;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(stabilized, "~+"))
        {
            longest = Math.Max(longest, match.Value.Length);
        }

        var fence = new string('~', Math.Max(3, longest + 1));
        return $"{fence}{language}\n{stabilized}\n{fence}";
    }

    private async Task<SessionExportMetadata> ReadSessionExportMetadataAsync(string path, CancellationToken cancellationToken)
    {
        await foreach (var evt in StreamJsonlRecordsAsync(path, MaxJsonlLineBytesHard, false, cancellationToken))
        {
            var record = evt.Record;
            if (record is null)
            {
                continue;
            }

            var payload = record["payload"] as JsonObject;
            if (GetString(record, "type") == "session_meta" && payload is not null)
            {
                var id = GetString(payload, "id");
                var cwd = GetString(payload, "cwd");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(cwd))
                {
                    return new SessionExportMetadata(
                        id,
                        GetString(payload, "timestamp") ?? GetString(record, "timestamp"),
                        cwd,
                        GetString(payload, "originator"),
                        GetString(payload, "cli_version"),
                        GetString(payload, "source"),
                        GetString(payload, "model_provider"));
                }
            }

            var sessionId = GetString(record, "sessionId");
            var recordCwd = GetString(record, "cwd");
            if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(recordCwd))
            {
                return new SessionExportMetadata(sessionId, GetString(record, "timestamp"), recordCwd, null, null, "claude", null);
            }
        }

        return new SessionExportMetadata(Path.GetFileNameWithoutExtension(path), null, Path.GetDirectoryName(path) ?? "", null, null, "claude", null);
    }

    private static bool IsHumanClaudeUserMessage(JsonObject record)
    {
        if (GetString(record, "type") != "user")
        {
            return false;
        }

        var message = record["message"] as JsonObject;
        if (GetString(message, "role") != "user")
        {
            return false;
        }

        var content = message?["content"] as JsonArray;
        var text = content is null ? "" : ExtractClaudeTextContent(content);
        return !string.IsNullOrWhiteSpace(text) && !LooksLikeBootstrapContext(text);
    }

    private static int CountClaudeToolUses(JsonObject record)
    {
        var content = (record["message"] as JsonObject)?["content"] as JsonArray;
        return content?.Count(item => (item as JsonObject)?["type"]?.GetValue<string>() == "tool_use") ?? 0;
    }

    private static void AddClaudeUsageFromRecord(JsonObject record, Dictionary<string, SessionTokenUsage> target)
    {
        var usage = ExtractClaudeUsage(record);
        if (usage is not null)
        {
            target[usage.Value.Key] = usage.Value.Usage;
        }
    }

    private static (string Key, SessionTokenUsage Usage)? ExtractClaudeUsage(JsonObject record)
    {
        var message = record["message"] as JsonObject;
        var usage = message?["usage"] as JsonObject;
        if (message is null || usage is null)
        {
            return null;
        }

        var input = GetNonNegativeLong(usage, "input_tokens") ?? 0;
        var cacheCreation = GetNonNegativeLong(usage, "cache_creation_input_tokens") ?? 0;
        var cacheRead = GetNonNegativeLong(usage, "cache_read_input_tokens") ?? 0;
        var output = GetNonNegativeLong(usage, "output_tokens") ?? 0;
        if (input == 0 && cacheCreation == 0 && cacheRead == 0 && output == 0)
        {
            return null;
        }

        var key = GetString(message, "id") ?? GetString(record, "requestId") ?? GetString(record, "uuid");
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var totalInput = input + cacheCreation + cacheRead;
        return (key, new SessionTokenUsage(totalInput, cacheCreation + cacheRead, output, 0, totalInput + output));
    }

    private static SessionTokenUsage? SumClaudeTokenUsage(Dictionary<string, SessionTokenUsage> usageByMessage)
    {
        if (usageByMessage.Count == 0)
        {
            return null;
        }

        var total = SessionTokenUsage.Empty;
        foreach (var usage in usageByMessage.Values)
        {
            total = total.Add(usage);
        }

        return total;
    }

    private async Task<HashSet<string>> FindCandidateSessionFilesAsync(
        IReadOnlyList<string> files,
        List<ComparablePathAlias> aliases,
        CancellationToken cancellationToken)
    {
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var probe = await ProbeSessionFileAsync(file, cancellationToken);
                if (probe is not null && MatchesRootAliases(probe.Cwd, aliases))
                {
                    matches.Add(NormalizeFileLookupPath(file));
                    continue;
                }

                if (await SessionTouchesRootAsync(file, aliases, probe?.Cwd, cancellationToken))
                {
                    matches.Add(NormalizeFileLookupPath(file));
                }
            }
            catch
            {
            }
        }

        return matches;
    }

    private async Task<bool> SessionTouchesRootAsync(string file, List<ComparablePathAlias> rootAliases, string? sessionCwd, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sessionCwd) && MatchesRootAliases(sessionCwd, rootAliases))
        {
            return true;
        }

        await using var stream = File.OpenRead(file);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            foreach (var candidate in ExtractPathCandidates(line))
            {
                if (MatchesRootAliases(candidate, rootAliases))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> ExtractPathCandidates(string text)
    {
        var patterns = new[]
        {
            @"(?:\\\\\?\\)?[a-zA-Z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*",
            @"\\\\wsl(?:\.localhost)?\\[^\\/:*?""<>|\r\n]+(?:\\[^\\/:*?""<>|\r\n]+)*",
            @"/mnt/[a-z](?:/[^\s'""<>|`]+)*"
        };
        foreach (var pattern in patterns)
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, pattern))
            {
                yield return match.Value;
            }
        }
    }

    private static string? FindRepoRoot(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool CanReadDirectory(string directory)
    {
        try
        {
            return Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Take(1).Count() >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanWriteDirectoryOrParent(string directory)
    {
        try
        {
            var candidate = directory;
            while (!Directory.Exists(candidate))
            {
                var parent = Path.GetDirectoryName(candidate);
                if (string.IsNullOrWhiteSpace(parent) || parent == candidate)
                {
                    return false;
                }
                candidate = parent;
            }

            var test = Path.Combine(candidate, $".clodlogs-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(test, "");
            File.Delete(test);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCommandAvailable(string file, string argument)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = argument,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(3000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeOptionalPathInput(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? "" : trimmed;
    }

    private static string ResolveOutputDirectory(string sessionFilePath, string? outputDirectory)
    {
        var normalized = NormalizeOptionalPathInput(outputDirectory);
        return string.IsNullOrWhiteSpace(normalized)
            ? Path.GetDirectoryName(sessionFilePath) ?? Environment.CurrentDirectory
            : ResolveFilesystemPath(normalized);
    }

    private static string EnsureHtmlOutputPath(string path)
        => string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(path), ".htm", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{path}.html";

    private static string DecodeClaudeProjectFolderPath(string folderName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(folderName, "^([a-z])--(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success
            ? $"{match.Groups[1].Value.ToLowerInvariant()}:\\{match.Groups[2].Value.Replace("-", "\\")}"
            : "";
    }

    private static int CompareByNewestDesc(SessionMetaMatch left, SessionMetaMatch right)
    {
        var leftTime = ParseDate(left.UpdatedAt ?? left.StartedAt);
        var rightTime = ParseDate(right.UpdatedAt ?? right.StartedAt);
        if (leftTime is not null || rightTime is not null)
        {
            return Nullable.Compare(rightTime, leftTime);
        }

        return string.Compare(right.File, left.File, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static RenderedMessageContent BuildMessageContent(JsonArray content, ExportImageContext? imageContext)
    {
        var textChunks = new List<string>();
        var renderedImages = new List<string>();
        foreach (var item in content)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
            {
                textChunks.Add(text);
                continue;
            }

            var part = item as JsonObject;
            if (part is null)
            {
                continue;
            }

            if (imageContext is not null && imageContext.IncludeImages && TryRenderImagePart(part, imageContext) is { } renderedImage)
            {
                renderedImages.Add(renderedImage);
                continue;
            }

            if (LooksLikeImageContentPart(part))
            {
                continue;
            }

            if (GetMessageContentText(part) is { } partText)
            {
                textChunks.Add(partText);
                continue;
            }

            textChunks.Add(PrettyStructuredText(part));
        }

        var messageText = StripImagePlaceholderTags(string.Join("\n\n", textChunks.Select(chunk => chunk.Trim()).Where(chunk => chunk.Length > 0)));
        if (imageContext is null)
        {
            return new RenderedMessageContent(messageText, messageText, "markdown", false);
        }

        if (imageContext.Mode == ExportImageRenderMode.Html)
        {
            var paragraphs = string.IsNullOrWhiteSpace(messageText)
                ? ""
                : string.Join("", messageText.Split("\n\n").Select(paragraph => $"<p>{HtmlEncoder.Default.Encode(paragraph)}</p>"));
            return new RenderedMessageContent(messageText, paragraphs + string.Join("", renderedImages), "html", renderedImages.Count > 0);
        }

        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(messageText))
        {
            sections.Add(messageText);
        }
        if (renderedImages.Count > 0)
        {
            sections.Add(string.Join("\n\n", renderedImages));
        }

        return new RenderedMessageContent(messageText, string.Join("\n\n", sections), "markdown", renderedImages.Count > 0);
    }

    private static string ExtractClaudeTextContent(JsonArray content)
    {
        var chunks = new List<string>();
        foreach (var item in content)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
            {
                chunks.Add(text);
                continue;
            }

            var part = item as JsonObject;
            if (part is null || LooksLikeImageContentPart(part))
            {
                continue;
            }

            if (GetString(part, "type") == "text" && GetString(part, "text") is { } partText)
            {
                chunks.Add(partText);
            }
        }

        return StripImagePlaceholderTags(string.Join("\n\n", chunks.Select(c => c.Trim()).Where(c => c.Length > 0)));
    }

    private static string? GetMessageContentText(JsonObject part)
        => GetString(part, "text") ?? GetString(part, "input_text") ?? GetString(part, "output_text");

    private static IReadOnlyList<string> ExtractReasoningSummaries(JsonNode? summary)
    {
        if (summary is not JsonArray array)
        {
            return [];
        }

        var summaries = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
            {
                var trimmed = text.Trim();
                if (trimmed.Length > 0)
                {
                    summaries.Add(trimmed);
                }
                continue;
            }

            if (item is JsonObject obj && GetString(obj, "text") is { } entryText)
            {
                var trimmed = entryText.Trim();
                if (trimmed.Length > 0)
                {
                    summaries.Add(trimmed);
                }
            }
        }

        return summaries;
    }

    private static string? TryRenderImagePart(JsonObject part, ExportImageContext context)
    {
        if (!LooksLikeImageContentPart(part))
        {
            return null;
        }

        var source = (GetString(part, "image_url") ?? GetString(part, "url"))?.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var reference = context.PersistImageReference(source);
        if (reference is null)
        {
            return null;
        }

        var alt = GetString(part, "alt_text")?.Trim();
        if (string.IsNullOrWhiteSpace(alt))
        {
            alt = $"Image {Math.Max(1, context.NextImageIndex - 1)}";
        }

        return context.Mode == ExportImageRenderMode.Html
            ? $"<img src=\"{HtmlEncoder.Default.Encode(reference)}\" alt=\"{HtmlEncoder.Default.Encode(alt)}\">"
            : $"![{EscapeMarkdownText(alt)}]({reference})";
    }

    private static string EscapeMarkdownText(string text)
        => text.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");

    private static string? ExtensionForMimeType(string mimeType)
        => mimeType.ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/jpeg" or "image/jpg" => "jpg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/svg+xml" => "svg",
            _ => null
        };

    private static bool LooksLikeImageContentPart(JsonObject part)
    {
        var type = GetString(part, "type")?.ToLowerInvariant() ?? "";
        return type.Contains("image", StringComparison.Ordinal) || GetString(part, "image_url") is not null || GetString(part, "url") is not null;
    }

    private static string StripImagePlaceholderTags(string text)
        => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(text, @"^\s*</?image>\s*$", "", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            @"\n{3,}",
            "\n\n").Trim();

    private static bool LooksLikeBootstrapContext(string text)
        => text.Contains("# AGENTS.md instructions", StringComparison.Ordinal)
            || text.Contains("<environment_context>", StringComparison.Ordinal)
            || text.Contains("<permissions instructions>", StringComparison.Ordinal)
            || text.Contains("<collaboration_mode>", StringComparison.Ordinal);

    private static string PrettyStructuredText(JsonNode? value)
    {
        if (value is null)
        {
            return "";
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return text;
            }

            try
            {
                return JsonNode.Parse(trimmed)?.ToJsonString(IndentedJsonOptions) ?? text;
            }
            catch
            {
                return text;
            }
        }

        return value.ToJsonString(IndentedJsonOptions);
    }

    private static string StabilizeExportText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\t' or '\n' or '\r')
            {
                builder.Append(ch);
            }
            else if (char.IsControl(ch))
            {
                builder.Append($"\\u{(int)ch:x4}");
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string ToTitleCase(string value)
        => value.Length == 0 ? value : $"{char.ToUpperInvariant(value[0])}{value[1..]}";

    private static string? GetString(JsonObject? obj, string property)
    {
        if (obj is null || !obj.TryGetPropertyValue(property, out var node))
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
    }

    private static long? GetNonNegativeLong(JsonObject obj, string property)
    {
        if (!obj.TryGetPropertyValue(property, out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var result) && result >= 0)
        {
            return result;
        }

        return null;
    }

    private static string? NormalizeSessionTitle(string? value)
    {
        var sanitized = SanitizeSessionTitleInput(value);
        return sanitized.Length == 0 ? null : sanitized;
    }

    private static string BuildSanitizedSessionMetaRecord(string source, string? claudeHome, string? chatName, bool stripImageContent, bool stripBlobContent, bool createJsonlCopy, bool reAddToCurrentDay)
    {
        var now = DateTimeOffset.Now;
        var id = GenerateUuidV7String(now);
        var record = new JsonObject
        {
            ["type"] = "session_meta",
            ["timestamp"] = now.ToString("O"),
            ["payload"] = new JsonObject
            {
                ["id"] = id,
                ["timestamp"] = now.ToString("O"),
                ["cwd"] = Path.GetDirectoryName(source) ?? Environment.CurrentDirectory,
                ["originator"] = "clodlogs",
                ["source"] = "clodlogs-sanitized-copy",
                ["chat_name"] = NormalizeSessionTitle(chatName),
                ["options"] = new JsonObject
                {
                    ["claudeHome"] = claudeHome,
                    ["stripImageContent"] = stripImageContent,
                    ["stripBlobContent"] = stripBlobContent,
                    ["createJsonlCopy"] = createJsonlCopy,
                    ["reAddToCurrentDay"] = reAddToCurrentDay
                }
            }
        };
        return record.ToJsonString(CompactJsonOptions);
    }

    private static string GenerateUuidV7String(DateTimeOffset date)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var timestamp = date.ToUnixTimeMilliseconds();
        bytes[0] = (byte)((timestamp >> 40) & 0xff);
        bytes[1] = (byte)((timestamp >> 32) & 0xff);
        bytes[2] = (byte)((timestamp >> 24) & 0xff);
        bytes[3] = (byte)((timestamp >> 16) & 0xff);
        bytes[4] = (byte)((timestamp >> 8) & 0xff);
        bytes[5] = (byte)(timestamp & 0xff);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant().Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-");
    }

    private static string? SanitizeJsonlLine(string line, bool keepImagePlaceholders, bool stripBlobContent, SanitizedSessionStats stats)
    {
        JsonObject? record;
        try
        {
            record = JsonNode.Parse(line) as JsonObject;
        }
        catch
        {
            return null;
        }

        if (record is null)
        {
            return null;
        }

        var type = GetString(record, "type");
        if (type == "event_msg")
        {
            return SanitizeEventMsg(record, keepImagePlaceholders, stripBlobContent, stats);
        }
        if (type == "response_item")
        {
            return SanitizeResponseItem(record, keepImagePlaceholders, stripBlobContent, stats);
        }
        if (type == "turn_context" && stripBlobContent)
        {
            record["payload"] = SanitizeLargeStrings(record["payload"], "turn context blob");
            return record.ToJsonString(CompactJsonOptions);
        }
        if (type == "compacted")
        {
            var payload = record["payload"] as JsonObject;
            if (payload?["replacement_history"] is JsonArray replacement)
            {
                var sanitized = new JsonArray();
                foreach (var item in replacement)
                {
                    sanitized.Add(CloneJson(SanitizeResponseItemPayload(item, keepImagePlaceholders, false, stats)));
                }
                payload["replacement_history"] = sanitized;
                return record.ToJsonString(CompactJsonOptions);
            }
        }

        return stripBlobContent ? record.ToJsonString(CompactJsonOptions) : null;
    }

    private static string? SanitizeEventMsg(JsonObject record, bool keepImagePlaceholders, bool stripBlobContent, SanitizedSessionStats stats)
    {
        var payload = record["payload"] as JsonObject;
        if (payload is null)
        {
            return null;
        }

        if (GetString(payload, "type") == "token_count" && stripBlobContent)
        {
            record["payload"] = new JsonObject { ["type"] = "token_count" };
            return record.ToJsonString(CompactJsonOptions);
        }

        if (GetString(payload, "type") != "user_message")
        {
            return null;
        }

        var imageCount = payload["images"] is JsonArray images ? images.Count : 0;
        var localImageCount = payload["local_images"] is JsonArray localImages ? localImages.Count : 0;
        if (imageCount == 0 && localImageCount == 0)
        {
            return null;
        }

        stats.DroppedInputImageCount += imageCount;
        stats.DroppedLocalImageCount += localImageCount;
        var placeholders = keepImagePlaceholders
            ? string.Join("\n", Enumerable.Repeat("<image removed>", imageCount).Concat(Enumerable.Repeat("<local image removed>", localImageCount)))
            : "";
        var message = GetString(payload, "message") ?? "";
        payload["message"] = string.IsNullOrWhiteSpace(placeholders) ? message : string.IsNullOrWhiteSpace(message) ? placeholders : $"{message}\n{placeholders}";
        payload["images"] = new JsonArray();
        payload["local_images"] = new JsonArray();
        return record.ToJsonString(CompactJsonOptions);
    }

    private static string? SanitizeResponseItem(JsonObject record, bool keepImagePlaceholders, bool stripBlobContent, SanitizedSessionStats stats)
    {
        var payload = record["payload"];
        var sanitized = SanitizeResponseItemPayload(payload, keepImagePlaceholders, stripBlobContent, stats);
        if (sanitized is null)
        {
            return null;
        }

        record["payload"] = CloneJson(sanitized);
        return record.ToJsonString(CompactJsonOptions);
    }

    private static JsonNode? SanitizeResponseItemPayload(JsonNode? node, bool keepImagePlaceholders, bool stripBlobContent, SanitizedSessionStats stats)
    {
        if (node is not JsonObject payload)
        {
            return node;
        }

        var changed = false;
        if (GetString(payload, "type") == "message" && payload["content"] is JsonArray content)
        {
            payload["content"] = SanitizeContentArray(content, keepImagePlaceholders, stats);
            changed = true;
        }

        if ((GetString(payload, "type") == "function_call_output" || GetString(payload, "type") == "custom_tool_call_output") && payload["output"] is JsonArray output)
        {
            payload["output"] = SanitizeContentArray(output, keepImagePlaceholders, stats);
            changed = true;
        }

        if (stripBlobContent)
        {
            var type = GetString(payload, "type");
            if (type == "function_call" && ShouldStripBlobString(GetString(payload, "arguments")))
            {
                payload["arguments"] = FormatRemovedBlobPlaceholder("function call arguments", GetString(payload, "arguments")!);
                changed = true;
            }
            else if (type == "custom_tool_call" && ShouldStripBlobString(GetString(payload, "input")))
            {
                payload["input"] = FormatRemovedBlobPlaceholder("tool input", GetString(payload, "input")!);
                changed = true;
            }
            else if (type == "reasoning" && ShouldStripBlobString(GetString(payload, "encrypted_content")))
            {
                payload["encrypted_content"] = FormatRemovedBlobPlaceholder("reasoning blob", GetString(payload, "encrypted_content")!);
                changed = true;
            }
            else if ((type == "function_call_output" || type == "custom_tool_call_output") && ShouldStripBlobString(GetString(payload, "output")))
            {
                payload["output"] = FormatRemovedBlobPlaceholder("tool output", GetString(payload, "output")!);
                changed = true;
            }
        }

        return changed ? payload : node;
    }

    private static JsonArray SanitizeContentArray(JsonArray content, bool keepImagePlaceholders, SanitizedSessionStats stats)
    {
        var result = new JsonArray();
        foreach (var item in content)
        {
            var obj = item as JsonObject;
            if (obj is not null && GetString(obj, "type") == "input_image")
            {
                stats.DroppedInputImageCount++;
                if (keepImagePlaceholders)
                {
                    result.Add(new JsonObject { ["type"] = "input_text", ["text"] = "<image removed>" });
                }
                continue;
            }

            result.Add(CloneJson(item));
        }

        return MergeAdjacentRawTextItems(result);
    }

    private static JsonArray MergeAdjacentRawTextItems(JsonArray items)
    {
        var merged = new JsonArray();
        foreach (var item in items)
        {
            var current = item as JsonObject;
            var previous = merged.Count > 0 ? merged[^1] as JsonObject : null;
            var currentType = GetString(current, "type");
            if ((currentType == "input_text" || currentType == "output_text") && GetString(previous, "type") == currentType)
            {
                previous!["text"] = $"{GetString(previous, "text")}\n{GetString(current, "text")}";
            }
            else
            {
                merged.Add(CloneJson(item));
            }
        }

        return merged;
    }

    private static JsonNode? SanitizeLargeStrings(JsonNode? node, string label)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return ShouldStripBlobString(text) ? FormatRemovedBlobPlaceholder(label, text) : text;
        }
        if (node is JsonArray array)
        {
            var output = new JsonArray();
            foreach (var item in array)
            {
                output.Add(SanitizeLargeStrings(item, label));
            }
            return output;
        }
        if (node is JsonObject obj)
        {
            var output = new JsonObject();
            foreach (var property in obj)
            {
                output[property.Key] = SanitizeLargeStrings(property.Value, label);
            }
            return output;
        }
        return CloneJson(node);
    }

    private static bool ShouldStripBlobString(string? text)
        => text is not null && Encoding.UTF8.GetByteCount(text) > BlobTextThresholdBytes;

    private static string FormatRemovedBlobPlaceholder(string label, string text)
        => $"<{label} removed: {Encoding.UTF8.GetByteCount(text)} bytes>";

    private static JsonNode? CloneJson(JsonNode? node)
        => node?.DeepClone();

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string SanitizeFileStem(string value)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(value, @"[^a-zA-Z0-9._-]+", "-");
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, "-+", "-").Trim('-');
        return sanitized.Length == 0 ? "session" : sanitized[..Math.Min(80, sanitized.Length)];
    }

    private static string NormalizeFileLookupPath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();

    private static bool MatchesRootAliases(string candidatePath, List<ComparablePathAlias> rootAliases)
    {
        var candidateAliases = GetComparablePathAliases(candidatePath);
        foreach (var candidate in candidateAliases)
        {
            foreach (var root in rootAliases)
            {
                if (candidate.Style == root.Style && IsSamePathOrChildForStyle(candidate.CompareValue, root.CompareValue, candidate.Style))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSamePathOrChildForStyle(string candidate, string root, string style)
    {
        var comparison = style == "win" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(candidate, root, comparison))
        {
            return true;
        }

        var separator = style == "win" ? "\\" : "/";
        return candidate.StartsWith(root.TrimEnd('\\', '/') + separator, comparison);
    }

    private static List<ComparablePathAlias> GetComparablePathAliases(string input)
    {
        var aliases = new Dictionary<string, ComparablePathAlias>(StringComparer.OrdinalIgnoreCase);
        void Add(string style, string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var normalized = style == "win" ? NormalizeWindowsPath(candidate) : NormalizePosixPath(candidate);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            var compare = style == "win" ? normalized.ToLowerInvariant() : normalized;
            aliases[$"{style}:{compare}"] = new ComparablePathAlias(style, normalized, compare);
        }

        var windows = NormalizeWindowsPath(input);
        if (windows is not null)
        {
            Add("win", windows);
            Add("posix", WindowsDrivePathToWslMount(windows));
        }

        var wslMount = NormalizeWslMountPath(input);
        if (wslMount is not null)
        {
            Add("posix", wslMount);
            Add("win", WslMountPathToWindowsDrive(wslMount));
        }

        var unc = ParseWslUncPath(input);
        if (unc is not null)
        {
            Add("win", unc.Value.WinPath);
            Add("posix", unc.Value.PosixPath);
        }

        var posix = NormalizeAbsolutePosixPath(input);
        if (posix is not null)
        {
            Add("posix", posix);
        }

        if (aliases.Count == 0)
        {
            Add(OperatingSystem.IsWindows() ? "win" : "posix", Path.GetFullPath(input));
        }

        return aliases.Values.ToList();
    }

    private static string? NormalizeWindowsPath(string input)
    {
        var slash = input.Replace('/', '\\');
        if (!System.Text.RegularExpressions.Regex.IsMatch(slash, @"^(?:[a-zA-Z]:\\|\\\\|[a-zA-Z]:$)"))
        {
            return null;
        }

        return Path.GetFullPath(slash).TrimEnd('\\');
    }

    private static string? NormalizePosixPath(string input)
    {
        if (!input.StartsWith('/'))
        {
            return null;
        }

        return input.Replace('\\', '/').TrimEnd('/');
    }

    private static string? NormalizeAbsolutePosixPath(string input) => NormalizePosixPath(input);

    private static string? NormalizeWslMountPath(string input)
    {
        var normalized = NormalizeAbsolutePosixPath(input);
        return normalized is not null && System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^/mnt/[a-z](?:/|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? normalized
            : null;
    }

    private static string? WindowsDrivePathToWslMount(string? input)
    {
        if (input is null)
        {
            return null;
        }
        var match = System.Text.RegularExpressions.Regex.Match(input, @"^([a-z]):\\?(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }
        var drive = match.Groups[1].Value.ToLowerInvariant();
        var rest = match.Groups[2].Value.Replace('\\', '/');
        return rest.Length == 0 ? $"/mnt/{drive}" : $"/mnt/{drive}/{rest}";
    }

    private static string? WslMountPathToWindowsDrive(string? input)
    {
        if (input is null)
        {
            return null;
        }
        var match = System.Text.RegularExpressions.Regex.Match(input, @"^/mnt/([a-z])(?:/(.*))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }
        var rest = match.Groups[2].Value.Replace('/', '\\');
        return rest.Length == 0 ? $"{match.Groups[1].Value.ToLowerInvariant()}:\\" : $"{match.Groups[1].Value.ToLowerInvariant()}:\\{rest}";
    }

    private static (string WinPath, string PosixPath)? ParseWslUncPath(string input)
    {
        var slash = input.Replace('/', '\\');
        if (!slash.StartsWith(@"\\wsl\", StringComparison.OrdinalIgnoreCase) && !slash.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = slash.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        var rest = string.Join('\\', parts.Skip(2));
        return (slash, "/" + rest.Replace('\\', '/'));
    }

    public sealed record JobStartResult(string JobId);
    private enum ExportImageRenderMode
    {
        Markdown,
        Html
    }
    private sealed record RenderedMessageContent(string Text, string Body, string Language, bool HasImages);
    private sealed class ExportImageContext(
        ExportImageRenderMode mode,
        bool includeImages,
        bool inlineImages,
        string outputDirectory,
        string assetDirectoryName)
    {
        public ExportImageRenderMode Mode { get; } = mode;
        public bool IncludeImages { get; } = includeImages;
        public bool InlineImages { get; } = inlineImages;
        public int NextImageIndex { get; private set; } = 1;
        private string OutputDirectory { get; } = outputDirectory;
        private string AssetDirectoryName { get; } = assetDirectoryName;
        private string AssetDirectoryPath => Path.Combine(OutputDirectory, AssetDirectoryName);

        public string? PersistImageReference(string source)
        {
            if (!source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }

            if (Mode == ExportImageRenderMode.Html && InlineImages)
            {
                return source;
            }

            var comma = source.IndexOf(',');
            if (comma < 0)
            {
                return null;
            }

            var metadata = source[..comma];
            var match = System.Text.RegularExpressions.Regex.Match(metadata, "^data:([^;,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var extension = ExtensionForMimeType(match.Success ? match.Groups[1].Value : "image/png");
            if (extension is null)
            {
                return null;
            }

            try
            {
                var bytes = Convert.FromBase64String(source[(comma + 1)..]);
                Directory.CreateDirectory(AssetDirectoryPath);
                var fileName = $"image-{NextImageIndex:000}.{extension}";
                NextImageIndex++;
                File.WriteAllBytes(Path.Combine(AssetDirectoryPath, fileName), bytes);
                return $"./{AssetDirectoryName}/{fileName}";
            }
            catch
            {
                return null;
            }
        }

        public void CleanupAssetDirectory()
        {
            try
            {
                if (Directory.Exists(AssetDirectoryPath))
                {
                    Directory.Delete(AssetDirectoryPath, true);
                }
            }
            catch
            {
            }
        }
    }
    private sealed record SessionProbe(string Id, long FileSizeBytes, string Cwd, string? StartedAt, string? UpdatedAt, string? ThreadName, string? Source);
    private sealed record SessionExportMetadata(string Id, string? StartedAt, string Cwd, string? Originator, string? CliVersion, string? Source, string? ModelProvider);
    private sealed record ComparablePathAlias(string Style, string Value, string CompareValue);
    private sealed record TranscriptBuildResult(IReadOnlyList<SessionTranscriptEntry> Entries, bool SawFirstUserMessage, int OmittedBootstrapMessages);
    private sealed record JsonlRecordEvent(int LineNumber, long ByteLength, long BytesProcessed, JsonObject? Record, bool Oversized)
    {
        public static JsonlRecordEvent AsRecord(int lineNumber, long byteLength, long bytesProcessed, JsonObject record)
            => new(lineNumber, byteLength, bytesProcessed, record, false);
        public static JsonlRecordEvent AsOversized(int lineNumber, long byteLength, long bytesProcessed)
            => new(lineNumber, byteLength, bytesProcessed, null, true);
    }
    private sealed record JobRecord<T>(CancellationTokenSource Cancellation, T Status)
    {
        public T Status { get; set; } = Status;
    }
    private sealed record DateRange(bool Active, int? From, int? To)
    {
        public static DateRange Create(string? from, string? to) => new(ParseDateKey(from) is not null || ParseDateKey(to) is not null, ParseDateKey(from), ParseDateKey(to));
        public bool Matches(string? startedAt, string? updatedAt)
        {
            if (!Active)
            {
                return true;
            }
            var key = ParseTimestampDateKey(updatedAt ?? startedAt);
            return key is not null && (From is null || key >= From) && (To is null || key <= To);
        }
        private static int? ParseTimestampDateKey(string? value)
            => value is not null && value.Length >= 10 ? ParseDateKey(value[..10]) : null;
        private static int? ParseDateKey(string? value)
        {
            if (DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", out var date))
            {
                return date.Year * 10000 + date.Month * 100 + date.Day;
            }
            return null;
        }
    }
    private sealed class SanitizedSessionStats
    {
        public int DroppedInputImageCount { get; set; }
        public int DroppedLocalImageCount { get; set; }
        public int PreservedCommandExecutionCount { get; set; }
        public int PreservedReasoningCount { get; set; }
        public int ReconstructedMessageCount { get; set; }
        public JsonObject ToJson() => new()
        {
            ["droppedInputImageCount"] = DroppedInputImageCount,
            ["droppedLocalImageCount"] = DroppedLocalImageCount,
            ["preservedCommandExecutionCount"] = PreservedCommandExecutionCount,
            ["preservedReasoningCount"] = PreservedReasoningCount,
            ["reconstructedMessageCount"] = ReconstructedMessageCount,
            ["omittedThreadItemCounts"] = new JsonObject()
        };
    }
}
