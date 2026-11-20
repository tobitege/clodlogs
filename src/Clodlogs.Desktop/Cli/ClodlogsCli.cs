using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Clodlogs.Desktop.Models;
using Clodlogs.Desktop.Services;

namespace Clodlogs.Desktop.Cli;

/// <summary>
/// The command line of clodlogs: <c>list</c> shows sessions and <c>export</c> writes them to a folder,
/// both with the same filters for sessions, project, time window and title.
/// </summary>
public sealed partial class ClodlogsCli(
    ClaudeSessionService service,
    TextWriter output,
    TextWriter error,
    string currentDirectory,
    Func<DateTimeOffset> now,
    TimeZoneInfo timeZone)
{
    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;

    private static readonly string[] Commands = ["list", "export", "help", "--help", "-h", "/?", "--version"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    /// <summary>True when the arguments ask for the command line instead of the desktop window.</summary>
    public static bool IsCommand(string[] args)
        => args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        if (command is "help" or "--help" or "-h" or "/?")
        {
            output.WriteLine(HelpText);
            return ExitSuccess;
        }

        if (command == "--version")
        {
            output.WriteLine(GetVersion());
            return ExitSuccess;
        }

        CliOptions options;
        try
        {
            options = CliOptions.Parse(command, args[1..], currentDirectory, now(), timeZone);
        }
        catch (CliUsageException ex)
        {
            error.WriteLine($"clodlogs: {ex.Message}");
            error.WriteLine("Run 'clodlogs help' for the options.");
            return ExitUsage;
        }

        if (options.Help)
        {
            output.WriteLine(HelpText);
            return ExitSuccess;
        }

        try
        {
            var sessions = await SelectSessionsAsync(options, cancellationToken);
            if (sessions is null)
            {
                return ExitFailure;
            }

            return command == "list"
                ? WriteList(options, sessions)
                : await ExportAsync(options, sessions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("clodlogs: cancelled.");
            return ExitFailure;
        }
    }

    private async Task<List<SessionMetaMatch>?> SelectSessionsAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var scan = await service.FindClaudeSessionsAsync(
            options.ClaudeHome,
            options.ProjectDirectory,
            options.CwdOnly,
            null,
            null,
            options.CrossSessionWrites,
            currentDirectory,
            cancellationToken: cancellationToken,
            // Named sessions are looked up among all files, so a session outside the window is not reported as unknown.
            modifiedSince: options.Sessions.Count == 0 ? options.Since : null);
        if (!scan.IsComplete)
        {
            error.WriteLine($"clodlogs: warning: the scan read only {scan.ScannedFileCount} of {scan.TotalFileCount} session files.");
        }

        IEnumerable<SessionMetaMatch> candidates = scan.Sessions;
        if (!options.IncludeSubagents)
        {
            candidates = candidates.Where(session => !IsSubagentFile(session.File));
        }

        var pool = candidates.ToList();
        if (options.Sessions.Count > 0)
        {
            var selected = new List<SessionMetaMatch>();
            foreach (var selector in options.Sessions)
            {
                var matches = await ResolveSelectorAsync(selector, pool, options, cancellationToken);
                if (matches is null)
                {
                    return null;
                }

                selected.AddRange(matches);
            }

            pool = selected
                .DistinctBy(session => session.File, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(session => ParseTimestamp(session.UpdatedAt ?? session.StartedAt) ?? DateTimeOffset.MinValue)
                .ToList();
        }

        IEnumerable<SessionMetaMatch> filtered = pool;
        if (options.Since is not null || options.Until is not null)
        {
            filtered = filtered.Where(session => IsActiveInWindow(session, options.Since, options.Until));
        }

        if (options.Match is not null)
        {
            filtered = filtered.Where(session => GetTitle(session).Contains(options.Match, StringComparison.OrdinalIgnoreCase));
        }

        if (options.Limit is not null)
        {
            filtered = filtered.Take(options.Limit.Value);
        }

        return filtered.ToList();
    }

    private async Task<IReadOnlyList<SessionMetaMatch>?> ResolveSelectorAsync(
        string selector,
        List<SessionMetaMatch> pool,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        if (selector.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            var path = ResolvePath(selector, currentDirectory);
            var known = pool.Where(session => string.Equals(Path.GetFullPath(session.File), path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (known.Count > 0)
            {
                return known;
            }

            // A file outside the Claude home (a copy, a sanitized session) is exported as it is.
            var meta = File.Exists(path) ? await service.ReadSessionMetaAsync(path, cancellationToken) : null;
            if (meta is null)
            {
                error.WriteLine($"clodlogs: session file not found or not readable: {path}");
                return null;
            }

            return [meta];
        }

        var exact = pool.Where(session =>
                string.Equals(session.Id, selector, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(session.File), selector, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        var prefixed = pool.Where(session =>
                session.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(session.File).StartsWith(selector, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ids = prefixed.Select(session => session.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 1)
        {
            return prefixed;
        }

        if (ids.Count > 1)
        {
            error.WriteLine($"clodlogs: session '{selector}' is ambiguous; it matches {ids.Count} sessions:");
            foreach (var id in ids.Take(10))
            {
                error.WriteLine($"  {id}");
            }

            return null;
        }

        var scope = options.ProjectDirectory is null ? "" : " in the chosen project";
        error.WriteLine($"clodlogs: no session matches '{selector}'{scope}.");
        return null;
    }

    private int WriteList(CliOptions options, List<SessionMetaMatch> sessions)
    {
        if (options.Json)
        {
            var array = new JsonArray(sessions.Select(session => (JsonNode)ToJson(session)).ToArray());
            output.WriteLine(array.ToJsonString(JsonOptions));
            return ExitSuccess;
        }

        if (sessions.Count == 0)
        {
            output.WriteLine("No sessions matched.");
            return ExitSuccess;
        }

        foreach (var session in sessions)
        {
            var subagent = IsSubagentFile(session.File) ? " [subagent]" : "";
            output.WriteLine($"{FormatLocal(session.UpdatedAt ?? session.StartedAt),-16}  {ShortId(session.Id)}  {ClaudeSessionService.FormatByteCount(session.FileSizeBytes),9}  {GetTitle(session)}{subagent}");
            output.WriteLine($"{"",-16}  {"",-8}  {"",9}  {session.Cwd}");
        }

        output.WriteLine();
        output.WriteLine($"{sessions.Count} session{(sessions.Count == 1 ? "" : "s")}.");
        return ExitSuccess;
    }

    private async Task<int> ExportAsync(CliOptions options, List<SessionMetaMatch> sessions, CancellationToken cancellationToken)
    {
        var outputDirectory = options.OutputDirectory ?? Path.Combine(currentDirectory, "export");
        var exportOptions = new SessionExportOptions(options.IncludeImages, options.InlineImages, options.IncludeToolCalls, options.IncludeThinking);
        var results = new JsonArray();
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new JsonArray();
        if (sessions.Count == 0)
        {
            WriteExportSummary(options, outputDirectory, results, failed, "No sessions matched; nothing was exported.");
            return ExitSuccess;
        }

        foreach (var session in sessions)
        {
            foreach (var format in options.Formats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new BatchExportSessionRequest(session.File, GetTitle(session), session.StartedAt);
                if (options.DryRun)
                {
                    var plannedPath = ClaudeSessionService.PlanExportPath(format, request, exportOptions, outputDirectory, planned);
                    results.Add(new JsonObject { ["id"] = session.Id, ["file"] = session.File, ["format"] = format, ["output"] = plannedPath });
                    if (!options.Json) output.WriteLine($"would write {plannedPath}");
                    continue;
                }

                try
                {
                    var path = await service.ExportSessionToDirectoryAsync(format, request, exportOptions, outputDirectory, cancellationToken: cancellationToken, planned: planned);
                    results.Add(new JsonObject { ["id"] = session.Id, ["file"] = session.File, ["format"] = format, ["output"] = path });
                    if (!options.Json) output.WriteLine($"wrote {path}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed.Add(new JsonObject { ["id"] = session.Id, ["file"] = session.File, ["format"] = format, ["error"] = ex.Message });
                    error.WriteLine($"clodlogs: failed to export {session.File} as {format}: {ex.Message}");
                }
            }
        }

        var verb = options.DryRun ? "Would export" : "Exported";
        var summary = failed.Count == 0
            ? $"{verb} {results.Count} file{(results.Count == 1 ? "" : "s")} from {sessions.Count} session{(sessions.Count == 1 ? "" : "s")} to {outputDirectory}."
            : $"{verb} {results.Count} file{(results.Count == 1 ? "" : "s")}; {failed.Count} failed. Output folder: {outputDirectory}";
        WriteExportSummary(options, outputDirectory, results, failed, summary);
        return failed.Count == 0 ? ExitSuccess : ExitFailure;
    }

    private void WriteExportSummary(CliOptions options, string outputDirectory, JsonArray results, JsonArray failed, string summary)
    {
        if (options.Json)
        {
            var payload = new JsonObject
            {
                ["outputDirectory"] = outputDirectory,
                ["dryRun"] = options.DryRun,
                ["formats"] = new JsonArray(options.Formats.Select(format => (JsonNode?)format).ToArray()),
                [options.DryRun ? "planned" : "exported"] = results,
                ["failed"] = failed
            };
            output.WriteLine(payload.ToJsonString(JsonOptions));
            return;
        }

        output.WriteLine(summary);
    }

    private static JsonObject ToJson(SessionMetaMatch session) => new()
    {
        ["id"] = session.Id,
        ["title"] = GetTitle(session),
        ["cwd"] = session.Cwd,
        ["startedAt"] = session.StartedAt,
        ["updatedAt"] = session.UpdatedAt,
        ["sizeBytes"] = session.FileSizeBytes,
        ["subagent"] = IsSubagentFile(session.File),
        ["file"] = session.File
    };

    /// <summary>A session counts as active in the window when its first-to-last activity overlaps it.</summary>
    internal static bool IsActiveInWindow(SessionMetaMatch session, DateTimeOffset? since, DateTimeOffset? until)
    {
        var start = ParseTimestamp(session.StartedAt) ?? ParseTimestamp(session.UpdatedAt);
        var end = ParseTimestamp(session.UpdatedAt) ?? start;
        if (start is null || end is null)
        {
            return false;
        }

        return (since is null || end >= since) && (until is null || start < until);
    }

    internal static string GetTitle(SessionMetaMatch session)
        => string.IsNullOrWhiteSpace(session.ThreadName) ? Path.GetFileNameWithoutExtension(session.File) : session.ThreadName!;

    // Claude Code keeps the transcript of a subagent in <project>/<session id>/subagents/agent-*.jsonl.
    internal static bool IsSubagentFile(string file)
        => Path.GetFileName(Path.GetDirectoryName(file) ?? "").Equals("subagents", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? ParseTimestamp(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private string FormatLocal(string? timestamp)
        => ParseTimestamp(timestamp) is { } parsed
            ? TimeZoneInfo.ConvertTime(parsed, timeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "unknown";

    private static string ShortId(string id) => id.Length <= 8 ? id.PadRight(8) : id[..8];

    internal static string ResolvePath(string value, string baseDirectory)
    {
        var trimmed = value.Trim();
        return Path.IsPathRooted(trimmed) || trimmed.StartsWith("/mnt/", StringComparison.Ordinal) || trimmed.StartsWith(@"\\", StringComparison.Ordinal)
            ? ClaudeSessionService.ResolveFilesystemPath(trimmed)
            : Path.GetFullPath(trimmed, baseDirectory);
    }

    private static string GetVersion()
        => typeof(ClodlogsCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(ClodlogsCli).Assembly.GetName().Version?.ToString()
            ?? "unknown";

    public const string HelpText = """
        Usage:
          clodlogs list   [filters] [--json]
          clodlogs export [filters] [export options] [--json] [--dry-run]
          clodlogs help | --version

        Without a command, clodlogs opens the desktop app.

        Filters (all optional; together they narrow the selection):
          -s, --session ID|FILE   A session id, a unique id prefix, a file name or a .jsonl path.
                                  Repeat it or separate values with commas for several sessions.
          -p, --project DIR       Only sessions of this folder. Inside a git repository the whole
                                  repository counts, unless --cwd-only is given. '.' is the current folder.
              --cwd-only          With --project, match only that folder tree, not the repository root.
              --cross-session-writes
                                  With --project, also include sessions from other folders that
                                  wrote files into the project.
              --since WHEN        Sessions with activity at or after WHEN.
              --until WHEN        Sessions with activity before WHEN.
              --from WHEN, --to WHEN
                                  Same as --since and --until.
                                  WHEN is a duration back from now (30m, 1h, 2d, 1w, 1h30m), a date
                                  (2026-11-20, a whole local day; --until includes that day) or a
                                  date and time (2026-11-20T14:00, local time unless an offset is given).
                                  With --since, files not written since WHEN are not read at all.
              --match TEXT        Only sessions whose title contains TEXT (case-insensitive).
              --limit N           Only the N most recently active sessions.
              --include-subagents Also list the transcripts of subagents (left out by default).
              --claude-home DIR   The Claude home folder. Defaults to %CLAUDE_HOME% or ~/.claude.

        Export options:
          -o, --out DIR           Output folder, created when missing. Defaults to .\export.
                                  Existing files are never replaced; a numbered name is used instead.
          -f, --format LIST       markdown (md), html or opencode; several separated by commas.
                                  Defaults to markdown.
              --images            Include images (Markdown and HTML: a sibling assets folder).
              --inline-images     HTML only: embed the images in the file. Implies --images.
              --tool-calls        Include tool calls and tool outputs.
              --thinking          Include thinking.
              --full              Same as --images --tool-calls --thinking.
              --dry-run           Show the files that would be written and write nothing.
                                  With --json, they are listed under "planned".

        Output:
              --json              Print machine-readable JSON.
          -h, --help              Show this help text.

        Exit codes: 0 success, 1 a session was not found or an export failed, 2 invalid arguments.

        Examples:
          clodlogs list --since 1h
          clodlogs export --since 1h --out D:\exports --format markdown,html --tool-calls
          clodlogs export --project C:\github\myrepo --from 2026-11-01 --to 2026-11-15 -o D:\exports
          clodlogs export -s af32f898,9229c2f9 -f opencode --full -o D:\exports
        """;

    private sealed class CliUsageException(string message) : Exception(message);

    private sealed class CliOptions
    {
        public bool Help { get; private set; }
        public bool Json { get; private set; }
        public bool DryRun { get; private set; }
        public List<string> Sessions { get; } = [];
        public string? ProjectDirectory { get; private set; }
        public bool CwdOnly { get; private set; }
        public bool CrossSessionWrites { get; private set; }
        public DateTimeOffset? Since { get; private set; }
        public DateTimeOffset? Until { get; private set; }
        public string? Match { get; private set; }
        public int? Limit { get; private set; }
        public bool IncludeSubagents { get; private set; }
        public string? ClaudeHome { get; private set; }
        public string? OutputDirectory { get; private set; }
        public List<string> Formats { get; } = [];
        public bool IncludeImages { get; private set; }
        public bool InlineImages { get; private set; }
        public bool IncludeToolCalls { get; private set; }
        public bool IncludeThinking { get; private set; }

        public static CliOptions Parse(string command, string[] args, string currentDirectory, DateTimeOffset now, TimeZoneInfo timeZone)
        {
            var options = new CliOptions();
            var isExport = command == "export";
            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index];
                string? inlineValue = null;
                if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') is var equals and > 2)
                {
                    inlineValue = arg[(equals + 1)..];
                    arg = arg[..equals];
                }

                string Value()
                {
                    if (inlineValue is not null)
                    {
                        return inlineValue;
                    }

                    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliUsageException($"{arg} needs a value.");
                    }

                    return args[++index];
                }

                bool Flag()
                    => inlineValue is null ? true : throw new CliUsageException($"{arg} takes no value.");

                void ExportOnly()
                {
                    if (!isExport) throw new CliUsageException($"{arg} is an option of 'clodlogs export'.");
                }

                switch (arg)
                {
                    case "-h" or "--help":
                        options.Help = Flag();
                        break;
                    case "--json":
                        options.Json = Flag();
                        break;
                    case "-s" or "--session":
                        options.Sessions.AddRange(Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        break;
                    case "-p" or "--project":
                        options.ProjectDirectory = ResolvePath(Value(), currentDirectory);
                        break;
                    case "--cwd-only":
                        options.CwdOnly = Flag();
                        break;
                    case "--cross-session-writes":
                        options.CrossSessionWrites = Flag();
                        break;
                    case "--since" or "--from":
                        options.Since = ParseWhen(arg, Value(), now, timeZone, endOfDay: false);
                        break;
                    case "--until" or "--to":
                        options.Until = ParseWhen(arg, Value(), now, timeZone, endOfDay: true);
                        break;
                    case "--match":
                        options.Match = Value();
                        break;
                    case "--limit":
                        var limitText = Value();
                        options.Limit = int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit > 0
                            ? limit
                            : throw new CliUsageException($"--limit needs a positive number, not '{limitText}'.");
                        break;
                    case "--include-subagents":
                        options.IncludeSubagents = Flag();
                        break;
                    case "--claude-home":
                        options.ClaudeHome = ResolvePath(Value(), currentDirectory);
                        break;
                    case "-o" or "--out":
                        ExportOnly();
                        options.OutputDirectory = ResolvePath(Value(), currentDirectory);
                        break;
                    case "-f" or "--format":
                        ExportOnly();
                        options.Formats.AddRange(ParseFormats(Value()).Except(options.Formats));
                        break;
                    case "--images":
                        ExportOnly();
                        options.IncludeImages = Flag();
                        break;
                    case "--inline-images":
                        ExportOnly();
                        options.IncludeImages = options.InlineImages = Flag();
                        break;
                    case "--tool-calls":
                        ExportOnly();
                        options.IncludeToolCalls = Flag();
                        break;
                    case "--thinking":
                        ExportOnly();
                        options.IncludeThinking = Flag();
                        break;
                    case "--full":
                        ExportOnly();
                        options.IncludeImages = options.IncludeToolCalls = options.IncludeThinking = Flag();
                        break;
                    case "--dry-run":
                        ExportOnly();
                        options.DryRun = Flag();
                        break;
                    default:
                        throw new CliUsageException(arg.StartsWith('-') ? $"Unknown option '{arg}'." : $"Unexpected argument '{arg}'.");
                }
            }

            if (options.Formats.Count == 0)
            {
                options.Formats.Add("markdown");
            }

            if ((options.CwdOnly || options.CrossSessionWrites) && options.ProjectDirectory is null)
            {
                throw new CliUsageException("--cwd-only and --cross-session-writes need --project.");
            }

            if (options.Since is not null && options.Until is not null && options.Since >= options.Until)
            {
                throw new CliUsageException("--since must be earlier than --until.");
            }

            return options;
        }

        private static IEnumerable<string> ParseFormats(string value)
            => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(format => format.ToLowerInvariant() switch
                {
                    "markdown" or "md" => "markdown",
                    "html" => "html",
                    "opencode" => ClaudeSessionService.OpenCodeExportFormat,
                    _ => throw new CliUsageException($"Unknown format '{format}'. Use markdown, html or opencode.")
                })
                .Distinct()
                .ToList();

        private static DateTimeOffset ParseWhen(string option, string value, DateTimeOffset now, TimeZoneInfo timeZone, bool endOfDay)
        {
            try
            {
                return ParseWhen(value.Trim(), now, timeZone, endOfDay)
                    ?? throw new CliUsageException($"{option} needs a duration (1h, 30m, 2d), a date (2026-11-20) or a date and time, not '{value}'.");
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
            {
                throw new CliUsageException($"{option} is out of range: '{value}'.");
            }
        }

        private static DateTimeOffset? ParseWhen(string text, DateTimeOffset now, TimeZoneInfo timeZone, bool endOfDay)
        {
            if (DurationPattern().IsMatch(text))
            {
                var total = TimeSpan.Zero;
                foreach (Match part in DurationPartPattern().Matches(text))
                {
                    var amount = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
                    total += part.Groups[2].Value.ToLowerInvariant() switch
                    {
                        "w" => TimeSpan.FromDays(7 * amount),
                        "d" => TimeSpan.FromDays(amount),
                        "h" => TimeSpan.FromHours(amount),
                        "m" or "min" => TimeSpan.FromMinutes(amount),
                        _ => TimeSpan.FromSeconds(amount)
                    };
                }

                return now - total;
            }

            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                // A date is a whole local day; --until includes it, so it ends at the next midnight.
                var day = date.ToDateTime(TimeOnly.MinValue);
                return InZone(endOfDay ? day.AddDays(1) : day, timeZone);
            }

            // A time with Z or an offset is an instant; one without is local time in the given zone.
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var parsed))
            {
                return null;
            }

            return parsed.Kind == DateTimeKind.Unspecified ? InZone(parsed, timeZone) : new DateTimeOffset(parsed.ToUniversalTime(), TimeSpan.Zero);
        }

        private static DateTimeOffset InZone(DateTime time, TimeZoneInfo timeZone)
            => new(time, timeZone.GetUtcOffset(time));
    }

    [GeneratedRegex(@"^(\d+(\.\d+)?\s*(w|d|h|min|m|s)\s*)+$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(w|d|h|min|m|s)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPartPattern();
}
