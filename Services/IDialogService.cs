namespace GitDailyReport.Services;

/// <summary>
/// 界面交互。ViewModel 不直接依赖 MessageBox 和文件对话框。
/// </summary>
public interface IDialogService
{
    void ShowInfo(string message, string title);
    void ShowWarning(string message, string title);
    void ShowError(string message, string title);
    bool Confirm(string message, string title);
    string? PickFolder(string title, string? initialDirectory);
    string? PickSaveFile(string title, string defaultFileName, string filter, string defaultExt);
    void SetClipboardText(string text);
}
