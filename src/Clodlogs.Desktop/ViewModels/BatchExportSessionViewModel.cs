using Clodlogs.Desktop.Services;

namespace Clodlogs.Desktop.ViewModels;

public sealed class BatchExportSessionViewModel : ViewModelBase
{
    private readonly Action _selectionChanged;
    private bool _isSelected = true;
    private string _outputFileName;

    public BatchExportSessionViewModel(SessionCardViewModel session, string format, Action selectionChanged)
    {
        Session = session;
        _selectionChanged = selectionChanged;
        _outputFileName = ClaudeSessionService.BuildBatchExportFileName(session.Title, session.Session.StartedAt, format);
    }

    public SessionCardViewModel Session { get; }
    public string Title => Session.Title;
    public string StartedAtLabel => Session.StartedAtLabel;
    public string File => Session.File;
    public string? StartedAt => Session.Session.StartedAt;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _selectionChanged();
            }
        }
    }

    public string OutputFileName
    {
        get => _outputFileName;
        private set => SetProperty(ref _outputFileName, value);
    }

    public void RefreshOutputFileName(string format)
        => OutputFileName = ClaudeSessionService.BuildBatchExportFileName(Title, StartedAt, format);
}
