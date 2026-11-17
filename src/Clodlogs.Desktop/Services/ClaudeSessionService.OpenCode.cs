using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Clodlogs.Desktop.Models;

namespace Clodlogs.Desktop.Services;

// Converts Claude session rows into the session transfer document that
// `opencode session import` of OpenCode 2 accepts: { info, messages }.
// The shapes follow SessionTransfer.Data of OpenCode 2.3 (packages/schema).
public sealed partial class ClaudeSessionService
{
    public const string OpenCodeExportFormat = "opencode";
    public const string OpenCodeSessionFileSuffix = ".opencode.json";
    private const string OpenCodeIdentifierAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int OpenCodeIdentifierSuffixLength = 14;
    private const int OpenCodeIdentifierMaxCounter = 0xfff;
    // Claude Code writes rows under this model name itself: API errors and "No response requested."
    private const string ClaudeSyntheticModel = "<synthetic>";
    private const string ClaudeNoResponsePlaceholder = "No response requested.";

    /// <summary>
    /// Builds an identifier with OpenCode's layout: a prefix, twelve hex digits holding the
    /// timestamp and a per-millisecond counter, and fourteen base62 characters. OpenCode draws
    /// those characters at random; here they come from <paramref name="seed"/>, so the same
    /// Claude session always yields the same identifiers and a second import is reported as
    /// "Session already exists" instead of adding a copy.
    /// </summary>
    public static string BuildOpenCodeIdentifier(string prefix, long timestampMs, int counter, bool descending, string seed)
    {
        var current = (ulong)Math.Max(0, timestampMs) * 0x1000UL + (ulong)counter;
        var value = (descending ? ~current : current) & 0xFFFF_FFFF_FFFFUL;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var suffix = new char[OpenCodeIdentifierSuffixLength];
        for (var index = 0; index < suffix.Length; index++)
        {
            suffix[index] = OpenCodeIdentifierAlphabet[digest[index] % OpenCodeIdentifierAlphabet.Length];
        }

        return $"{prefix}_{value.ToString("x12", CultureInfo.InvariantCulture)}{new string(suffix)}";
    }

    private static bool GetBoolean(JsonObject? obj, string property)
        => obj is not null
            && obj.TryGetPropertyValue(property, out var node)
            && node is JsonValue value
            && value.TryGetValue<bool>(out var result)
            && result;

