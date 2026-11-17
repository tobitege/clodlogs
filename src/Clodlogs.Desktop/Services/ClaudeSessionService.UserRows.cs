using System.Text.Json.Nodes;

namespace Clodlogs.Desktop.Services;

// Tells apart the rows that Claude Code writes as user rows: prompts, slash commands, markers,
// notifications and the text that Claude Code or the host added. The prompt count and the
// OpenCode export both decide by ReadClaudeUserRow.
public sealed partial class ClaudeSessionService
{
    private const string ClaudeInterruptionPrefix = "[Request interrupted by user";

    // Elements that the host puts around a prompt.
    private static readonly (string Open, string Close)[] ClaudeInjectedElements =
    [
        ("<system-reminder>", "</system-reminder>"),
        ("<ide_opened_file>", "</ide_opened_file>"),
        ("<ide_selection>", "</ide_selection>")
    ];

    // Rows such as api-request-blob repeat conversation content; only user and assistant rows are the transcript.
    private static bool IsClaudeTranscriptRecord(JsonObject record)
        => GetString(record, "type") is null or "user" or "assistant";

    // Claude Code reports a finished background task or subagent as a user row. It is the late result of a tool call.
    private static bool IsClaudeTaskNotification(JsonObject record, string text)
        => GetString(record["origin"] as JsonObject, "kind") == "task-notification"
            || text.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal);

    // A user row that the host wrote to start a turn, such as a CI event, not something the user typed.
    private static bool IsClaudeHostTurn(JsonObject record)
        => GetString(record, "turnOrigin") is "auto_continuation" or "host_synthetic";

    // A message that another session or a subagent sent; like a task notification it reports delegated work.
    private static bool IsClaudePeerMessage(JsonObject record)
        => GetString(record["origin"] as JsonObject, "kind") == "peer";

    // Claude Code writes much more than prompts as user rows. This is what such a row is.
    private enum ClaudeUserRowKind
    {
        // Text that the user typed.
        Prompt,
        // A slash command of the user, such as /loop.
        SlashCommand,
        // The marker of an interrupted turn.
        Interruption,
        // The row in front of a command that Claude Code ran itself, such as /model.
        LocalCommandCaveat,
        // A row for Claude Code's own interface, which carries nothing for another program.
        Housekeeping,
        // The summary that replaces the conversation after a compaction.
        CompactSummary,
        // A task notification or a message of another session: the late result of delegated work.
        DelegatedWork,
        // A row of the host that starts a turn, such as a CI event or a reminder without a prompt.
        HostTurn,
        // Text that Claude Code added because of a tool call or a slash command, such as a loaded skill.
        ToolCompanion,
        // Other text that Claude Code added for the model.
        Injected
    }

    // Text is the prompt as typed for a prompt and a slash command, and the text of the row otherwise.
    // InjectedText is what the host put in front of a prompt, such as a reminder.
    private readonly record struct ClaudeUserRow(ClaudeUserRowKind Kind, string Text, string InjectedText = "");

    /// <summary>
    /// Decides what a user row is. <paramref name="texts"/> are its text blocks, and
    /// <paramref name="previousRow"/> is the kind of the message row right before it if that was a user
    /// row with text: a local command follows its caveat, and an expansion follows its slash command.
    /// </summary>
    private static ClaudeUserRow ReadClaudeUserRow(JsonObject record, IReadOnlyList<string> texts, bool hasImages, ClaudeUserRowKind? previousRow)
    {
        var text = string.Join("\n\n", texts);
        if (ClassifyClaudeHostRow(record, text, previousRow) is { } kind)
        {
            return new ClaudeUserRow(kind, text);
        }

        // A row of the user. The host can put blocks of its own in front of the typed text.
        var blocks = texts.ToLookup(IsClaudeInjectedBlock);
        var injected = string.Join("\n\n", blocks[true]);
        var typed = string.Join("\n\n", blocks[false]);
        if (typed.Length == 0 && !hasImages)
        {
            return new ClaudeUserRow(ClaudeUserRowKind.HostTurn, injected);
        }

        if (IsClaudeInterruptionMarker(typed))
        {
            return new ClaudeUserRow(ClaudeUserRowKind.Interruption, typed);
        }

        if (ReadClaudeSlashCommand(typed) is not { } command)
        {
            return new ClaudeUserRow(ClaudeUserRowKind.Prompt, typed, injected);
        }

        return previousRow == ClaudeUserRowKind.LocalCommandCaveat
            ? new ClaudeUserRow(ClaudeUserRowKind.Housekeeping, typed)
            : new ClaudeUserRow(ClaudeUserRowKind.SlashCommand, command, injected);
    }

    // The kind of a row that Claude Code or the host wrote, or null for a row of the user.
    private static ClaudeUserRowKind? ClassifyClaudeHostRow(JsonObject record, string text, ClaudeUserRowKind? previousRow)
    {
        var isMeta = GetBoolean(record, "isMeta");
        if (IsClaudeHousekeepingRow(isMeta, text)) return ClaudeUserRowKind.Housekeeping;
        if (isMeta && IsClaudeLocalCommandCaveat(text)) return ClaudeUserRowKind.LocalCommandCaveat;
        if (GetBoolean(record, "isCompactSummary")) return ClaudeUserRowKind.CompactSummary;
        if (IsClaudeTaskNotification(record, text) || IsClaudePeerMessage(record)) return ClaudeUserRowKind.DelegatedWork;
        if (IsClaudeHostTurn(record)) return ClaudeUserRowKind.HostTurn;
        if (!isMeta) return null;
        return previousRow == ClaudeUserRowKind.SlashCommand || IsClaudeToolCompanion(record, text)
            ? ClaudeUserRowKind.ToolCompanion
            : ClaudeUserRowKind.Injected;
    }

    // Claude Code marks an interrupted turn with a user row of its own.
    private static bool IsClaudeInterruptionMarker(string text)
    {
        var trimmed = text.AsSpan().Trim();
        return trimmed.StartsWith(ClaudeInterruptionPrefix) && trimmed.EndsWith("]") && !trimmed.Contains('\n');
    }

    // Claude Code writes this row in front of a command that it ran itself, such as /model or /compact.
    private static bool IsClaudeLocalCommandCaveat(string text)
        => text.AsSpan().TrimStart().StartsWith("<local-command-caveat>");

    // Rows that Claude Code wrote for its own interface: the output of a local command, the temporary
    // path of a pasted image, the resume marker and the list of tools used before a compaction.
    private static bool IsClaudeHousekeepingRow(bool isMeta, string text)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.StartsWith("<local-command-stdout>") || trimmed.StartsWith("<local-command-stderr>"))
        {
            return true;
        }

        return isMeta && (
            trimmed.StartsWith("[Image: source:")
            || trimmed.SequenceEqual("Continue from where you left off.")
            || IsClaudeToolsUsedNote(text));
    }

    // A reminder that holds nothing but the names of the tools used before a compaction.
    private static bool IsClaudeToolsUsedNote(string text)
    {
        if (!IsClaudeInjectedBlock(text) || ReadClaudeElement(text, "system-reminder") is not { } reminder)
        {
            return false;
        }

        var inner = reminder.AsSpan().Trim();
        return inner.StartsWith("<tools-used-before-summary>") && inner.EndsWith("</tools-used-before-summary>");
    }

    // Text that Claude Code added because of a tool call: a loaded skill or the scale of a screenshot.
    private static bool IsClaudeToolCompanion(JsonObject record, string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return record["sourceToolUseID"] is not null
            || trimmed.StartsWith("Base directory for this skill:")
            || trimmed.StartsWith("[Image: original ");
    }

    // True for text that consists only of elements the host puts around a prompt, such as a reminder
    // in front of the typed text.
    private static bool IsClaudeInjectedBlock(string text)
    {
        var rest = text.AsSpan().Trim();
        if (rest.IsEmpty)
        {
            return false;
        }

        while (!rest.IsEmpty)
        {
            var end = -1;
            foreach (var (open, close) in ClaudeInjectedElements)
            {
                if (!rest.StartsWith(open)) continue;
                var closeIndex = rest.IndexOf(close);
                end = closeIndex < 0 ? -1 : closeIndex + close.Length;
                break;
            }

            if (end < 0)
            {
                return false;
            }
            rest = rest[end..].TrimStart();
        }

        return true;
    }

    /// <summary>
    /// Reads a slash command row, which consists of the elements command-name, command-message and
    /// command-args, as the user typed it: "/loop 10m check the build". Returns null for other text.
    /// </summary>
    private static string? ReadClaudeSlashCommand(string text)
    {
        var trimmed = text.Trim();
        if (!(trimmed.StartsWith("<command-name>", StringComparison.Ordinal) || trimmed.StartsWith("<command-message>", StringComparison.Ordinal))
            || !trimmed.EndsWith('>'))
        {
            return null;
        }

        var name = ReadClaudeElement(trimmed, "command-name")?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var command = name.StartsWith('/') ? name : $"/{name}";
        var arguments = ReadClaudeElement(trimmed, "command-args")?.Trim();
        return string.IsNullOrEmpty(arguments) ? command : $"{command} {arguments}";
    }

    // The content of the first element with this name, or null.
    private static string? ReadClaudeElement(string text, string element)
    {
        var open = $"<{element}>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        var end = text.IndexOf($"</{element}>", start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }

    // The text blocks of a message that are not blank.
    private static List<string> ReadClaudeTextBlocks(JsonArray content)
    {
        var texts = new List<string>();
        foreach (var item in content)
        {
            var text = item is JsonValue value && value.TryGetValue<string>(out var plainText)
                ? plainText
                : item is JsonObject part && GetString(part, "type") == "text" ? GetString(part, "text") : null;
            if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
        }

        return texts;
    }
}
