using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace NcmGui.Models;

public enum FileStatus
{
    Pending,
    Running,
    Ok,
    Warn,
    Error,
    Cancelled,
}

public sealed class FileItem : INotifyPropertyChanged
{
    private FileStatus _status = FileStatus.Pending;
    private string _message = string.Empty;
    private string _output = string.Empty;
    private long _outputSize;
    private double _seconds;

    public FileItem(string path, long size)
    {
        Path = path;
        Size = size;
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    public long Size { get; }

    public FileStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(CanReveal));
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    public string Message
    {
        get => _message;
        set
        {
            if (_message == value) return;
            _message = value;
            OnPropertyChanged();
        }
    }

    public string Output
    {
        get => _output;
        set
        {
            if (_output == value) return;
            _output = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanReveal));
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    public long OutputSize
    {
        get => _outputSize;
        set
        {
            if (_outputSize == value) return;
            _outputSize = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeText));
        }
    }

    public double Seconds
    {
        get => _seconds;
        set
        {
            if (Math.Abs(_seconds - value) < 0.005) return;
            _seconds = value;
            OnPropertyChanged();
        }
    }

    public string StatusText => Status switch
    {
        FileStatus.Pending => "等待",
        FileStatus.Running => "转换中",
        FileStatus.Ok => "完成",
        FileStatus.Warn => "完成",
        FileStatus.Error => "失败",
        FileStatus.Cancelled => "已取消",
        _ => string.Empty,
    };

    public string SizeText => FormatSize(Status is FileStatus.Ok or FileStatus.Warn && OutputSize > 0 ? OutputSize : Size);

    public bool CanReveal => Status == FileStatus.Ok && !string.IsNullOrEmpty(Output);

    public string ToolTipText => CanReveal ? $"输出：{Output}\n点击在资源管理器中显示" : Path;

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return index == 0 ? $"{value:0} {units[index]}" : $"{value:0.0} {units[index]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
