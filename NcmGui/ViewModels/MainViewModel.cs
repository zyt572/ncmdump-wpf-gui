using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using NcmGui.Core;
using NcmGui.Models;

namespace NcmGui.ViewModels;

public enum LogKind
{
    Info,
    Ok,
    Err,
    Dim,
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int MaxFiles = 3000;

    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private string _outputDir = string.Empty;
    private bool _removeSource;
    private bool _recursive = true;
    private bool _isRunning;
    private bool _hasRun;
    private bool _hasOutput;
    private bool? _engineReady;
    private double _progress;
    private string _runningText = string.Empty;
    private double _lastSeconds;
    private int _okCount;
    private int _errCount;
    private int _logCount;
    private string _version = string.Empty;
    private Task? _initTask;

    public ObservableCollection<FileItem> Files { get; } = new();

    public event Action<string, LogKind>? Logged;
    public event Action? RequestShowLog;
    public event PropertyChangedEventHandler? PropertyChanged;

    // ------------------------------------------------------------ 设置

    public string OutputDir
    {
        get => _outputDir;
        set => Set(ref _outputDir, value);
    }

    public bool RemoveSource
    {
        get => _removeSource;
        set => Set(ref _removeSource, value);
    }

    public bool Recursive
    {
        get => _recursive;
        set => Set(ref _recursive, value);
    }

