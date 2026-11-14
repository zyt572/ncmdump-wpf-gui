using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NcmGui.Models;
using NcmGui.ViewModels;

namespace NcmGui;

public partial class MainWindow : Window
{
    private static readonly Brush DropStrokeIdle = MakeBrush("#DFE3E9");
    private static readonly Brush DropFillIdle = MakeBrush("#FCFCFD");
    private static readonly Brush DropStrokeActive = MakeBrush("#A9BFF2");
    private static readonly Brush DropFillActive = MakeBrush("#F7FAFF");

    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.Logged += AppendLog;
        _vm.RequestShowLog += () => SetLogVisible(true);

        Loaded += async (_, _) => await _vm.InitializeAsync();
        Closing += (_, _) => _vm.Cancel();
    }

    private static Brush MakeBrush(string hex)
        => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    // ------------------------------------------------------------ 日志

    private void AppendLog(string line, LogKind kind)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void LogToggle_Click(object sender, RoutedEventArgs e)
        => SetLogVisible(LogBox.Visibility != Visibility.Visible);

    private void SetLogVisible(bool visible)
    {
        LogBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        LogChevRotate.Angle = visible ? 90 : 0;
    }

    // ------------------------------------------------------------ 选择文件

    private void PickFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 NCM 文件",
            Filter = "网易云加密文件 (*.ncm)|*.ncm|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        AfterAdd(_vm.AddPaths(dialog.FileNames));
    }

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含 NCM 文件的文件夹",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        AfterAdd(_vm.AddPaths(new[] { dialog.FolderName }));
    }

    private void PickOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择输出目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _vm.OutputDir = dialog.FolderName;
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e) => _vm.OutputDir = string.Empty;

    private void AfterAdd((int added, int skipped) result)
    {
        if (result.added > 0)
        {
            _vm.Log($"添加 {result.added} 个文件", LogKind.Dim);
            if (result.skipped > 0) _vm.Log($"已忽略 {result.skipped} 个项目", LogKind.Dim);
            return;
        }
        MessageBox.Show(this, "没有找到新的 .ncm 文件。", "NCM 转换台",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------ 拖放

    private void Window_DragEnter(object sender, DragEventArgs e) => SetDropActive(true);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => SetDropActive(false);

    private void Window_Drop(object sender, DragEventArgs e)
    {
        SetDropActive(false);
        if (_vm.IsRunning) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        AfterAdd(_vm.AddPaths(paths));
    }

    private void SetDropActive(bool active)
    {
        DropRect.Stroke = active ? DropStrokeActive : DropStrokeIdle;
        DropRect.Fill = active ? DropFillActive : DropFillIdle;
    }

    // ------------------------------------------------------------ 列表操作

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileItem item }) _vm.Remove(item);
    }

    private void Reveal_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileItem item } && item.CanReveal)
            MainViewModel.RevealInExplorer(item.Output);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _vm.Clear();

    // ------------------------------------------------------------ 转换

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "转换出错", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _vm.Cancel();

    private void OpenOutput_Click(object sender, RoutedEventArgs e) => _vm.OpenOutputFolder();
}
