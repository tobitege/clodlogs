using Clodlogs.Desktop.Models;
using Clodlogs.Desktop.Services;

namespace Clodlogs.Desktop.ViewModels;

public sealed class SessionCardViewModel(SessionMetaMatch session) : ViewModelBase
{
    public SessionMetaMatch Session { get; } = session;
    public string KindLabel => Session.Kind == SessionKind.Live ? "LIVE" : "ARCHIVED";
    public string Title => string.IsNullOrWhiteSpace(Session.ThreadName)
        ? FormatDisplayFileName(Path.GetFileName(Session.File)).Replace(".jsonl", "", StringComparison.OrdinalIgnoreCase)
        : Session.ThreadName!;
    public string Cwd => Session.Cwd;
    public string File => Session.File;
    public string Id => Session.Id;
    public long FileSizeBytes => Session.FileSizeBytes;
    public string FileSizeLabel => ClaudeSessionService.FormatByteCount(Session.FileSizeBytes);
    public bool IsLarge => Session.FileSizeBytes >= ClaudeSessionService.LargeSessionWarningBytes;
    public string UpdatedAtLabel => FormatTimestamp(Session.UpdatedAt);
    public string StartedAtLabel => FormatTimestamp(Session.StartedAt);
    public string SourceLabel => Session.Source ?? "unknown";

    private static string FormatTimestamp(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToLocalTime().ToString("g") : value ?? "Unknown";

    private static string FormatDisplayFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var stem = extension.Length == 0 ? fileName : fileName[..^extension.Length];
        return $"{TruncateGuidText(stem)}{extension}";
    }

    private static string TruncateGuidText(string value)
        => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(
                value,
                @"\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b",
                match => $"{match.Value[..10]}..."),
            @"\b([0-9a-fA-F]{10})[0-9a-fA-F]{8,}\b",
            "$1...");
}