    private sealed class OpenCodeSessionConverter(
        string sessionId,
        string cwd,
        long fallbackTimestampMs,
        SessionExportOptions options)
    {
        private const string Agent = "build";
        private const string Provider = "anthropic";
        private const string UnknownModel = "unknown";
        private const string MissingToolResultMessage = "No tool result was recorded in the Claude session.";
        private const int MaxDerivedTitleLength = 80;
        private const int MaxToolErrorMessageLength = 500;

        private readonly Queue<PendingMessage> _queue = new();
        private readonly Dictionary<string, PendingToolCall> _pendingCalls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SessionTokenUsageRecord> _usageByMessage = new(StringComparer.Ordinal);
        private long? _firstTimestampMs;
        private long? _lastTimestampMs;
        private long? _lastActivityMs;
        private long _idTimestampMs;
        private int _idCounter;
        private int _messageCount;
        private int _imageCount;
        private int _anonymousCallCount;
        private string? _modelId;
        private string? _customTitle;
        private string? _aiTitle;
        private string? _firstPromptText;
        private string? _claudeVersion;
        private string _compactionReason = "auto";
        private bool _sawFirstUserMessage;
        // The previous message row if it was a user row with text; some rows are read in pairs.
        private ClaudeUserRowKind? _previousUserRow;

        // The model response that is being read; its message object exists once it has content.
        private PendingMessage? _currentStep;
        private string? _stepMessageId;
        private string? _stepStopReason;
        private SessionTokenUsage? _stepUsage;
        private bool _stepIsApiError;

        private int _messagesSinceIdle;
        private bool _assistantSinceIdle;
        private bool _promptSinceIdle;
        private long? _lastIdleMs;
        private string? _lastOutcome;

        // Returns the messages that are complete after this row, in transcript order.
        public List<JsonObject> Push(JsonObject record)
        {
            var timestampMs = ResolveTimestamp(GetString(record, "timestamp"));
            _claudeVersion ??= GetString(record, "version");
            switch (GetString(record, "type"))
            {
                case "custom-title":
                    _customTitle = NonBlank(GetString(record, "customTitle")) ?? _customTitle;
                    break;
                case "ai-title":
                    _aiTitle = NonBlank(GetString(record, "aiTitle")) ?? _aiTitle;
                    break;
                case "system":
                    if (GetString(record, "subtype") == "compact_boundary")
                    {
                        _compactionReason = GetString(record["compactMetadata"] as JsonObject, "trigger") == "manual" ? "manual" : "auto";
                    }
                    break;
                default:
                    // Sidechain rows belong to a subagent, not to this conversation.
                    if (IsClaudeTranscriptRecord(record) && !GetBoolean(record, "isSidechain"))
                    {
                        PushMessageRecord(record, timestampMs);
                    }
                    break;
            }

            return Flush();
        }

        public (List<JsonObject> Messages, JsonObject Info) Finish()
        {
            var timestampMs = _lastTimestampMs ?? fallbackTimestampMs;
            var outcome = _stepIsApiError ? "failed" : _stepStopReason switch
            {
                "end_turn" or "stop_sequence" => "succeeded",
                // A turn that still waits for a tool or never got an answer was cut off.
                null or "tool_use" or "pause_turn" => "interrupted",
                _ => "failed"
            };
            // A prompt without an answer was cut off; injected text without an answer changes nothing.
            EndTurn(
                _lastActivityMs ?? timestampMs,
                _assistantSinceIdle ? outcome : _promptSinceIdle ? "interrupted" : _lastOutcome ?? "succeeded");
            return (Flush(), BuildInfo());
        }

        private void PushMessageRecord(JsonObject record, long timestampMs)
        {
            var message = record["message"] as JsonObject;
            var content = message?["content"] switch
            {
                JsonArray array => array,
                JsonValue value when value.TryGetValue<string>(out var text) => new JsonArray(text),
                _ => null
            };
            if (message is null || content is null)
            {
                return;
            }

            switch (GetString(message, "role"))
            {
                case "assistant":
                    PushAssistantRecord(record, message, content, timestampMs);
                    break;
                case "user":
                    PushUserRecord(record, content, timestampMs);
                    break;
            }
        }

        // Claude writes one row per content block; rows with the same message id are one model response.
        private void PushAssistantRecord(JsonObject record, JsonObject message, JsonArray content, long timestampMs)
        {
            _previousUserRow = null;
            var model = GetString(message, "model");
            var isApiError = GetBoolean(record, "isApiErrorMessage");
            // Claude Code writes this row itself where the model was not asked; it belongs to no response.
            if (model == ClaudeSyntheticModel && !isApiError && ReadClaudeTextBlocks(content) is [var onlyText] && onlyText.Trim() == ClaudeNoResponsePlaceholder)
            {
                return;
            }

            var messageId = GetString(message, "id");
            if (messageId is null || messageId != _stepMessageId)
            {
                CloseStep();
                _stepMessageId = messageId;
                _stepStopReason = null;
                _stepUsage = null;
            }

            if (!string.IsNullOrWhiteSpace(model) && model != ClaudeSyntheticModel)
            {
                _modelId = model;
            }
            _stepStopReason = GetString(message, "stop_reason") ?? _stepStopReason;
            _stepIsApiError = isApiError;
            if (ExtractClaudeUsage(record) is { } usage)
            {
                _usageByMessage[usage.Key] = usage;
                _stepUsage = usage.Usage;
            }
            _assistantSinceIdle = true;
            _lastActivityMs = timestampMs;
            if (isApiError)
            {
                // The text is a notice of Claude Code, not an answer; the turn ends as failed instead.
                return;
            }

            foreach (var raw in content)
            {
                if (raw is JsonValue value && value.TryGetValue<string>(out var plainText))
                {
                    AddStepText(plainText, timestampMs);
                    continue;
                }

                if (raw is not JsonObject part) continue;
                switch (GetString(part, "type"))
                {
                    case "thinking":
                        if (options.IncludeThinking && NonBlank(GetString(part, "thinking")) is { } thinking)
                        {
                            OpenStep(timestampMs).Add(new JsonObject { ["type"] = "reasoning", ["text"] = thinking.Trim() });
                        }
                        break;
                    case "text":
                        AddStepText(GetString(part, "text"), timestampMs);
                        break;
                    case "tool_use":
                        if (options.IncludeToolCallResults) AddToolCall(part, timestampMs);
                        break;
                }
            }
        }

        private void PushUserRecord(JsonObject record, JsonArray content, long timestampMs)
        {
            var (texts, files) = CollectUserContent(content, timestampMs);
            var previousRow = _previousUserRow;
            _previousUserRow = null;
            if (texts.Count == 0 && files.Count == 0)
            {
                return;
            }

            var row = ReadClaudeUserRow(record, texts, files.Count > 0, previousRow);
            _previousUserRow = row.Kind;
            var previousActivityMs = _lastActivityMs ?? timestampMs;
            // Rows for Claude Code's own interface are no activity of the conversation.
            if (row.Kind is not (ClaudeUserRowKind.Housekeeping or ClaudeUserRowKind.LocalCommandCaveat or ClaudeUserRowKind.Interruption))
            {
                _lastActivityMs = timestampMs;
            }

            switch (row.Kind)
            {
                case ClaudeUserRowKind.Interruption:
                    // The marker is not text of the user; the idle marker of the turn carries the outcome.
                    if (_assistantSinceIdle || _promptSinceIdle)
                    {
                        EndTurn(previousActivityMs, "interrupted");
                    }
                    else
                    {
                        CloseStep();
                    }
                    break;
                case ClaudeUserRowKind.CompactSummary:
                    CloseStep();
                    Enqueue(NewMessage("compaction", timestampMs, message =>
                    {
                        message["status"] = "completed";
                        message["reason"] = _compactionReason;
                        message["summary"] = row.Text;
                    }));
                    _compactionReason = "auto";
                    break;
                case ClaudeUserRowKind.DelegatedWork:
                    // Follows the option for tool calls, like the tool result it stands for.
                    EndFinishedTurn(previousActivityMs);
                    if (options.IncludeToolCallResults) EnqueueSynthetic(row.Text, timestampMs);
                    break;
                case ClaudeUserRowKind.HostTurn:
                    EndFinishedTurn(previousActivityMs);
                    EnqueueSynthetic(row.Text, timestampMs);
                    break;
                case ClaudeUserRowKind.ToolCompanion:
                    // A loaded skill or the scale of a screenshot only matters together with the tool calls.
                    if (options.IncludeToolCallResults) PushInjectedText(row.Text, timestampMs);
                    break;
                case ClaudeUserRowKind.Injected:
                    PushInjectedText(row.Text, timestampMs);
                    break;
                case ClaudeUserRowKind.Prompt:
                case ClaudeUserRowKind.SlashCommand:
                    PushPrompt(row, files, timestampMs, previousActivityMs);
                    break;
            }
        }

        // The texts and the embedded images of a user row. Tool results in the row go to their tool calls.
        private (List<string> Texts, JsonArray Files) CollectUserContent(JsonArray content, long timestampMs)
        {
            var texts = new List<string>();
            var files = new JsonArray();
            foreach (var raw in content)
            {
                if (raw is JsonValue value && value.TryGetValue<string>(out var plainText))
                {
                    if (!string.IsNullOrWhiteSpace(plainText)) texts.Add(plainText);
                    continue;
                }

                if (raw is not JsonObject part) continue;
                if (GetString(part, "type") == "tool_result")
                {
                    if (options.IncludeToolCallResults) AttachToolResult(part, timestampMs);
                    continue;
                }

                if (LooksLikeImageContentPart(part))
                {
                    if (options.IncludeImages && ReadImage(part) is { } image)
                    {
                        files.Add(new JsonObject
                        {
                            ["data"] = image.Base64,
                            ["mime"] = image.Mime,
                            ["source"] = new JsonObject { ["type"] = "inline" },
                            ["name"] = image.Name
                        });
                    }
                    continue;
                }

                var text = GetMessageContentText(part) ?? part.ToJsonString(CompactJsonOptions);
                if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
            }

            return (texts, files);
        }

        private void PushPrompt(ClaudeUserRow row, JsonArray files, long timestampMs, long previousActivityMs)
        {
            if (!_sawFirstUserMessage && LooksLikeBootstrapContext(row.Text))
            {
                _sawFirstUserMessage = true;
                return;
            }

            _sawFirstUserMessage = true;
            if (_assistantSinceIdle)
            {
                var outcome = row.Text.StartsWith(ClaudeInterruptionPrefix, StringComparison.Ordinal) || _pendingCalls.Count > 0
                    ? "interrupted"
                    : _stepIsApiError ? "failed" : "succeeded";
                EndTurn(previousActivityMs, outcome);
            }
            else
            {
                CloseStep();
            }

            // A reminder of the host in front of the typed text is not text of the user.
            EnqueueSynthetic(row.InjectedText, timestampMs);
            _firstPromptText ??= NonBlank(row.Text);
            _promptSinceIdle = true;
            Enqueue(NewMessage("user", timestampMs, message =>
            {
                message["text"] = row.Text;
                if (files.Count > 0) message["files"] = files;
            }));
        }

        // A row of the host or about delegated work starts a turn of its own once the previous response has finished.
        private void EndFinishedTurn(long timestampMs)
        {
            if (_assistantSinceIdle && _stepStopReason is not (null or "tool_use" or "pause_turn"))
            {
                EndTurn(timestampMs, _stepIsApiError ? "failed" : "succeeded");
            }
            else
            {
                CloseStep();
            }
        }

        // Text that Claude Code added for the model in the middle of a turn.
        private void PushInjectedText(string text, long timestampMs)
        {
            CloseStep();
            EnqueueSynthetic(text, timestampMs);
        }

        private void EnqueueSynthetic(string text, long timestampMs)
        {
            if (text.Length > 0)
            {
                Enqueue(NewMessage("synthetic", timestampMs, message => message["text"] = text));
            }
        }

        private void AddStepText(string? text, long timestampMs)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                OpenStep(timestampMs).Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }
        }

        private void AddToolCall(JsonObject part, long timestampMs)
        {
            var callId = NonBlank(GetString(part, "id"));
            if (callId is null || _pendingCalls.ContainsKey(callId))
            {
                _anonymousCallCount++;
                callId = $"{callId ?? "claude_call"}_{_anonymousCallCount}";
            }

            // OpenCode stores tool input as an object.
            var input = part["input"]?.DeepClone() switch
            {
                JsonObject obj => obj,
                null => new JsonObject(),
                var other => new JsonObject { ["input"] = other }
            };
            var toolPart = new JsonObject
            {
                ["type"] = "tool",
                ["id"] = callId,
                ["name"] = NonBlank(GetString(part, "name")) ?? "unknown-tool",
                // Replaced when the tool result arrives; kept if the session ends without one.
                ["state"] = BuildToolErrorState(input, "aborted", MissingToolResultMessage, null),
                ["time"] = new JsonObject { ["created"] = timestampMs, ["ran"] = timestampMs }
            };
            OpenStep(timestampMs).Add(toolPart);
            _currentStep!.OpenCalls++;
            _pendingCalls[callId] = new PendingToolCall(_currentStep, toolPart);
        }

        private void AttachToolResult(JsonObject part, long timestampMs)
        {
            var callId = GetString(part, "tool_use_id");
            if (callId is null || !_pendingCalls.Remove(callId, out var call))
            {
                return;
            }

            var previousState = (JsonObject)call.Part["state"]!;
            var input = (JsonObject)previousState["input"]!;
            previousState.Remove("input");
            var content = ReadToolResultContent(part["content"]);
            call.Part["state"] = GetBoolean(part, "is_error")
                ? BuildToolErrorState(input, "tool.execution", SummarizeToolError(content), content)
                : new JsonObject { ["status"] = "completed", ["input"] = input, ["content"] = content };
            var time = (JsonObject)call.Part["time"]!;
            time["completed"] = Math.Max(time["created"]!.GetValue<long>(), timestampMs);
            call.Owner.OpenCalls--;
            ExtendCompletion(call.Owner.Message, timestampMs);
            _lastActivityMs = timestampMs;
        }

        private static JsonObject BuildToolErrorState(JsonObject input, string errorType, string message, JsonArray? content)
        {
            var state = new JsonObject
            {
                ["status"] = "error",
                ["input"] = input,
                ["error"] = new JsonObject { ["type"] = errorType, ["message"] = message }
            };
            if (content is not null) state["content"] = content;
            return state;
        }

        private static string SummarizeToolError(JsonArray content)
        {
            var text = content
                .Select(item => GetString(item as JsonObject, "text")?.Trim())
                .FirstOrDefault(item => !string.IsNullOrEmpty(item));
            if (text is null) return "Tool call failed.";
            return text.Length <= MaxToolErrorMessageLength ? text : $"{text[..MaxToolErrorMessageLength].TrimEnd()}...";
        }

        private JsonArray ReadToolResultContent(JsonNode? node)
        {
            var content = new JsonArray();
            void AddText(string? text)
            {
                if (!string.IsNullOrEmpty(text)) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }

            switch (node)
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    AddText(text);
                    break;
                case JsonArray array:
                    var loadedTools = new List<string>();
                    foreach (var raw in array)
                    {
                        if (raw is JsonValue item && item.TryGetValue<string>(out var itemText))
                        {
                            AddText(itemText);
                        }
                        else if (raw is JsonObject reference && GetString(reference, "type") == "tool_reference")
                        {
                            // The tool search answers with one reference per tool that it loaded.
                            if (NonBlank(GetString(reference, "tool_name")) is { } toolName) loadedTools.Add(toolName);
                        }
                        else if (raw is JsonObject part && LooksLikeImageContentPart(part))
                        {
                            if (options.IncludeImages && ReadImage(part) is { } image)
                            {
                                content.Add(new JsonObject
                                {
                                    ["type"] = "file",
                                    ["uri"] = $"data:{image.Mime};base64,{image.Base64}",
                                    ["mime"] = image.Mime,
                                    ["name"] = image.Name
                                });
                            }
                        }
                        else if (raw is JsonObject other)
                        {
                            AddText(GetString(other, "type") == "text" ? GetString(other, "text") : other.ToJsonString(CompactJsonOptions));
                        }
                    }

                    if (loadedTools.Count > 0) AddText($"Loaded tools: {string.Join(", ", loadedTools)}");
                    break;
                case not null:
                    AddText(node.ToJsonString(CompactJsonOptions));
                    break;
            }

            // OpenCode rejects a finished tool call without content.
            if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = "" });
            return content;
        }

        private JsonArray OpenStep(long timestampMs)
        {
            if (_currentStep is null)
            {
                _currentStep = Enqueue(NewMessage("assistant", timestampMs, message =>
                {
                    ((JsonObject)message["time"]!)["completed"] = timestampMs;
                    message["agent"] = Agent;
                    message["model"] = new JsonObject { ["id"] = _modelId ?? UnknownModel, ["providerID"] = Provider };
                    message["content"] = new JsonArray();
                }));
            }
            else
            {
                ExtendCompletion(_currentStep.Message, timestampMs);
            }

            return (JsonArray)_currentStep.Message["content"]!;
        }

        private void CloseStep()
        {
            if (_currentStep is null)
            {
                return;
            }

            var message = _currentStep.Message;
            var hasToolCalls = ((JsonArray)message["content"]!).Any(part => GetString(part as JsonObject, "type") == "tool");
            message["finish"] = _stepIsApiError ? "error" : _stepStopReason switch
            {
                "tool_use" => "tool-calls",
                "end_turn" or "stop_sequence" => "stop",
                "max_tokens" => "length",
                "refusal" => "content-filter",
                _ => hasToolCalls ? "tool-calls" : "stop"
            };
            if (_stepUsage is not null)
            {
                message["tokens"] = BuildTokens(_stepUsage);
            }
            _currentStep = null;
        }

        private void FailOpenCalls(long timestampMs)
        {
            foreach (var call in _pendingCalls.Values)
            {
                var time = (JsonObject)call.Part["time"]!;
                time["completed"] = Math.Max(time["created"]!.GetValue<long>(), timestampMs);
                call.Owner.OpenCalls--;
            }
            _pendingCalls.Clear();
        }

        private void EndTurn(long timestampMs, string outcome)
        {
            CloseStep();
            // A tool result cannot arrive after its turn ended.
            FailOpenCalls(timestampMs);
            _assistantSinceIdle = false;
            _promptSinceIdle = false;
            _stepStopReason = null;
            _stepIsApiError = false;
            if (_messagesSinceIdle == 0)
            {
                return;
            }

            _queue.Enqueue(new PendingMessage(NewMessage("idle", timestampMs, message => message["outcome"] = outcome)));
            _messagesSinceIdle = 0;
            _lastIdleMs = timestampMs;
            _lastOutcome = outcome;
        }

        private PendingMessage Enqueue(JsonObject message)
        {
            var pending = new PendingMessage(message);
            _queue.Enqueue(pending);
            _messagesSinceIdle++;
            return pending;
        }

        // A message stays queued while it is the response being read or waits for a tool result.
        private List<JsonObject> Flush()
        {
            var ready = new List<JsonObject>();
            while (_queue.TryPeek(out var head) && head != _currentStep && head.OpenCalls == 0)
            {
                ready.Add(_queue.Dequeue().Message);
            }
            return ready;
        }

        private JsonObject NewMessage(string type, long timestampMs, Action<JsonObject> fill)
        {
            if (timestampMs > _idTimestampMs)
            {
                _idTimestampMs = timestampMs;
                _idCounter = 0;
            }
            _idCounter++;
            if (_idCounter > OpenCodeIdentifierMaxCounter)
            {
                _idTimestampMs++;
                _idCounter = 1;
            }
            _messageCount++;
            var message = new JsonObject
            {
                ["id"] = BuildOpenCodeIdentifier("msg", _idTimestampMs, _idCounter, false, $"claude:{sessionId}:message:{_messageCount}"),
                ["type"] = type,
                ["time"] = new JsonObject { ["created"] = timestampMs }
            };
            fill(message);
            return message;
        }

        private static void ExtendCompletion(JsonObject assistantMessage, long timestampMs)
        {
            var time = (JsonObject)assistantMessage["time"]!;
            time["completed"] = Math.Max(time["completed"]!.GetValue<long>(), timestampMs);
        }

        // Rows are written in order; a row without a usable timestamp takes the previous one.
        private long ResolveTimestamp(string? value)
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                && parsed.ToUnixTimeMilliseconds() is >= 0 and var timestampMs)
            {
                _firstTimestampMs ??= timestampMs;
                _lastTimestampMs = Math.Max(_lastTimestampMs ?? timestampMs, timestampMs);
            }

            return _lastTimestampMs ?? fallbackTimestampMs;
        }

        private JsonObject BuildInfo()
        {
            var createdMs = _firstTimestampMs ?? fallbackTimestampMs;
            var info = new JsonObject
            {
                ["id"] = BuildOpenCodeIdentifier("ses", createdMs, 1, true, $"claude:{sessionId}"),
                // The importer replaces this with the project of the directory it imports into.
                ["projectID"] = "global"
            };
            if (_modelId is not null)
            {
                info["model"] = new JsonObject { ["id"] = _modelId, ["providerID"] = Provider };
            }
            info["cost"] = 0;
            info["tokens"] = BuildTokens(SumClaudeTokenUsage(_usageByMessage) ?? SessionTokenUsage.Empty);
            if (_lastOutcome is not null) info["outcome"] = _lastOutcome;
            var time = new JsonObject
            {
                ["created"] = createdMs,
                ["updated"] = Math.Max(createdMs, _lastTimestampMs ?? createdMs)
            };
            if (_lastIdleMs is { } idleMs)
            {
                time["idle"] = idleMs;
                time["viewed"] = idleMs;
            }
            info["time"] = time;
            info["title"] = _customTitle ?? _aiTitle ?? DeriveTitle(_firstPromptText) ?? $"Claude session {sessionId}";
            info["location"] = new JsonObject { ["directory"] = cwd };
            var metadata = new JsonObject { ["source"] = "claude-code", ["claudeSessionId"] = sessionId };
            if (_claudeVersion is not null) metadata["claudeCodeVersion"] = _claudeVersion;
            metadata["exportedBy"] = "clodlogs";
            info["metadata"] = metadata;
            return info;
        }

        // Claude counts thinking inside output; OpenCode keeps the token kinds apart.
        private static JsonObject BuildTokens(SessionTokenUsage usage)
        {
            var reasoning = Math.Min(usage.ReasoningOutputTokens, usage.OutputTokens);
            return new JsonObject
            {
                ["input"] = usage.InputTokens,
                ["output"] = usage.OutputTokens - reasoning,
                ["reasoning"] = reasoning,
                ["cache"] = new JsonObject
                {
                    ["read"] = usage.CacheReadInputTokens,
                    ["write"] = usage.CacheCreationInputTokens
                }
            };
        }

        private static string? DeriveTitle(string? text)
        {
            var line = text?
                .Split('\n')
                .Select(entry => entry.Trim())
                .FirstOrDefault(entry => entry.Length > 0);
            if (line is null) return null;
            return line.Length <= MaxDerivedTitleLength ? line : $"{line[..(MaxDerivedTitleLength - 3)].TrimEnd()}...";
        }

        private static string? NonBlank(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value;

        // Only embedded images can travel inside the session file.
        private OpenCodeImage? ReadImage(JsonObject part)
        {
            string? mime;
            string? data;
            if (part["source"] is JsonObject source)
            {
                if (GetString(source, "type") != "base64") return null;
                mime = GetString(source, "media_type");
                data = GetString(source, "data");
            }
            else
            {
                var url = (GetString(part, "image_url") ?? GetString(part, "url"))?.Trim();
                var separator = url?.IndexOf(',') ?? -1;
                if (url is null || separator < 0 || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
                var header = url[5..separator];
                if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
                mime = header[..header.IndexOf(';')];
                data = url[(separator + 1)..];
            }

            mime = mime?.Trim().ToLowerInvariant();
            if (mime is null || !mime.StartsWith("image/", StringComparison.Ordinal) || mime.Length == "image/".Length
                || mime.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '+' or '-')))
            {
                return null;
            }

            var base64 = NormalizeBase64(data);
            if (base64 is null) return null;
            _imageCount++;
            var extension = ExtensionForMimeType(mime)
                ?? new string(mime["image/".Length..].Where(char.IsAsciiLetterOrDigit).ToArray());
            return new OpenCodeImage(mime, base64, $"image-{_imageCount:000}.{(extension.Length > 0 ? extension : "img")}");
        }

        // OpenCode accepts only padded base64 without line breaks.
        private static string? NormalizeBase64(string? data)
        {
            if (string.IsNullOrEmpty(data)) return null;
            var builder = new StringBuilder(data.Length + 2);
            var sawPadding = false;
            foreach (var character in data)
            {
                if (char.IsWhiteSpace(character)) continue;
                if (character == '=')
                {
                    sawPadding = true;
                    continue;
                }

                // Padding ends the data.
                if (sawPadding || !(char.IsAsciiLetterOrDigit(character) || character is '+' or '/')) return null;
                builder.Append(character);
            }

            if (builder.Length == 0 || builder.Length % 4 == 1) return null;
            builder.Append('=', (4 - builder.Length % 4) % 4);
            return builder.ToString();
        }

        private sealed class PendingMessage(JsonObject message)
        {
            public JsonObject Message { get; } = message;
            // Tool calls of this response that still wait for their result row.
            public int OpenCalls { get; set; }
        }

        private sealed record PendingToolCall(PendingMessage Owner, JsonObject Part);
        private sealed record OpenCodeImage(string Mime, string Base64, string Name);
    }
}
