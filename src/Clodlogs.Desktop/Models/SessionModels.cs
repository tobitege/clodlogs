namespace Clodlogs.Desktop.Models;

public enum SessionKind
{
    Live,
    Archived
}

public enum ScopeMode
{
    Repo,
    Cwd,
    All
}

public sealed record SessionMetaMatch(
    SessionKind Kind,
    string Id,
    string File,
    long FileSizeBytes,
    string Cwd,
    string? StartedAt,
    string? UpdatedAt,
    string? ThreadName,
    string? Source);

public sealed record FindClaudeSessionsResult(
    string ClaudeHome,
    string CurrentWorkingDirectory,
    string? RequestedDirectory,
    ScopeMode ScopeMode,
    string? TargetRoot,
    int SessionCount,
    int LiveCount,
    int ArchivedCount,
    IReadOnlyList<SessionMetaMatch> Sessions);

public sealed record SessionTokenUsage(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens)
{
    public static SessionTokenUsage Empty { get; } = new(0, 0, 0, 0, 0);

    public SessionTokenUsage Add(SessionTokenUsage other)
        => new(
            InputTokens + other.InputTokens,
            CachedInputTokens + other.CachedInputTokens,
            OutputTokens + other.OutputTokens,
            ReasoningOutputTokens + other.ReasoningOutputTokens,
            TotalTokens + other.TotalTokens);
}

public sealed record SessionDetailMetrics(
    int InteractionCount,
    int ToolCallCount,
    SessionTokenUsage? TokenUsage,
    long FileSizeBytes,
    string AnalysisKind,
    string? SkipReason,
    long? LargestParsedLineBytes,
    int OversizedLineCount);

public enum SessionTranscriptEntryKind
{
    Message,
    Reasoning,
    ToolCall,
    ToolOutput,
    CustomToolCall,
    CustomToolOutput
}

public sealed record SessionTranscriptEntry(
    int Index,
    SessionTranscriptEntryKind Kind,
    string? Role,
    string? Timestamp,
    string Title,
    string Text,
    string Language);

public sealed record SessionTranscriptResult(
    string? SessionId,
    string? Cwd,
    string? StartedAt,
    long FileSizeBytes,
    IReadOnlyList<SessionTranscriptEntry> Entries,
    bool Truncated,
    int OmittedBootstrapMessages,
    int OversizedLineCount);

public sealed record EnvironmentCapabilities(
    string ClaudeHome,
    bool ClaudeHomeReadable,
    bool ClaudeHomeWritable,
    bool GitAvailable,
    bool RipgrepAvailable,
    string OverallKind,
    string Summary,
    IReadOnlyList<string> Notes);

public sealed record TokenUsageSummaryResult(
    int SessionCount,
    int ScannedSessionCount,
    int SessionsWithTokenUsage,
    int SessionsWithoutTokenUsage,
    int FailedSessionCount,
    long FileSizeBytes,
    int OversizedLineCount,
    int TokenCountRows,
    SessionTokenUsage TokenUsage);

public sealed record ExportJobStatus(
    string Kind,
    int ProgressPercent,
    string Stage,
    string Message,
    string? OutputPath);

public sealed record TokenUsageSummaryJobStatus(
    string Kind,
    int ProgressPercent,
    string Stage,
    string Message,
    int ScannedSessionCount,
    int TotalSessionCount,
    string? CurrentSessionPath,
    TokenUsageSummaryResult? Result);

public sealed record ExportProgress(string Stage, string Message, int ProgressPercent);
