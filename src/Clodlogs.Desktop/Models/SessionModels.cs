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
    IReadOnlyList<SessionMetaMatch> Sessions,
    int ScannedFileCount,
    int TotalFileCount,
    bool IsComplete);

public sealed record SessionScanProgress(
    int ScannedFileCount,
    int TotalFileCount,
    SessionMetaMatch? Match);

public sealed record SessionTokenUsage(
    long InputTokens,
    long CacheCreation5MinuteInputTokens,
    long CacheCreation1HourInputTokens,
    long CacheReadInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens)
{
    public static SessionTokenUsage Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public long CacheCreationInputTokens => CacheCreation5MinuteInputTokens + CacheCreation1HourInputTokens;
    public long CachedInputTokens => CacheCreationInputTokens + CacheReadInputTokens;

    public SessionTokenUsage Add(SessionTokenUsage other)
        => new(
            InputTokens + other.InputTokens,
            CacheCreation5MinuteInputTokens + other.CacheCreation5MinuteInputTokens,
            CacheCreation1HourInputTokens + other.CacheCreation1HourInputTokens,
            CacheReadInputTokens + other.CacheReadInputTokens,
            OutputTokens + other.OutputTokens,
            ReasoningOutputTokens + other.ReasoningOutputTokens,
            TotalTokens + other.TotalTokens);
}

public sealed record SessionTokenUsageRecord(
    string Key,
    string? Timestamp,
    string Model,
    SessionTokenUsage Usage);

public sealed record AnthropicModelPrice(
    string Model,
    decimal InputPerMillionTokens,
    decimal CacheWrite5MinutePerMillionTokens,
    decimal CacheWrite1HourPerMillionTokens,
    decimal CacheReadPerMillionTokens,
    decimal OutputPerMillionTokens);

public sealed record AnthropicPricing(
    string SourceUrl,
    string? RefreshedAt,
    IReadOnlyList<AnthropicModelPrice> Models);

public sealed record TokenUsageCostBreakdown(
    decimal InputCost,
    decimal CacheWriteCost,
    decimal CacheReadCost,
    decimal OutputCost,
    decimal TotalCost,
    int PricedRows,
    int UnpricedRows,
    string PricingSourceUrl,
    string? PricingRefreshedAt);

public sealed record TokenUsageDailyBreakdown(
    DateOnly Date,
    SessionTokenUsage TokenUsage,
    decimal Cost);

public sealed record TokenUsageModelBreakdown(
    string Model,
    SessionTokenUsage TokenUsage,
    decimal Cost);

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
    SessionTokenUsage TokenUsage,
    TokenUsageCostBreakdown? CostBreakdown,
    IReadOnlyList<TokenUsageDailyBreakdown> DailyBreakdown,
    IReadOnlyList<TokenUsageModelBreakdown> ModelBreakdown);

public sealed record ExportJobStatus(
    string Kind,
    int ProgressPercent,
    string Stage,
    string Message,
    string? OutputPath);

public sealed record BatchExportSessionRequest(
    string SessionFilePath,
    string SessionName,
    string? StartedAt);

public sealed record BatchExportFailure(
    string SessionFilePath,
    string Message);

public sealed record BatchExportResult(
    IReadOnlyList<BatchExportFailure> Failures);

public sealed record BatchExportJobStatus(
    string Kind,
    int ProgressPercent,
    string Stage,
    string Message,
    string? OutputDirectory,
    BatchExportResult? Result);

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
