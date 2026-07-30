using System.Diagnostics;
using System.Windows;

namespace GitDailyReport;

/// <summary>
/// MainWindow code-behind — 处理 PasswordBox 事件和外部链接
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// PasswordBox 不支持直接绑定，通过 PasswordChanged 事件同步到 ViewModel
    /// </summary>
    private void ApiKeyPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
        {
            vm.ApiKey = ApiKeyPasswordBox.Password;
        }
    }

    /// <summary>
    /// 打开 Deepseek API Key 获取页面
    /// </summary>
    private void OpenApiKeyUrl_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://platform.deepseek.com/api_keys",
            UseShellExecute = true
        });
    }
}
