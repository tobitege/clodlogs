using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
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
    private bool _startupLoadingVisible = true;
    private string _startupLoadingMessage = "Loading saved settings...";
    private bool _startupScanActive;
    private string _partialSessionResultsMessage = "";
    private CancellationTokenSource? _startupLoadCancellation;
    private int _startupScannedFileCount;
    private int _startupTotalFileCount;
    private int _startupReportedScannedFileCount;
    private bool _startupScanResultFinalized;
    private readonly DispatcherTimer _startupProgressTimer;
    private string? _errorMessage;
    private string _statusMessage = "Ready";
    private string _browseMode = "folder";
    private bool _exportDialogVisible;
    private bool _batchExportDialogVisible;
    private bool _exportProgressVisible;
    private bool _sanitizeDialogVisible;
    private bool _sanitizeProgressVisible;
    private bool _transcriptDialogVisible;
    private bool _tokenSummaryDialogVisible;
    private bool _optionsDialogVisible;
    private string _exportFormat = "markdown";
    private bool _exportImages;
    private bool _exportInlineImages = true;
    private bool _exportToolCallResults;
    private string _batchExportDirectory = "";
    private bool _updatingBatchSelection;
    private string _operationTitle = "Ready";
    private string _operationMessage = "";
    private string _operationStage = "";
    private int _operationProgress;
    private string? _operationOutputPath;
    private string? _activeOperationKind;
    private string? _activeOperationJobId;
    private string? _activeTokenSummaryJobId;
    private int _metricsRequestVersion;
    private string _sanitizeChatName = "";
    private bool _sanitizeStripImageContent = true;
    private bool _sanitizeStripBlobContent;
    private bool _sanitizeCreateJsonlCopy = true;
    private bool _sanitizeReAddToCurrentDay;
    private string _transcriptSearch = "";
    private bool _transcriptShowToolCalls = true;
    private SessionTranscriptResult? _transcript;
    private TokenUsageSummaryJobStatus? _tokenSummaryStatus;
    private AnthropicPricing _anthropicPricing = AnthropicPricingService.DefaultPricing();
    private bool _isPricingRefreshing;
    private string _pricingStatus = "Bundled Anthropic prices are active.";

    public MainWindowViewModel(ClaudeSessionService sessions, IUiService ui, AppSettingsService settings)
    {
        _sessions = sessions;
        _ui = ui;
        _settings = settings;
        _startupProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _startupProgressTimer.Tick += (_, _) => AdvanceStartupProgressDisplay();

        RefreshCommand = new AsyncRelayCommand(() => LoadSessionsAsync(_browseMode), () => !StartupScanActive && !IsLoading);
        BrowseFolderCommand = new AsyncRelayCommand(BrowseFolderAsync, () => !StartupScanActive && !IsLoading);
        ShowAllSessionsCommand = new AsyncRelayCommand(() => LoadSessionsAsync("all"), () => !StartupScanActive && !IsLoading);
        OpenRepositoryCommand = new RelayCommand(OpenRepository);
        AnalyzeAnywayCommand = new AsyncRelayCommand(() => LoadSelectedMetricsAsync(true), () => SelectedSession is not null);
        OpenTranscriptCommand = new AsyncRelayCommand(OpenTranscriptAsync, () => SelectedSession is not null);
        CloseTranscriptCommand = new RelayCommand(() => TranscriptDialogVisible = false);
        ToggleTranscriptToolCallsCommand = new RelayCommand(() => ApplyTranscriptFilter(!TranscriptShowToolCalls));
        CopyTranscriptEntryCommand = new AsyncRelayCommand(CopyTranscriptEntryAsync);
        CopyAllTranscriptCommand = new AsyncRelayCommand(CopyAllTranscriptAsync);
        OpenExportDialogCommand = new RelayCommand(() => ExportDialogVisible = true, () => SelectedSession is not null && !IsOperationRunning);
        ConfirmExportCommand = new AsyncRelayCommand(StartExportAsync, () => SelectedSession is not null && !IsOperationRunning);
        OpenBatchExportDialogCommand = new AsyncRelayCommand(OpenBatchExportDialogAsync, () => FilteredSessions.Count > 0 && !IsOperationRunning);
        SelectAllBatchExportCommand = new RelayCommand(() => SetAllBatchExportSelections(true));
        ClearBatchExportCommand = new RelayCommand(() => SetAllBatchExportSelections(false));
        BrowseBatchExportDirectoryCommand = new AsyncRelayCommand(BrowseBatchExportDirectoryAsync);
        ConfirmBatchExportCommand = new AsyncRelayCommand(StartBatchExportAsync, () => CanStartBatchExport);
        CancelOperationCommand = new RelayCommand(CancelOperation, () => IsOperationRunning);
        OpenSanitizeDialogCommand = new RelayCommand(() => SanitizeDialogVisible = true, () => SelectedSession is not null && !IsOperationRunning);
        ConfirmSanitizeCommand = new AsyncRelayCommand(StartSanitizeAsync, () => SelectedSession is not null && !IsOperationRunning);
        OpenTokenSummaryCommand = new AsyncRelayCommand(StartTokenSummaryAsync, () => FilteredSessions.Count > 0);
        CancelTokenSummaryCommand = new RelayCommand(CancelTokenSummary);
        CopySelectedTokenUsageCommand = new AsyncRelayCommand(CopySelectedTokenUsageAsync, () => SelectedMetrics?.TokenUsage is not null && SelectedSession is not null);
        CopyTokenSummaryCommand = new AsyncRelayCommand(CopyTokenSummaryAsync, () => TokenSummaryStatus?.Result is not null);
        ExportTokenSummaryCommand = new AsyncRelayCommand(ExportTokenSummaryAsync, _ => TokenSummaryStatus?.Result is not null);
        OpenOptionsCommand = new RelayCommand(() => OptionsDialogVisible = true);
        RefreshPricingCommand = new AsyncRelayCommand(RefreshPricingAsync);
        RevealOperationOutputCommand = new AsyncRelayCommand(RevealOperationOutputAsync, () => !string.IsNullOrWhiteSpace(OperationOutputPath));
        OpenSelectedFileCommand = new AsyncRelayCommand(() => SelectedSession is null ? Task.CompletedTask : _ui.OpenPathAsync(SelectedSession.File), () => SelectedSession is not null);
        RevealSelectedFileCommand = new AsyncRelayCommand(() => SelectedSession is null ? Task.CompletedTask : _ui.RevealPathAsync(SelectedSession.File), () => SelectedSession is not null);
        StopStartupScanCommand = new RelayCommand(StopStartupScan, () => StartupScanActive);
        DismissDialogsCommand = new RelayCommand(DismissDialogs);

        _ = InitializeAsync();
    }

    public ObservableCollection<SessionCardViewModel> Sessions { get; } = [];
    public ObservableCollection<SessionCardViewModel> FilteredSessions { get; } = [];
    public ObservableCollection<BatchExportSessionViewModel> BatchExportSessions { get; } = [];
    public ObservableCollection<TranscriptEntryViewModel> TranscriptEntries { get; } = [];
    public ObservableCollection<TokenUsageDailyBarViewModel> TokenSummaryDailyBars { get; } = [];
    public ObservableCollection<TokenUsageModelRowViewModel> TokenSummaryModelRows { get; } = [];
    public ObservableCollection<AnthropicPricingRowViewModel> AnthropicPricingRows { get; } = [];
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
    public RelayCommand OpenExportDialogCommand { get; }
    public AsyncRelayCommand ConfirmExportCommand { get; }
    public AsyncRelayCommand OpenBatchExportDialogCommand { get; }
    public RelayCommand SelectAllBatchExportCommand { get; }
    public RelayCommand ClearBatchExportCommand { get; }
    public AsyncRelayCommand BrowseBatchExportDirectoryCommand { get; }
    public AsyncRelayCommand ConfirmBatchExportCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public RelayCommand OpenSanitizeDialogCommand { get; }
    public AsyncRelayCommand ConfirmSanitizeCommand { get; }
    public AsyncRelayCommand OpenTokenSummaryCommand { get; }
    public RelayCommand CancelTokenSummaryCommand { get; }
    public AsyncRelayCommand CopySelectedTokenUsageCommand { get; }
    public AsyncRelayCommand CopyTokenSummaryCommand { get; }
    public AsyncRelayCommand ExportTokenSummaryCommand { get; }
    public RelayCommand OpenOptionsCommand { get; }
    public AsyncRelayCommand RefreshPricingCommand { get; }
    public AsyncRelayCommand RevealOperationOutputCommand { get; }
    public AsyncRelayCommand OpenSelectedFileCommand { get; }
    public AsyncRelayCommand RevealSelectedFileCommand { get; }
    public RelayCommand StopStartupScanCommand { get; }
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
        set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                BrowseFolderCommand.RaiseCanExecuteChanged();
                ShowAllSessionsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDetailLoading
    {
        get => _isDetailLoading;
        set => SetProperty(ref _isDetailLoading, value);
    }

    public bool StartupLoadingVisible
    {
        get => _startupLoadingVisible;
        private set => SetProperty(ref _startupLoadingVisible, value);
    }

    public string StartupLoadingMessage
    {
        get => _startupLoadingMessage;
        private set => SetProperty(ref _startupLoadingMessage, value);
    }

    public bool StartupScanActive
    {
        get => _startupScanActive;
        private set
        {
            if (SetProperty(ref _startupScanActive, value))
            {
                StopStartupScanCommand.RaiseCanExecuteChanged();
                RefreshCommand.RaiseCanExecuteChanged();
                BrowseFolderCommand.RaiseCanExecuteChanged();
                ShowAllSessionsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasPartialSessionResults => !string.IsNullOrWhiteSpace(PartialSessionResultsMessage);

    public string PartialSessionResultsMessage
    {
        get => _partialSessionResultsMessage;
        private set
        {
            if (SetProperty(ref _partialSessionResultsMessage, value))
            {
                OnPropertyChanged(nameof(HasPartialSessionResults));
            }
        }
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
            : $"{SelectedMetrics.TokenUsage.TotalTokens:n0} total / {SelectedMetrics.TokenUsage.InputTokens:n0} input / {SelectedMetrics.TokenUsage.OutputTokens:n0} output / {SelectedMetrics.TokenUsage.CacheCreationInputTokens:n0} cache write / {SelectedMetrics.TokenUsage.CacheReadInputTokens:n0} cache read";

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

    public bool BatchExportDialogVisible
    {
        get => _batchExportDialogVisible;
        set => SetProperty(ref _batchExportDialogVisible, value);
    }

    public bool ExportProgressVisible
    {
        get => _exportProgressVisible;
        set => SetProperty(ref _exportProgressVisible, value);
    }

    public bool IsOperationRunning => _activeOperationKind is not null;
    public bool CanCloseOperationProgress => !IsOperationRunning;

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

    public bool OptionsDialogVisible
    {
        get => _optionsDialogVisible;
        set => SetProperty(ref _optionsDialogVisible, value);
    }

    public bool IsPricingRefreshing
    {
        get => _isPricingRefreshing;
        private set
        {
            if (SetProperty(ref _isPricingRefreshing, value))
            {
                OnPropertyChanged(nameof(PricingRefreshButtonText));
            }
        }
    }

    public string PricingStatus
    {
        get => _pricingStatus;
        private set => SetProperty(ref _pricingStatus, value);
    }

    public string PricingRefreshButtonText => IsPricingRefreshing ? "Refreshing..." : "Refresh";
    public string PricingSourceUrl => _anthropicPricing.SourceUrl;


    public string ExportFormat
    {
        get => _exportFormat;
        set
        {
            if (SetProperty(ref _exportFormat, value))
            {
                foreach (var session in BatchExportSessions)
                {
                    session.RefreshOutputFileName(value);
                }
            }
        }
    }

    public string BatchExportDirectory
    {
        get => _batchExportDirectory;
        set
        {
            if (SetProperty(ref _batchExportDirectory, value))
            {
                OnPropertyChanged(nameof(CanStartBatchExport));
                ConfirmBatchExportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int BatchExportSelectedCount => BatchExportSessions.Count(session => session.IsSelected);
    public string BatchExportSelectionText => $"{BatchExportSelectedCount:n0} of {BatchExportSessions.Count:n0} sessions selected";
    public bool CanStartBatchExport => !IsOperationRunning && BatchExportSelectedCount > 0 && IsValidDirectoryPath(BatchExportDirectory);

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
                OnPropertyChanged(nameof(IsTokenSummaryRunning));
                OnPropertyChanged(nameof(CanCloseTokenSummary));
                OnPropertyChanged(nameof(HasTokenSummaryResult));
                OnPropertyChanged(nameof(TokenSummaryPeriodTitle));
                OnPropertyChanged(nameof(TokenSummaryTotalCostText));
                OnPropertyChanged(nameof(TokenSummaryInputText));
                OnPropertyChanged(nameof(TokenSummaryOutputText));
                OnPropertyChanged(nameof(TokenSummaryCacheWriteText));
                OnPropertyChanged(nameof(TokenSummaryCacheReadText));
                OnPropertyChanged(nameof(TokenSummaryGrandTotalText));
                OnPropertyChanged(nameof(TokenSummaryPricingNote));
                RefreshTokenSummaryBreakdowns(value?.Result);
                CopyTokenSummaryCommand.RaiseCanExecuteChanged();
                ExportTokenSummaryCommand.RaiseCanExecuteChanged();
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
    public bool IsTokenSummaryRunning => TokenSummaryStatus?.Kind == "working";
    public bool CanCloseTokenSummary => !IsTokenSummaryRunning;
    public bool HasTokenSummaryResult => TokenSummaryStatus?.Result is not null;
    public string TokenSummaryPeriodTitle
    {
        get
        {
            var days = TokenSummaryStatus?.Result?.DailyBreakdown;
            if (days is null || days.Count == 0)
            {
                return "Token usage";
            }

            var first = days[0].Date;
            var last = days[^1].Date;
            if (first.Year == last.Year && first.Month == last.Month)
            {
                return first.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            }

            return $"{first.ToString("MMM yyyy", CultureInfo.CurrentCulture)} - {last.ToString("MMM yyyy", CultureInfo.CurrentCulture)}";
        }
    }

    public string TokenSummaryTotalCostText => FormatUsd(TokenSummaryStatus?.Result?.CostBreakdown?.TotalCost ?? 0m);
    public string TokenSummaryInputText => FormatCompactTokens(TokenSummaryStatus?.Result?.TokenUsage.InputTokens ?? 0);
    public string TokenSummaryOutputText => FormatCompactTokens(TokenSummaryStatus?.Result?.TokenUsage.OutputTokens ?? 0);
    public string TokenSummaryCacheWriteText => FormatCompactTokens(TokenSummaryStatus?.Result?.TokenUsage.CacheCreationInputTokens ?? 0);
    public string TokenSummaryCacheReadText => FormatCompactTokens(TokenSummaryStatus?.Result?.TokenUsage.CacheReadInputTokens ?? 0);
    public string TokenSummaryGrandTotalText => FormatCompactTokens(TokenSummaryStatus?.Result?.TokenUsage.TotalTokens ?? 0);
    public string TokenSummaryPricingNote
    {
        get
        {
            var costs = TokenSummaryStatus?.Result?.CostBreakdown;
            if (costs is null)
            {
                return "";
            }

            return costs.UnpricedRows == 0
                ? $"Estimated from {costs.PricedRows:n0} priced usage rows."
                : $"Estimated from {costs.PricedRows:n0} priced usage rows; {costs.UnpricedRows:n0} rows use an unknown model.";
        }
    }
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
        try
        {
            StartupLoadingMessage = "Loading saved settings...";
            var appSettings = await _settings.ReadAsync();
            FolderPath = appSettings.LastOpenedFolder ?? System.Environment.CurrentDirectory;
            _anthropicPricing = appSettings.AnthropicPricing ?? AnthropicPricingService.DefaultPricing();
            RefreshAnthropicPricingRows();
            PricingStatus = FormatPricingStatus(_anthropicPricing);
            OnPropertyChanged(nameof(PricingSourceUrl));
            StartupLoadingMessage = "Checking the Claude environment...";
            await RefreshEnvironmentAsync();
            StartupLoadingMessage = "Scanning the session folder. Large histories can take a while...";
            _startupLoadCancellation = new CancellationTokenSource();
            _startupScanResultFinalized = false;
            _startupScannedFileCount = 0;
            _startupReportedScannedFileCount = 0;
            _startupTotalFileCount = 0;
            StartupScanActive = true;
            _startupProgressTimer.Start();
            var progress = new Progress<SessionScanProgress>(ApplyStartupScanProgress);
            await LoadSessionsAsync("folder", _startupLoadCancellation.Token, progress);
            if (StartupLoadingVisible && _result?.IsComplete == true)
            {
                while (_startupScannedFileCount < _startupReportedScannedFileCount)
                {
                    await Task.Delay(50);
                }
                StartupLoadingMessage = $"Scan complete: {_result.ScannedFileCount:n0} of {_result.TotalFileCount:n0} session files. Opening the session list...";
                await Task.Delay(150);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Startup failed.";
        }
        finally
        {
            _startupProgressTimer.Stop();
            StartupScanActive = false;
            _startupLoadCancellation?.Dispose();
            _startupLoadCancellation = null;
            StartupLoadingVisible = false;
        }
    }

    private async Task RefreshEnvironmentAsync()
    {
        try
        {
            var claudeHome = string.IsNullOrWhiteSpace(ClaudeHome) ? null : ClaudeHome;
            Environment = await Task.Run(() => _sessions.GetEnvironmentCapabilitiesAsync(claudeHome));
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

    private async Task LoadSessionsAsync(
        string browseMode,
        CancellationToken cancellationToken = default,
        IProgress<SessionScanProgress>? progress = null)
    {
        IsLoading = true;
        ErrorMessage = null;
        _browseMode = browseMode;
        if (progress is not null)
        {
            Sessions.Clear();
            FilteredSessions.Clear();
            SelectedSession = null;
            RefreshHeaderProperties();
        }
        try
        {
            var target = browseMode == "all" ? null : FolderPath;
            var claudeHome = string.IsNullOrWhiteSpace(ClaudeHome) ? null : ClaudeHome;
            var cwdOnly = CwdOnly;
            var dateFrom = string.IsNullOrWhiteSpace(DateFrom) ? null : DateFrom;
            var dateTo = string.IsNullOrWhiteSpace(DateTo) ? null : DateTo;
            var includeCrossSessionWrites = IncludeCrossSessionWrites;
            _result = await Task.Run(() => _sessions.FindClaudeSessionsAsync(
                claudeHome,
                target,
                cwdOnly,
                dateFrom,
                dateTo,
                includeCrossSessionWrites,
                progress: progress,
                cancellationToken: cancellationToken));
            if (progress is not null)
            {
                _startupScanResultFinalized = true;
                _startupReportedScannedFileCount = _result.ScannedFileCount;
                _startupTotalFileCount = _result.TotalFileCount;
            }
            Sessions.Clear();
            foreach (var session in _result.Sessions)
            {
                Sessions.Add(new SessionCardViewModel(session));
            }

            ApplyFilters();
            SelectedSession = FilteredSessions.FirstOrDefault();
            PartialSessionResultsMessage = _result.IsComplete
                ? ""
                : BuildPartialSessionResultsMessage(_result.ScannedFileCount, _result.TotalFileCount, Sessions.Count);
            StatusMessage = _result.IsComplete
                ? $"Loaded {Sessions.Count:n0} session{(Sessions.Count == 1 ? "" : "s")} after scanning {_result.ScannedFileCount:n0} session file{(_result.ScannedFileCount == 1 ? "" : "s")}."
                : $"Showing {Sessions.Count:n0} partial session result{(Sessions.Count == 1 ? "" : "s")}. Use Refresh to complete the scan.";
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

    private void ApplyStartupScanProgress(SessionScanProgress progress)
    {
        if (_startupScanResultFinalized)
        {
            return;
        }

        _startupReportedScannedFileCount = progress.ScannedFileCount;
        _startupTotalFileCount = progress.TotalFileCount;
        if (progress.Match is null || Sessions.Any(session => string.Equals(session.File, progress.Match.File, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Sessions.Add(new SessionCardViewModel(progress.Match));
        if (!StartupLoadingVisible)
        {
            ApplyFilters();
            PartialSessionResultsMessage = BuildPartialSessionResultsMessage(_startupScannedFileCount, _startupTotalFileCount, Sessions.Count);
        }
    }

    private void StopStartupScan()
    {
        if (!StartupScanActive)
        {
            return;
        }

        _startupLoadCancellation?.Cancel();
        StartupLoadingVisible = false;
        PartialSessionResultsMessage = BuildPartialSessionResultsMessage(_startupScannedFileCount, _startupTotalFileCount, Sessions.Count);
        ApplyFilters();
        StatusMessage = $"Showing {Sessions.Count:n0} partial session result{(Sessions.Count == 1 ? "" : "s")}. Use Refresh to complete the scan.";
    }

    private void AdvanceStartupProgressDisplay()
    {
        var target = _startupReportedScannedFileCount;
        if (_startupScannedFileCount < target)
        {
            var remaining = target - _startupScannedFileCount;
            _startupScannedFileCount += Math.Max(1, (int)Math.Ceiling(remaining / 10d));
        }

        StartupLoadingMessage = $"Scanning session files: {_startupScannedFileCount:n0} of {_startupTotalFileCount:n0}...";
    }

    private static string BuildPartialSessionResultsMessage(int scannedFileCount, int totalFileCount, int sessionCount)
        => $"Partial results: scan stopped after {scannedFileCount:n0} of {totalFileCount:n0} session files; {sessionCount:n0} session{(sessionCount == 1 ? "" : "s")} found. Use Refresh to complete the list.";

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
        var requestVersion = ++_metricsRequestVersion;
        var session = SelectedSession;
        if (session is null)
        {
            SelectedMetrics = null;
            IsDetailLoading = false;
            return;
        }

        IsDetailLoading = true;
        SelectedMetrics = null;
        try
        {
            var metrics = await Task.Run(() => _sessions.GetSessionDetailMetricsAsync(session.File, forceDeepAnalysis));
            if (requestVersion == _metricsRequestVersion && ReferenceEquals(SelectedSession, session))
            {
                SelectedMetrics = metrics;
            }
        }
        catch (Exception ex)
        {
            if (requestVersion == _metricsRequestVersion)
            {
                StatusMessage = ex.Message;
            }
        }
        finally
        {
            if (requestVersion == _metricsRequestVersion)
            {
                IsDetailLoading = false;
                OnPropertyChanged(nameof(InteractionSummary));
            }
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
            var sessionFile = SelectedSession.File;
            _transcript = await Task.Run(() => _sessions.ReadSessionTranscriptAsync(sessionFile, SessionBrowserMaxEntries));
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
        OperationStage = "starting";
        OperationProgress = 2;
        OperationOutputPath = null;
        var jobId = _sessions.StartExportJob(ExportFormat, SelectedSession.File, ExportImages, ExportInlineImages, ExportToolCallResults, outputDirectory, outputPath).JobId;
        SetActiveOperation("export", jobId);
        try
        {
            await PollExportJobAsync(jobId);
        }
        finally
        {
            ClearActiveOperation(jobId);
        }
    }

    private async Task OpenBatchExportDialogAsync()
    {
        if (FilteredSessions.Count == 0)
        {
            return;
        }

        var appSettings = await _settings.ReadAsync();
        BatchExportDirectory = appSettings.ExportDirectory ?? GetDefaultBatchExportDirectory();
        BatchExportSessions.Clear();
        foreach (var session in FilteredSessions)
        {
            BatchExportSessions.Add(new BatchExportSessionViewModel(session, ExportFormat, OnBatchExportSelectionChanged));
        }

        OnBatchExportSelectionChanged();
        BatchExportDialogVisible = true;
    }

    private void SetAllBatchExportSelections(bool isSelected)
    {
        _updatingBatchSelection = true;
        try
        {
            foreach (var session in BatchExportSessions)
            {
                session.IsSelected = isSelected;
            }
        }
        finally
        {
            _updatingBatchSelection = false;
        }

        OnBatchExportSelectionChanged();
    }

    private void OnBatchExportSelectionChanged()
    {
        if (_updatingBatchSelection)
        {
            return;
        }

        OnPropertyChanged(nameof(BatchExportSelectedCount));
        OnPropertyChanged(nameof(BatchExportSelectionText));
        OnPropertyChanged(nameof(CanStartBatchExport));
        ConfirmBatchExportCommand.RaiseCanExecuteChanged();
    }

    private async Task BrowseBatchExportDirectoryAsync()
    {
        var selected = await _ui.PickExportDirectoryFromAsync(BatchExportDirectory);
        if (selected is not null)
        {
            BatchExportDirectory = selected;
        }
    }

    private async Task StartBatchExportAsync()
    {
        var selected = BatchExportSessions
            .Where(session => session.IsSelected)
            .Select(session => new BatchExportSessionRequest(session.File, session.Title, session.StartedAt))
            .ToArray();
        if (selected.Length == 0 || !IsValidDirectoryPath(BatchExportDirectory))
        {
            return;
        }

        var outputDirectory = Path.GetFullPath(BatchExportDirectory.Trim());
        await _settings.UpdateAsync(settings => settings.ExportDirectory = outputDirectory);
        BatchExportDirectory = outputDirectory;
        BatchExportDialogVisible = false;
        ExportProgressVisible = true;
        OperationTitle = "Batch exporting...";
        OperationMessage = $"Preparing {selected.Length} sessions...";
        OperationStage = "starting";
        OperationProgress = 1;
        OperationOutputPath = outputDirectory;
        var jobId = _sessions.StartBatchExportJob(
            ExportFormat,
            selected,
            ExportImages,
            ExportInlineImages,
            ExportToolCallResults,
            outputDirectory).JobId;
        SetActiveOperation("batch", jobId);
        try
        {
            while (true)
            {
                var status = _sessions.GetBatchExportJobStatus(jobId);
                ApplyBatchOperationStatus(status);
                if (status.Kind != "working")
                {
                    break;
                }

                await Task.Delay(250);
            }
        }
        finally
        {
            ClearActiveOperation(jobId);
        }
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
        OperationStage = "starting";
        OperationProgress = 2;
        OperationOutputPath = null;
        var jobId = _sessions.StartSanitizedCopyJob(
            SelectedSession.File,
            string.IsNullOrWhiteSpace(ClaudeHome) ? null : ClaudeHome,
            SanitizeChatName,
            SanitizeStripImageContent,
            SanitizeStripBlobContent,
            SanitizeCreateJsonlCopy,
            SanitizeReAddToCurrentDay).JobId;
        SetActiveOperation("sanitize", jobId);
        try
        {
            await PollSanitizedJobAsync(jobId);
        }
        finally
        {
            ClearActiveOperation(jobId);
        }
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

    private void CancelOperation()
    {
        if (_activeOperationJobId is null)
        {
            return;
        }

        switch (_activeOperationKind)
        {
            case "export":
                _sessions.CancelExportJob(_activeOperationJobId);
                break;
            case "batch":
                _sessions.CancelBatchExportJob(_activeOperationJobId);
                break;
            case "sanitize":
                _sessions.CancelSanitizedCopyJob(_activeOperationJobId);
                break;
        }
    }

    private void SetActiveOperation(string kind, string jobId)
    {
        _activeOperationKind = kind;
        _activeOperationJobId = jobId;
        RaiseOperationState();
    }

    private void ClearActiveOperation(string jobId)
    {
        if (!string.Equals(_activeOperationJobId, jobId, StringComparison.Ordinal))
        {
            return;
        }

        _activeOperationKind = null;
        _activeOperationJobId = null;
        RaiseOperationState();
    }

    private void RaiseOperationState()
    {
        OnPropertyChanged(nameof(IsOperationRunning));
        OnPropertyChanged(nameof(CanCloseOperationProgress));
        OnPropertyChanged(nameof(CanStartBatchExport));
        OpenExportDialogCommand.RaiseCanExecuteChanged();
        ConfirmExportCommand.RaiseCanExecuteChanged();
        OpenBatchExportDialogCommand.RaiseCanExecuteChanged();
        ConfirmBatchExportCommand.RaiseCanExecuteChanged();
        OpenSanitizeDialogCommand.RaiseCanExecuteChanged();
        ConfirmSanitizeCommand.RaiseCanExecuteChanged();
        CancelOperationCommand.RaiseCanExecuteChanged();
    }

    private async Task StartTokenSummaryAsync()
    {
        TokenSummaryDialogVisible = true;
        TokenSummaryStatus = new TokenUsageSummaryJobStatus("working", 1, "starting", "Preparing token summary...", 0, FilteredSessions.Count, null, null);
        _activeTokenSummaryJobId = _sessions.StartTokenUsageSummaryJob(FilteredSessions.Select(s => s.File).ToArray(), _anthropicPricing).JobId;
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

    private async Task ExportTokenSummaryAsync(object? parameter)
    {
        var summary = TokenSummaryStatus?.Result;
        if (summary is null || parameter is not string format)
        {
            return;
        }

        var baseName = BuildTokenSummaryExportBaseName(summary);
        try
        {
            var outputPath = format switch
            {
                "png" => await _ui.SaveStatisticsImageAsync($"{baseName}.png"),
                "csv" => await _ui.SaveStatisticsTextAsync($"{baseName}.csv", "csv", _sessions.FormatTokenUsageSummaryAsCsv(summary)),
                "md" => await _ui.SaveStatisticsTextAsync($"{baseName}.md", "md", _sessions.FormatTokenUsageSummaryAsMarkdown(summary)),
                _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "Unsupported token statistics export format.")
            };
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                StatusMessage = $"Token statistics exported: {outputPath}";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private static string BuildTokenSummaryExportBaseName(TokenUsageSummaryResult summary)
    {
        if (summary.DailyBreakdown.Count == 0)
        {
            return $"clodlogs-token-statistics-{DateTime.Today:yyyy-MM-dd}";
        }

        var first = summary.DailyBreakdown[0].Date;
        var last = summary.DailyBreakdown[^1].Date;
        var period = first == last
            ? first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : $"{first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_to_{last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        return $"clodlogs-token-statistics-{period}";
    }

    private async Task RefreshPricingAsync()
    {
        IsPricingRefreshing = true;
        PricingStatus = "Refreshing prices from Anthropic...";
        try
        {
            var refreshed = await new AnthropicPricingService().RefreshAsync();
            _anthropicPricing = refreshed;
            await _settings.UpdateAsync(settings => settings.AnthropicPricing = refreshed);
            RefreshAnthropicPricingRows();
            PricingStatus = FormatPricingStatus(refreshed);
            OnPropertyChanged(nameof(PricingSourceUrl));
        }
        catch (Exception ex)
        {
            PricingStatus = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsPricingRefreshing = false;
        }
    }

    private void RefreshAnthropicPricingRows()
    {
        AnthropicPricingRows.Clear();
        foreach (var price in _anthropicPricing.Models)
        {
            AnthropicPricingRows.Add(new AnthropicPricingRowViewModel(price));
        }
    }

    private void RefreshTokenSummaryBreakdowns(TokenUsageSummaryResult? result)
    {
        TokenSummaryDailyBars.Clear();
        TokenSummaryModelRows.Clear();
        if (result is null)
        {
            return;
        }

        var maxTokens = Math.Max(1L, result.DailyBreakdown.Select(day => day.TokenUsage.TotalTokens).DefaultIfEmpty(0).Max());
        foreach (var day in result.DailyBreakdown)
        {
            TokenSummaryDailyBars.Add(new TokenUsageDailyBarViewModel(day, maxTokens));
        }

        foreach (var model in result.ModelBreakdown)
        {
            TokenSummaryModelRows.Add(new TokenUsageModelRowViewModel(model));
        }
    }

    private static string FormatPricingStatus(AnthropicPricing pricing)
    {
        if (DateTimeOffset.TryParse(pricing.RefreshedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var refreshed))
        {
            return $"Last refreshed {refreshed.ToLocalTime():g}. Prices are USD per million tokens.";
        }

        return "Bundled Anthropic prices are active. Prices are USD per million tokens.";
    }

    private static string FormatCompactTokens(long value)
    {
        if (value >= 1_000_000_000)
        {
            return $"{value / 1_000_000_000d:0.##}B";
        }
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.##}M";
        }
        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.##}K";
        }

        return value.ToString("n0", CultureInfo.CurrentCulture);
    }

    private static string FormatUsd(decimal value)
        => $"${value:0.00}";

    private async Task RevealOperationOutputAsync()
    {
        if (string.IsNullOrWhiteSpace(OperationOutputPath))
        {
            return;
        }

        if (!await _ui.RevealPathAsync(OperationOutputPath))
        {
            StatusMessage = $"Output path is unavailable: {OperationOutputPath}";
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
        OperationStage = FormatOperationStage(status.Stage);
        OperationProgress = status.ProgressPercent;
        OperationOutputPath = status.OutputPath;
    }

    private void ApplyBatchOperationStatus(BatchExportJobStatus status)
    {
        OperationTitle = status.Kind switch
        {
            "success" => "Batch export complete",
            "partial" => "Batch export partially complete",
            "error" => "Batch export failed",
            "cancelled" => "Batch export cancelled",
            _ => "Batch exporting..."
        };
        OperationMessage = status.Message;
        OperationStage = status.Result?.Failures.FirstOrDefault() is { } failure
            ? $"First failure: {Path.GetFileName(failure.SessionFilePath)}: {failure.Message}"
            : FormatOperationStage(status.Stage);
        OperationProgress = status.ProgressPercent;
        OperationOutputPath = status.OutputDirectory;
    }

    private static string FormatOperationStage(string stage)
        => string.Equals(stage, "done", StringComparison.OrdinalIgnoreCase) ? "Done." : stage;

    private void DismissDialogs()
    {
        ExportDialogVisible = false;
        BatchExportDialogVisible = false;
        if (!IsOperationRunning)
        {
            ExportProgressVisible = false;
            SanitizeProgressVisible = false;
        }
        SanitizeDialogVisible = false;
        TokenSummaryDialogVisible = false;
        OptionsDialogVisible = false;
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
        OpenBatchExportDialogCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandState()
    {
        AnalyzeAnywayCommand.RaiseCanExecuteChanged();
        OpenTranscriptCommand.RaiseCanExecuteChanged();
        OpenExportDialogCommand.RaiseCanExecuteChanged();
        OpenSanitizeDialogCommand.RaiseCanExecuteChanged();
        ConfirmExportCommand.RaiseCanExecuteChanged();
        ConfirmSanitizeCommand.RaiseCanExecuteChanged();
        CopySelectedTokenUsageCommand.RaiseCanExecuteChanged();
        OpenSelectedFileCommand.RaiseCanExecuteChanged();
        RevealSelectedFileCommand.RaiseCanExecuteChanged();
    }

    private static bool IsValidDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            _ = Path.GetFullPath(path.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GetDefaultBatchExportDirectory()
    {
        var baseDirectory = IsValidDirectoryPath(FolderPath)
            ? Path.GetFullPath(FolderPath.Trim())
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        if (!IsValidDirectoryPath(baseDirectory))
        {
            baseDirectory = System.Environment.CurrentDirectory;
        }

        return Path.Combine(baseDirectory, "export");
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

public sealed class TokenUsageDailyBarViewModel
{
    public TokenUsageDailyBarViewModel(TokenUsageDailyBreakdown breakdown, long maxTokens)
    {
        DayLabel = breakdown.Date.Day.ToString(CultureInfo.CurrentCulture);
        TokensText = $"{breakdown.TokenUsage.TotalTokens:n0} tokens";
        CostText = $"${breakdown.Cost:0.00}";
        Height = breakdown.TokenUsage.TotalTokens == 0
            ? 2
            : Math.Max(6, Math.Round(breakdown.TokenUsage.TotalTokens / (double)Math.Max(1, maxTokens) * 84));
    }

    public string DayLabel { get; }
    public string TokensText { get; }
    public string CostText { get; }
    public double Height { get; }
}

public sealed class TokenUsageModelRowViewModel(TokenUsageModelBreakdown breakdown)
{
    public string Model => breakdown.Model;
    public string TokensText => $"{breakdown.TokenUsage.TotalTokens:n0}";
    public string CostText => $"${breakdown.Cost:0.00}";
}

public sealed class AnthropicPricingRowViewModel(AnthropicModelPrice price)
{
    public string Model => price.Model;
    public string InputText => $"${price.InputPerMillionTokens:0.##}";
    public string CacheWrite5MinuteText => $"${price.CacheWrite5MinutePerMillionTokens:0.##}";
    public string CacheWrite1HourText => $"${price.CacheWrite1HourPerMillionTokens:0.##}";
    public string CacheReadText => $"${price.CacheReadPerMillionTokens:0.##}";
    public string OutputText => $"${price.OutputPerMillionTokens:0.##}";
}