    // ------------------------------------------------------------ 状态

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            Raise(nameof(CanPick), nameof(CanStart), nameof(StatusText));
        }
    }

    public bool HasRun
    {
        get => _hasRun;
        private set => Set(ref _hasRun, value);
    }

    public bool HasOutput
    {
        get => _hasOutput;
        private set => Set(ref _hasOutput, value);
    }

    public bool? EngineReady
    {
        get => _engineReady;
        private set
        {
            if (!Set(ref _engineReady, value)) return;
            Raise(nameof(CanStart), nameof(StatusText), nameof(BinaryLabel), nameof(IsOnline));
        }
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public bool HasFiles => Files.Count > 0;

    public bool CanStart => !IsRunning && Files.Count > 0 && EngineReady == true;

    public bool CanPick => !IsRunning;

    public bool IsOnline => EngineReady == true;

    public string BinaryLabel => EngineReady == true
        ? (string.IsNullOrEmpty(_version) ? "ncmdump" : $"ncmdump {_version}")
        : EngineReady == false ? "缺少 ncmdump" : "ncmdump";

    public string TotalSizeText => Files.Count == 0
        ? string.Empty
        : FileItem.FormatSize(Files.Sum(f => f.Size));

    public string LogCountText => _logCount > 0 ? $"{_logCount} 条" : string.Empty;

    public string StatusText
    {
        get
        {
            if (IsRunning) return _runningText;
            if (Files.Count == 0)
            {
                return EngineReady switch
                {
                    null => "正在检查 ncmdump…",
                    true => "准备就绪",
                    false => "找不到 ncmdump.exe",
                };
            }
            if (HasRun)
            {
                var parts = new List<string> { $"已处理 {Files.Count} 个" };
                if (_okCount > 0) parts.Add($"成功 {_okCount}");
                if (_errCount > 0) parts.Add($"失败 {_errCount}");
                if (_lastSeconds > 0.05) parts.Add($"用时 {_lastSeconds:0.0} 秒");
                return string.Join(" · ", parts);
            }
            return $"{Files.Count} 个文件待转换";
        }
    }

    // ------------------------------------------------------------ 初始化

    public Task InitializeAsync() => _initTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        if (!ConversionEngine.Locate())
        {
            EngineReady = false;
            Log("找不到 ncmdump.exe：请把它放到本程序同目录，然后重新启动", LogKind.Err);
            RequestShowLog?.Invoke();
            return;
        }
        _version = await ConversionEngine.ReadVersionAsync();
        EngineReady = true;
        Log($"已连接 ncmdump {_version}".TrimEnd(), LogKind.Dim);
    }

    // ------------------------------------------------------------ 文件

    public (int added, int skipped) AddPaths(IEnumerable<string> inputs)
    {
        var added = 0;
        var skipped = 0;
        foreach (var raw in inputs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Files.Count >= MaxFiles) { skipped++; continue; }

            string full;
            try { full = System.IO.Path.GetFullPath(raw); }
            catch { skipped++; continue; }

            if (File.Exists(full))
            {
                if (full.EndsWith(".ncm", StringComparison.Ordinal))
                {
                    if (TryAdd(full)) added++; else skipped++;
                }
                else skipped++;
            }
            else if (Directory.Exists(full))
            {
                try
                {
                    var option = Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    foreach (var file in Directory.EnumerateFiles(full, "*.ncm", option))
                    {
                        if (Files.Count >= MaxFiles) { skipped++; continue; }
                        if (TryAdd(file)) added++; else skipped++;
                    }
                }
                catch { skipped++; }
            }
            else skipped++;
        }

        if (added > 0)
        {
            HasRun = false;
            HasOutput = false;
            NotifyFileState();
        }
        return (added, skipped);
    }

    private bool TryAdd(string path)
    {
        if (!_knownPaths.Add(path)) return false;
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            _knownPaths.Remove(path);
            return false;
        }
        Files.Add(new FileItem(path, info.Length));
        return true;
    }

    public void Remove(FileItem item)
    {
        if (IsRunning) return;
        Files.Remove(item);
        _knownPaths.Remove(item.Path);
        HasRun = false;
        HasOutput = Files.Any(f => f.CanReveal);
        NotifyFileState();
    }

    public void Clear()
    {
        if (IsRunning) return;
        Files.Clear();
        _knownPaths.Clear();
        HasRun = false;
        HasOutput = false;
        Progress = 0;
        _okCount = _errCount = 0;
        _lastSeconds = 0;
        NotifyFileState();
    }

    // ------------------------------------------------------------ 转换

    public async Task StartAsync()
    {
        if (!CanStart) return;

        if (!string.IsNullOrWhiteSpace(OutputDir))
        {
            try { Directory.CreateDirectory(OutputDir.Trim()); }
            catch (Exception ex)
            {
                Log($"无法创建输出目录：{ex.Message}", LogKind.Err);
                return;
            }
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;
        HasOutput = false;
        Progress = 0;
        _okCount = _errCount = 0;
        HasRun = true;

        var total = Files.Count;
        var done = 0;
        var sw = Stopwatch.StartNew();
        Log($"开始转换 {total} 个文件", LogKind.Info);

        foreach (var item in Files.ToList())
        {
            if (token.IsCancellationRequested)
            {
                item.Status = FileStatus.Cancelled;
                item.Message = "已取消";
                done++;
                Progress = done * 100.0 / total;
                continue;
            }

            item.Status = FileStatus.Running;
            item.Message = string.Empty;
            item.Output = string.Empty;
            item.OutputSize = 0;
            _runningText = $"正在转换 {done + 1} / {total}";
            Raise(nameof(StatusText));

            try
            {
                var result = await ConversionEngine.RunAsync(
                    item.Path,
                    string.IsNullOrWhiteSpace(OutputDir) ? null : OutputDir.Trim(),
                    RemoveSource,
                    token);

                item.Status = result.Status;
                item.Message = result.Message;
                item.Output = result.Output;
                item.OutputSize = result.Bytes;
                item.Seconds = result.Seconds;

                switch (result.Status)
                {
                    case FileStatus.Ok:
                        _okCount++;
                        HasOutput = true;
                        Log($"完成：{item.Name} → {System.IO.Path.GetFileName(result.Output)}", LogKind.Ok);
                        break;
                    case FileStatus.Warn:
                        _okCount++;
                        Log($"提示：{item.Name} — {result.Message}", LogKind.Dim);
                        break;
                    default:
                        _errCount++;
                        Log($"失败：{item.Name} — {result.Message}", LogKind.Err);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = FileStatus.Cancelled;
                item.Message = "已取消";
                Log($"已取消：{item.Name}", LogKind.Dim);
            }
            catch (Exception ex)
            {
                item.Status = FileStatus.Error;
                item.Message = ex.Message;
                _errCount++;
                Log($"失败：{item.Name} — {ex.Message}", LogKind.Err);
            }

            done++;
            Progress = done * 100.0 / total;
        }

        sw.Stop();
        _lastSeconds = sw.Elapsed.TotalSeconds;
        IsRunning = false;
        Raise(nameof(StatusText), nameof(Progress));

        var summary = $"结束 · 成功 {_okCount} · 失败 {_errCount} · 用时 {_lastSeconds:0.0} 秒";
        Log(summary, _errCount > 0 ? LogKind.Err : LogKind.Ok);
        if (_errCount > 0) RequestShowLog?.Invoke();

        _cts.Dispose();
        _cts = null;
    }

    public void Cancel() => _cts?.Cancel();

    // ------------------------------------------------------------ 打开目录

    public string? ResolveOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(OutputDir)) return OutputDir.Trim();
        var withOutput = Files.FirstOrDefault(f => f.CanReveal);
        if (withOutput is not null) return System.IO.Path.GetDirectoryName(withOutput.Output);
        return Files.Count > 0 ? System.IO.Path.GetDirectoryName(Files[0].Path) : null;
    }

    public void OpenOutputFolder()
    {
        var folder = ResolveOutputFolder();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    public static void RevealInExplorer(string path)
    {
        if (!File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    // ------------------------------------------------------------ 通知

    public void Log(string message, LogKind kind = LogKind.Info)
    {
        _logCount++;
        Raise(nameof(LogCountText));
        Logged?.Invoke($"{DateTime.Now:HH:mm:ss}  {message}", kind);
    }

    private void NotifyFileState()
        => Raise(nameof(HasFiles), nameof(TotalSizeText), nameof(StatusText), nameof(CanStart), nameof(Progress));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(params string?[] names)
    {
        foreach (var name in names)
        {
            if (name is not null)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
