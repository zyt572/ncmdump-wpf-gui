using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace NcmGui;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
            }
            catch
            {
                // 写日志失败就算了，不能让异常处理再抛异常
            }

            MessageBox.Show(e.Exception.Message, "NCM 转换台", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}

/// <summary>true → Collapsed，false → Visible，用于“空状态 / 列表”这类互斥显示。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
