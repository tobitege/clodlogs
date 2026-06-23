using System.Collections.ObjectModel;
using System.Diagnostics;
using Clodlogs.Desktop.Models;
using Clodlogs.Desktop.Services;

namespace Clodlogs.Desktop.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private const int SessionBrowserMaxEntries = 10000;
    private readonly ClaudeSessionService _sessions;
    private readonly IUiService _ui;
    private readonly AppSettingsService _settings;

    private FindClaudeSessionsResult? _result;
    private SessionCardViewModel? _selectedSession;
    private SessionDetailMetrics? _selectedMetrics;
    private EnvironmentCapabilities? _environment;
    private string _claudeHome = "";
    private string _folderPath = "";
    private string _searchQuery = "";
    private string _dateFrom = "";
    private string _dateTo = "";
    private bool _cwdOnly;
    private bool _includeCrossSessionWrites;
    private bool _showLiveSessions = true;
    private bool _showArchivedSessions = true;
    private bool _isLoading;
    private bool _isDetailLoading;
    private string? _errorMessage;
    private string _statusMessage = "Ready";
    private string _browseMode = "folder";
    private bool _exportDialogVisible;
    private bool _exportProgressVisible;
    private bool _sanitizeDialogVisible;
    private bool _sanitizeProgressVisible;
    private bool _transcriptDialogVisible;
    private bool _tokenSummaryDialogVisible;
    private bool _renameDialogVisible;
    private bool _renameSessionPending;
    private string _renameTitle = "";
    private string _exportFormat = "markdown";
    private bool _exportImages;
    private bool _exportInlineImages = true;
    private bool _exportToolCallResults;
    private string _operationTitle = "Ready";
    private string _operationMessage = "";
    private string _operationStage = "";
    private int _operationProgress;
    private string? _operationOutputPath;
    private string? _activeExportJobId;
    private string? _activeSanitizedJobId;
    private string? _activeTokenSummaryJobId;
    private string _sanitizeChatName = "";
    private bool _sanitizeStripImageContent = true;
    private bool _sanitizeStripBlobContent;
    private bool _sanitizeCreateJsonlCopy = true;
    private bool _sanitizeReAddToCurrentDay;
    private string _transcriptSearch = "";
    private bool _transcriptShowToolCalls = true;
    private SessionTranscriptResult? _transcript;
    private TokenUsageSummaryJobStatus? _tokenSummaryStatus;

    public MainWindowViewModel(ClaudeSessionService sessions, IUiService ui, AppSettingsService settings)
    {
        _sessions = sessions;
        _ui = ui;
        _settings = settings;

        RefreshCommand = new AsyncRelayCommand(() => LoadSessionsAsync(_browseMode));
        BrowseFolderCommand = new AsyncRelayCommand(BrowseFolderAsync);
        ShowAllSessionsCommand = new AsyncRelayCommand(() => LoadSessionsAsync("all"));
        OpenRepositoryCommand = new RelayCommand(OpenRepository);
        AnalyzeAnywayCommand = new AsyncRelayCommand(() => LoadSelectedMetricsAsync(true), () => SelectedSession is not null);
        OpenTranscriptCommand = new AsyncRelayCommand(OpenTranscriptAsync, () => SelectedSession is not null);
        CloseTranscriptCommand = new RelayCommand(() => TranscriptDialogVisible = false);
        ToggleTranscriptToolCallsCommand = new RelayCommand(() => ApplyTranscriptFilter(!TranscriptShowToolCalls));
        CopyTranscriptEntryCommand = new AsyncRelayCommand(CopyTranscriptEntryAsync);
        CopyAllTranscriptCommand = new AsyncRelayCommand(CopyAllTranscriptAsync);
        OpenRenameDialogCommand = new RelayCommand(OpenRenameDialog, () => CanRenameSession);
        ConfirmRenameCommand = new AsyncRelayCommand(ConfirmRenameAsync, () => CanRenameSession && !RenameSessionPending && !string.IsNullOrWhiteSpace(RenameTitle));
        OpenExportDialogCommand = new RelayCommand(() => ExportDialogVisible = true, () => SelectedSession is not null);
        ConfirmExportCommand = new AsyncRelayCommand(StartExportAsync, () => SelectedSession is not null);
        CancelExportCommand = new RelayCommand(CancelExport);
        OpenSanitizeDialogCommand = new RelayCommand(() => SanitizeDialogVisible = true, () => SelectedSession is not null);
        ConfirmSanitizeCommand = new AsyncRelayCommand(StartSanitizeAsync, () => SelectedSession is not null);
        CancelSanitizeCommand = new RelayCommand(CancelSanitize);
        OpenTokenSummaryCommand = new AsyncRelayCommand(StartTokenSummaryAsync, () => FilteredSessions.Count > 0);
        CancelTokenSummaryCommand = new RelayCommand(CancelTokenSummary);
        CopySelectedTokenUsageCommand = new AsyncRelayCommand(CopySelectedTokenUsageAsync, () => SelectedMetrics?.TokenUsage is not null && SelectedSession is not null);
        CopyTokenSummaryCommand = new AsyncRelayCommand(CopyTokenSummaryAsync, () => TokenSummaryStatus?.Result is not null);
        RevealOperationOutputCommand = new AsyncRelayCommand(RevealOperationOutputAsync, () => !string.IsNullOrWhiteSpace(OperationOutputPath));
        OpenSelectedFileCommand = new AsyncRelayCommand(() => SelectedSession is null ? Task.CompletedTask : _ui.OpenPathAsync(SelectedSession.File), () => SelectedSession is not null);
        RevealSelectedFileCommand = new AsyncRelayCommand(() => SelectedSession is null ? Task.CompletedTask : _ui.RevealPathAsync(SelectedSession.File), () => SelectedSession is not null);
        DismissDialogsCommand = new RelayCommand(DismissDialogs);

        _ = InitializeAsync();
    }

    public ObservableCollection<SessionCardViewModel> Sessions { get; } = [];
    public ObservableCollection<SessionCardViewModel> FilteredSessions { get; } = [];
    public ObservableCollection<TranscriptEntryViewModel> TranscriptEntries { get; } = [];
    public string[] ExportFormats { get; } = ["markdown", "html"];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand BrowseFolderCommand { get; }
    public AsyncRelayCommand ShowAllSessionsCommand { get; }
    public RelayCommand OpenRepositoryCommand { get; }
    public AsyncRelayCommand AnalyzeAnywayCommand { get; }
    public AsyncRelayCommand OpenTranscriptCommand { get; }
    public RelayCommand CloseTranscriptCommand { get; }
    public RelayCommand ToggleTranscriptToolCallsCommand { get; }
    public AsyncRelayCommand CopyTranscriptEntryCommand { get; }
    public AsyncRelayCommand CopyAllTranscriptCommand { get; }
    public RelayCommand OpenRenameDialogCommand { get; }
    public AsyncRelayCommand ConfirmRenameCommand { get; }
    public RelayCommand OpenExportDialogCommand { get; }
    public AsyncRelayCommand ConfirmExportCommand { get; }
    public RelayCommand CancelExportCommand { get; }
    public RelayCommand OpenSanitizeDialogCommand { get; }
    public AsyncRelayCommand ConfirmSanitizeCommand { get; }
    public RelayCommand CancelSanitizeCommand { get; }
    public AsyncRelayCommand OpenTokenSummaryCommand { get; }
    public RelayCommand CancelTokenSummaryCommand { get; }
    public AsyncRelayCommand CopySelectedTokenUsageCommand { get; }
    public AsyncRelayCommand CopyTokenSummaryCommand { get; }
    public AsyncRelayCommand RevealOperationOutputCommand { get; }
    public AsyncRelayCommand OpenSelectedFileCommand { get; }
    public AsyncRelayCommand RevealSelectedFileCommand { get; }
    public RelayCommand DismissDialogsCommand { get; }

    public string ClaudeHome
    {
        get => _claudeHome;
        set => SetProperty(ref _claudeHome, value);
    }

    public string FolderPath
    {
        get => _folderPath;
        set => SetProperty(ref _folderPath, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilters();
            }
        }
    }

    public string DateFrom
    {
        get => _dateFrom;
        set => SetProperty(ref _dateFrom, value);
    }

    public string DateTo
    {
        get => _dateTo;
        set => SetProperty(ref _dateTo, value);
    }

    public bool CwdOnly
    {
        get => _cwdOnly;
        set => SetProperty(ref _cwdOnly, value);
    }

    public bool IncludeCrossSessionWrites
    {
        get => _includeCrossSessionWrites;
        set => SetProperty(ref _includeCrossSessionWrites, value);
    }

    public bool ShowLiveSessions
    {
        get => _showLiveSessions;
        set
        {
            if (SetProperty(ref _showLiveSessions, value))
            {
                ApplyFilters();
            }
        }
    }

    public bool ShowArchivedSessions
    {
        get => _showArchivedSessions;
        set
        {
            if (SetProperty(ref _showArchivedSessions, value))
            {
                ApplyFilters();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsDetailLoading
    {
        get => _isDetailLoading;
        set => SetProperty(ref _isDetailLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public SessionCardViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                OnPropertyChanged(nameof(HasSelectedSession));
                OnPropertyChanged(nameof(SelectedTitle));
                OnPropertyChanged(nameof(SelectedCwd));
                OnPropertyChanged(nameof(SelectedFile));
                OnPropertyChanged(nameof(SelectedFileSize));
                OnPropertyChanged(nameof(SelectedStartedAt));
                OnPropertyChanged(nameof(SelectedUpdatedAt));
                OnPropertyChanged(nameof(SelectedSource));
                OnPropertyChanged(nameof(CanRenameSession));
                OnPropertyChanged(nameof(RenameSessionDisabledReason));
                RaiseCommandState();
                _ = LoadSelectedMetricsAsync(false);
            }
        }
    }

    public bool HasSelectedSession => SelectedSession is not null;
    public string SelectedTitle => SelectedSession?.Title ?? "No session selected";
    public string SelectedCwd => SelectedSession?.Cwd ?? "";
    public string SelectedFile => SelectedSession?.File ?? "";
    public string SelectedFileSize => SelectedSession?.FileSizeLabel ?? "";
    public string SelectedStartedAt => SelectedSession?.StartedAtLabel ?? "";
    public string SelectedUpdatedAt => SelectedSession?.UpdatedAtLabel ?? "";
    public string SelectedSource => SelectedSession?.SourceLabel ?? "";

    public SessionDetailMetrics? SelectedMetrics
    {
        get => _selectedMetrics;
        set
        {
            if (SetProperty(ref _selectedMetrics, value))
            {
                OnPropertyChanged(nameof(InteractionSummary));
                OnPropertyChanged(nameof(TokenSummaryText));
                OnPropertyChanged(nameof(AnalysisBannerText));
                OnPropertyChanged(nameof(HasAnalysisBanner));
                RaiseCommandState();
            }
        }
    }

    public string InteractionSummary
    {
        get
        {
            if (IsDetailLoading) return "Loading...";
            if (SelectedMetrics is null) return "Unavailable";
            var promptLabel = $"{SelectedMetrics.InteractionCount} {(SelectedMetrics.InteractionCount == 1 ? "prompt" : "prompts")}";
            var toolLabel = $"{SelectedMetrics.ToolCallCount} {(SelectedMetrics.ToolCallCount == 1 ? "tool call" : "tool calls")}";
            return SelectedMetrics.AnalysisKind == "partial" ? $"{promptLabel} / {toolLabel} (partial)" : $"{promptLabel} / {toolLabel}";
        }
    }

    public string TokenSummaryText
        => SelectedMetrics?.TokenUsage is null
            ? "No token usage found"
            : $"{SelectedMetrics.TokenUsage.TotalTokens:n0} total / {SelectedMetrics.TokenUsage.InputTokens:n0} input / {SelectedMetrics.TokenUsage.OutputTokens:n0} output";

    public bool HasAnalysisBanner => !string.IsNullOrWhiteSpace(AnalysisBannerText);
    public string? AnalysisBannerText => SelectedMetrics?.SkipReason;

    public EnvironmentCapabilities? Environment
    {
        get => _environment;
        set
        {
            if (SetProperty(ref _environment, value))
            {
                OnPropertyChanged(nameof(EnvironmentSummary));
                OnPropertyChanged(nameof(EnvironmentNotes));
            }
        }
    }

    public string EnvironmentSummary => Environment?.Summary ?? "Checking environment...";
    public string EnvironmentNotes => Environment is null ? "" : string.Join(" ", Environment.Notes);

    public string SessionCountText => $"{FilteredSessions.Count:n0} / {Sessions.Count:n0}";
    public string LiveCountText => $"{FilteredSessions.Count(s => s.Session.Kind == SessionKind.Live):n0} live";
    public string ArchivedCountText => $"{FilteredSessions.Count(s => s.Session.Kind == SessionKind.Archived):n0} archived";
    public string ScopeText => _result is null ? "Loading" : _result.ScopeMode == ScopeMode.All ? "All sessions" : _result.ScopeMode == ScopeMode.Repo ? "Repo root" : "Folder tree";
    public string ClaudeHomeText => _result?.ClaudeHome ?? "Loading";
    public string TotalSizeText => ClaudeSessionService.FormatByteCount(FilteredSessions.Sum(s => s.FileSizeBytes));

    public bool ExportDialogVisible
    {
        get => _exportDialogVisible;
        set => SetProperty(ref _exportDialogVisible, value);
    }

    public bool ExportProgressVisible
    {
        get => _exportProgressVisible;
        set => SetProperty(ref _exportProgressVisible, value);
    }

    public bool SanitizeDialogVisible
    {
        get => _sanitizeDialogVisible;
        set => SetProperty(ref _sanitizeDialogVisible, value);
    }

    public bool SanitizeProgressVisible
    {
        get => _sanitizeProgressVisible;
        set => SetProperty(ref _sanitizeProgressVisible, value);
    }

    public bool TranscriptDialogVisible
    {
        get => _transcriptDialogVisible;
        set => SetProperty(ref _transcriptDialogVisible, value);
    }

    public bool TokenSummaryDialogVisible
    {
        get => _tokenSummaryDialogVisible;
        set => SetProperty(ref _tokenSummaryDialogVisible, value);
    }

    public bool RenameDialogVisible
    {
        get => _renameDialogVisible;
        set => SetProperty(ref _renameDialogVisible, value);
    }

    public bool RenameSessionPending
    {
        get => _renameSessionPending;
        set
        {
            if (SetProperty(ref _renameSessionPending, value))
            {
                ConfirmRenameCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RenameTitle
    {
        get => _renameTitle;
        set
        {
            if (SetProperty(ref _renameTitle, ClaudeSessionService.SanitizeSessionTitleInput(value)))
            {
                ConfirmRenameCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanRenameSession => false;
    public string RenameSessionDisabledReason => "Claude project-log sessions cannot be renamed from clodlogs.";

    public string ExportFormat
    {
        get => _exportFormat;
        set => SetProperty(ref _exportFormat, value);
    }

    public bool ExportImages
    {
        get => _exportImages;
        set => SetProperty(ref _exportImages, value);
    }

    public bool ExportInlineImages
    {
        get => _exportInlineImages;
        set => SetProperty(ref _exportInlineImages, value);
    }

    public bool ExportToolCallResults
    {
        get => _exportToolCallResults;
        set => SetProperty(ref _exportToolCallResults, value);
    }

    public string OperationTitle
    {
        get => _operationTitle;
        set => SetProperty(ref _operationTitle, value);
    }

    public string OperationMessage
    {
        get => _operationMessage;
        set => SetProperty(ref _operationMessage, value);
    }

    public string OperationStage
    {
        get => _operationStage;
        set => SetProperty(ref _operationStage, value);
    }

    public int OperationProgress
    {
        get => _operationProgress;
        set => SetProperty(ref _operationProgress, value);
    }

    public string? OperationOutputPath
    {
        get => _operationOutputPath;
        set
        {
            if (SetProperty(ref _operationOutputPath, value))
            {
                OnPropertyChanged(nameof(HasOperationOutput));
                RevealOperationOutputCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasOperationOutput => !string.IsNullOrWhiteSpace(OperationOutputPath);

    public string SanitizeChatName
    {
        get => _sanitizeChatName;
        set => SetProperty(ref _sanitizeChatName, ClaudeSessionService.SanitizeSessionTitleInput(value));
    }

    public bool SanitizeStripImageContent
    {
        get => _sanitizeStripImageContent;
        set => SetProperty(ref _sanitizeStripImageContent, value);
    }

    public bool SanitizeStripBlobContent
    {
        get => _sanitizeStripBlobContent;
        set => SetProperty(ref _sanitizeStripBlobContent, value);
    }

    public bool SanitizeCreateJsonlCopy
    {
        get => _sanitizeCreateJsonlCopy;
        set => SetProperty(ref _sanitizeCreateJsonlCopy, value);
    }

    public bool SanitizeReAddToCurrentDay
    {
        get => _sanitizeReAddToCurrentDay;
        set => SetProperty(ref _sanitizeReAddToCurrentDay, value);
    }

    public string TranscriptSearch
    {
        get => _transcriptSearch;
        set
        {
            if (SetProperty(ref _transcriptSearch, value))
            {
                ApplyTranscriptFilter(TranscriptShowToolCalls);
            }
        }
    }

    public bool TranscriptShowToolCalls
    {
        get => _transcriptShowToolCalls;
        set
        {
            if (SetProperty(ref _transcriptShowToolCalls, value))
            {
                ApplyTranscriptFilter(value);
            }
        }
    }

    public string TranscriptMetaText
        => _transcript is null
            ? "No transcript loaded"
            : $"{TranscriptEntries.Count:n0} / {_transcript.Entries.Count:n0} entries"
              + (_transcript.Truncated ? $" / truncated at {SessionBrowserMaxEntries:n0}" : "")
              + (_transcript.OmittedBootstrapMessages > 0 ? $" / {_transcript.OmittedBootstrapMessages:n0} bootstrap hidden" : "")
              + (_transcript.OversizedLineCount > 0 ? $" / {_transcript.OversizedLineCount:n0} oversized skipped" : "");

    public TokenUsageSummaryJobStatus? TokenSummaryStatus
    {
        get => _tokenSummaryStatus;
        set
        {
            if (SetProperty(ref _tokenSummaryStatus, value))
            {
                OnPropertyChanged(nameof(TokenSummaryTitle));
                OnPropertyChanged(nameof(TokenSummaryMessage));
                OnPropertyChanged(nameof(TokenSummaryProgress));
                OnPropertyChanged(nameof(TokenSummaryResultText));
                CopyTokenSummaryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TokenSummaryTitle => TokenSummaryStatus?.Kind switch
    {
        "success" => "Token summary complete",
        "error" => "Token summary failed",
        "cancelled" => "Token summary cancelled",
        "working" => "Summarizing tokens...",
        _ => "Token summary"
    };

    public string TokenSummaryMessage => TokenSummaryStatus?.Message ?? "";
    public int TokenSummaryProgress => TokenSummaryStatus?.ProgressPercent ?? 0;
    public string TokenSummaryResultText
    {
        get
        {
            var result = TokenSummaryStatus?.Result;
            if (result is null)
            {
                return "";
            }

            return $"{result.SessionCount:n0} sessions, {result.SessionsWithTokenUsage:n0} with token data, {result.FailedSessionCount:n0} failed, {result.TokenUsage.TotalTokens:n0} total tokens";
        }
    }

    private async Task InitializeAsync()
    {
        var appSettings = await _settings.ReadAsync();
        FolderPath = appSettings.LastOpenedFolder ?? System.Environment.CurrentDirectory;
        await RefreshEnvironmentAsync();
        await LoadSessionsAsync("folder");
    }

    private async Task RefreshEnvironmentAsync()
    {
        try
        {
            Environment = await _sessions.GetEnvironmentCapabilitiesAsync(string.IsNullOrWhiteSpace(ClaudeHome) ? null : ClaudeHome);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task BrowseFolderAsync()
    {
        var selected = await _ui.PickDirectoryAsync(string.IsNullOrWhiteSpace(FolderPath) ? null : FolderPath);
        if (selected is null)
        {
            return;
        }

        FolderPath = selected;
        await LoadSessionsAsync("folder");
    }

    private async Task LoadSessionsAsync(string browseMode)
    {
        IsLoading = true;
        ErrorMessage = null;
        _browseMode = browseMode;
        try
        {
            var target = browseMode == "all" ? null : FolderPath;
            _result = await _sessions.FindClaudeSessionsAsync(
                string.IsNullOrWhiteSpace(ClaudeHome) ? null : ClaudeHome,
                target,
                CwdOnly,
                string.IsNullOrWhiteSpace(DateFrom) ? null : DateFrom,
                string.IsNullOrWhiteSpace(DateTo) ? null : DateTo,
                IncludeCrossSessionWrites);
            Sessions.Clear();
            foreach (var session in _result.Sessions)
            {
                Sessions.Add(new SessionCardViewModel(session));
            }

            ApplyFilters();
            SelectedSession = FilteredSessions.FirstOrDefault();
            StatusMessage = $"Loaded {Sessions.Count:n0} session{(Sessions.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Session loading failed.";
        }
        finally
        {
            IsLoading = false;
            RefreshHeaderProperties();
        }
    }

    private void ApplyFilters()
    {
        var query = SearchQuery.Trim().ToLowerInvariant();
        var filtered = Sessions.Where(session =>
        {
            if (session.Session.Kind == SessionKind.Live && !ShowLiveSessions) return false;
            if (session.Session.Kind == SessionKind.Archived && !ShowArchivedSessions) return false;
            if (query.Length == 0) return true;
            return session.Title.ToLowerInvariant().Contains(query)
                || session.Cwd.ToLowerInvariant().Contains(query)
                || session.KindLabel.ToLowerInvariant().Contains(query);
        }).ToList();

        FilteredSessions.Clear();
        foreach (var session in filtered)
        {
            FilteredSessions.Add(session);
        }

        if (SelectedSession is null || !FilteredSessions.Contains(SelectedSession))
        {
            SelectedSession = FilteredSessions.FirstOrDefault();
        }

        RefreshHeaderProperties();
    }

    private async Task LoadSelectedMetricsAsync(bool forceDeepAnalysis)
    {
        if (SelectedSession is null)
        {
            SelectedMetrics = null;
            return;
        }

        IsDetailLoading = true;
        SelectedMetrics = null;
        try
        {
            SelectedMetrics = await _sessions.GetSessionDetailMetricsAsync(SelectedSession.File, forceDeepAnalysis);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsDetailLoading = false;
            OnPropertyChanged(nameof(InteractionSummary));
        }
    }

    private async Task OpenTranscriptAsync()
    {
        if (SelectedSession is null)
        {
            return;
        }

        TranscriptDialogVisible = true;
        TranscriptEntries.Clear();
        TranscriptSearch = "";
        StatusMessage = "Reading session transcript...";
        try
        {
            _transcript = await _sessions.ReadSessionTranscriptAsync(SelectedSession.File, SessionBrowserMaxEntries);
            ApplyTranscriptFilter(TranscriptShowToolCalls);
            StatusMessage = "Transcript loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _ui.ShowMessageAsync("Transcript unavailable", ex.Message);
        }
    }

    private void ApplyTranscriptFilter(bool showToolCalls)
    {
        TranscriptEntries.Clear();
        if (_transcript is null)
        {
            OnPropertyChanged(nameof(TranscriptMetaText));
            return;
        }

        var query = TranscriptSearch.Trim().ToLowerInvariant();
        var entries = _transcript.Entries.Where(entry =>
        {
            var isTool = entry.Kind is SessionTranscriptEntryKind.ToolCall or SessionTranscriptEntryKind.ToolOutput or SessionTranscriptEntryKind.CustomToolCall or SessionTranscriptEntryKind.CustomToolOutput;
            if (!showToolCalls && isTool) return false;
            if (query.Length == 0) return true;
            return entry.Title.ToLowerInvariant().Contains(query)
                || entry.Text.ToLowerInvariant().Contains(query)
                || (entry.Role?.ToLowerInvariant().Contains(query) ?? false);
        });

        foreach (var entry in entries)
        {
            TranscriptEntries.Add(new TranscriptEntryViewModel(entry));
        }

        OnPropertyChanged(nameof(TranscriptMetaText));
    }

    private async Task CopyTranscriptEntryAsync(object? parameter)
    {
        if (parameter is not TranscriptEntryViewModel entry)
        {
            return;
        }

        await _ui.CopyTextAsync($"{entry.Title}\n\n{entry.Text}");
        StatusMessage = "Transcript entry copied.";
    }

    private async Task CopyAllTranscriptAsync()
    {
        if (_transcript is null)
        {
            return;
        }

        var payload = string.Join("\n\n---\n\n", _transcript.Entries.Select(entry =>
        {
            var timestamp = DateTimeOffset.TryParse(entry.Timestamp, out var parsed)
                ? parsed.ToLocalTime().ToString("g")
                : entry.Timestamp ?? "unknown time";
            return $"## {entry.Title}\n{timestamp}\n\n{entry.Text}";
        }));
        await _ui.CopyTextAsync(payload);
        StatusMessage = "Transcript copied.";
    }

    private void OpenRenameDialog()
    {
        if (!CanRenameSession || SelectedSession is null)
        {
            StatusMessage = RenameSessionDisabledReason;
            return;
        }

        RenameTitle = SelectedSession.Title;
        RenameDialogVisible = true;
    }

    private async Task ConfirmRenameAsync()
    {
        if (!CanRenameSession || SelectedSession is null)
        {
            StatusMessage = RenameSessionDisabledReason;
            return;
        }

        RenameSessionPending = true;
        try
        {
            await _sessions.RenameSessionThreadNameAsync(string.IsNullOrWhiteSpace(ClaudeHome) ? null : ClaudeHome, SelectedSession.Id, RenameTitle);
            RenameDialogVisible = false;
            await LoadSessionsAsync(_browseMode);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _ui.ShowMessageAsync("Rename unavailable", ex.Message);
        }
        finally
        {
            RenameSessionPending = false;
        }
    }

    private async Task StartExportAsync()
    {
        if (SelectedSession is null)
        {
            return;
        }

        string? outputDirectory = null;
        string? outputPath = null;
        if (ExportFormat == "markdown" || (ExportFormat == "html" && ExportImages && !ExportInlineImages))
        {
            outputDirectory = await _ui.PickExportDirectoryAsync(SelectedSession.File);
            if (outputDirectory is null) return;
        }
        else
        {
            outputPath = await _ui.PickHtmlExportDestinationAsync(SelectedSession.File, ExportImages, ExportInlineImages);
            if (outputPath is null) return;
        }

        ExportDialogVisible = false;
        ExportProgressVisible = true;
        OperationTitle = "Exporting...";
        OperationMessage = "Preparing export...";
        OperationProgress = 2;
        OperationOutputPath = null;
        _activeExportJobId = _sessions.StartExportJob(ExportFormat, SelectedSession.File, ExportImages, ExportInlineImages, ExportToolCallResults, outputDirectory, outputPath).JobId;
        await PollExportJobAsync(_activeExportJobId);
    }

    private async Task PollExportJobAsync(string jobId)
    {
        while (true)
        {
            var status = _sessions.GetExportJobStatus(jobId);
            ApplyOperationStatus(status);
            if (status.Kind != "working")
            {
                break;
            }

            await Task.Delay(250);
        }
    }

    private void CancelExport()
    {
        if (_activeExportJobId is not null)
        {
            _sessions.CancelExportJob(_activeExportJobId);
        }
    }

    private async Task StartSanitizeAsync()
    {
        if (SelectedSession is null)
        {
            return;
        }

        SanitizeDialogVisible = false;
        SanitizeProgressVisible = true;
        OperationTitle = "Creating sanitized output...";
        OperationMessage = "Preparing sanitized session output...";
        OperationProgress = 2;
        OperationOutputPath = null;
        _activeSanitizedJobId = _sessions.StartSanitizedCopyJob(
            SelectedSession.File,
            string.IsNullOrWhiteSpace(ClaudeHome) ? null : ClaudeHome,
            SanitizeChatName,
            SanitizeStripImageContent,
            SanitizeStripBlobContent,
            SanitizeCreateJsonlCopy,
            SanitizeReAddToCurrentDay).JobId;
        await PollSanitizedJobAsync(_activeSanitizedJobId);
    }

    private async Task PollSanitizedJobAsync(string jobId)
    {
        while (true)
        {
            var status = _sessions.GetSanitizedCopyJobStatus(jobId);
            ApplyOperationStatus(status);
            if (status.Kind != "working")
            {
                break;
            }

            await Task.Delay(250);
        }
    }

    private void CancelSanitize()
    {
        if (_activeSanitizedJobId is not null)
        {
            _sessions.CancelSanitizedCopyJob(_activeSanitizedJobId);
        }
    }

    private async Task StartTokenSummaryAsync()
    {
        TokenSummaryDialogVisible = true;
        TokenSummaryStatus = new TokenUsageSummaryJobStatus("working", 1, "starting", "Preparing token summary...", 0, FilteredSessions.Count, null, null);
        _activeTokenSummaryJobId = _sessions.StartTokenUsageSummaryJob(FilteredSessions.Select(s => s.File).ToArray()).JobId;
        while (true)
        {
            var status = _sessions.GetTokenUsageSummaryJobStatus(_activeTokenSummaryJobId);
            TokenSummaryStatus = status;
            if (status.Kind != "working")
            {
                break;
            }

            await Task.Delay(250);
        }
    }

    private void CancelTokenSummary()
    {
        if (_activeTokenSummaryJobId is not null)
        {
            _sessions.CancelTokenUsageSummaryJob(_activeTokenSummaryJobId);
        }
    }

    private async Task CopySelectedTokenUsageAsync()
    {
        if (SelectedSession is null || SelectedMetrics?.TokenUsage is null)
        {
            return;
        }

        await _ui.CopyTextAsync(_sessions.FormatTokenUsageForClipboard(SelectedSession.Title, SelectedMetrics.TokenUsage));
        StatusMessage = "Token usage copied.";
    }

    private async Task CopyTokenSummaryAsync()
    {
        if (TokenSummaryStatus?.Result is null)
        {
            return;
        }

        await _ui.CopyTextAsync(_sessions.FormatTokenUsageSummaryForClipboard(TokenSummaryStatus.Result));
        StatusMessage = "Token summary copied.";
    }

    private async Task RevealOperationOutputAsync()
    {
        if (!string.IsNullOrWhiteSpace(OperationOutputPath))
        {
            await _ui.RevealPathAsync(OperationOutputPath);
        }
    }

    private void ApplyOperationStatus(ExportJobStatus status)
    {
        OperationTitle = status.Kind switch
        {
            "success" => "Complete",
            "error" => "Failed",
            "cancelled" => "Cancelled",
            _ => "Working..."
        };
        OperationMessage = status.Message;
        OperationStage = status.Stage;
        OperationProgress = status.ProgressPercent;
        OperationOutputPath = status.OutputPath;
    }

    private void DismissDialogs()
    {
        ExportDialogVisible = false;
        ExportProgressVisible = false;
        SanitizeDialogVisible = false;
        SanitizeProgressVisible = false;
        TokenSummaryDialogVisible = false;
        RenameDialogVisible = false;
    }

    private void RefreshHeaderProperties()
    {
        OnPropertyChanged(nameof(SessionCountText));
        OnPropertyChanged(nameof(LiveCountText));
        OnPropertyChanged(nameof(ArchivedCountText));
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(ClaudeHomeText));
        OnPropertyChanged(nameof(TotalSizeText));
        OpenTokenSummaryCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandState()
    {
        AnalyzeAnywayCommand.RaiseCanExecuteChanged();
        OpenTranscriptCommand.RaiseCanExecuteChanged();
        OpenRenameDialogCommand.RaiseCanExecuteChanged();
        ConfirmRenameCommand.RaiseCanExecuteChanged();
        OpenExportDialogCommand.RaiseCanExecuteChanged();
        OpenSanitizeDialogCommand.RaiseCanExecuteChanged();
        ConfirmExportCommand.RaiseCanExecuteChanged();
        ConfirmSanitizeCommand.RaiseCanExecuteChanged();
        CopySelectedTokenUsageCommand.RaiseCanExecuteChanged();
        OpenSelectedFileCommand.RaiseCanExecuteChanged();
        RevealSelectedFileCommand.RaiseCanExecuteChanged();
    }

    private static void OpenRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/tobitege/clodlogs",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}

public sealed class TranscriptEntryViewModel(SessionTranscriptEntry entry)
{
    public SessionTranscriptEntry Entry { get; } = entry;
    public int Index => Entry.Index + 1;
    public string Title => Entry.Title;
    public string Text => Entry.Text;
    public string TimestampLabel => DateTimeOffset.TryParse(Entry.Timestamp, out var parsed) ? parsed.ToLocalTime().ToString("g") : Entry.Timestamp ?? "unknown time";
    public string KindClass => Entry.Kind.ToString();
}
