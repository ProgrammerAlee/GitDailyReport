using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using GitDailyReport.ViewModels;

namespace GitDailyReport;

/// <summary>
/// MainWindow code-behind — 处理 PasswordBox 事件和外部链接
/// </summary>
public partial class MainWindow : Window
{
    private bool _syncingPassword;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncPasswordBoxFromViewModel();
        if (DataContext is MainViewModel vm)
            await vm.InitializeAsync();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is MainViewModel newVm)
            newVm.PropertyChanged += OnViewModelPropertyChanged;
        SyncPasswordBoxFromViewModel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ApiKey))
            SyncPasswordBoxFromViewModel();
    }

    private void SyncPasswordBoxFromViewModel()
    {
        if (DataContext is not MainViewModel vm) return;
        if (ApiKeyPasswordBox.Password == vm.ApiKey) return;

        _syncingPassword = true;
        ApiKeyPasswordBox.Password = vm.ApiKey ?? string.Empty;
        _syncingPassword = false;
    }

    /// <summary>
    /// PasswordBox 不支持直接绑定，通过 PasswordChanged 事件同步到 ViewModel
    /// </summary>
    private void ApiKeyPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        if (DataContext is MainViewModel vm)
            vm.ApiKey = ApiKeyPasswordBox.Password;
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
