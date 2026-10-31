using System.ComponentModel;
using FileBridge.Core;

namespace FileBridge.Desktop;

public sealed class FileRow(TransferFile file) : INotifyPropertyChanged
{
    public TransferFile File { get; } = file;
    public string Name => File.RelativeName;
    public string Size => TransferDisplay.Bytes(File.Size, Localization.Current.Culture);
    private TransferProgress progress = new(file.RelativeName, TransferStage.Pending);
    public TransferProgress Progress
    {
        get => progress;
        set
        {
            progress = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }
    public string Status => TransferDisplay.StageText(progress, Localization.Current);
    public void RefreshLanguage()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Size)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

