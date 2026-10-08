using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace GitDailyReport.Services;

public class WpfDialogService : IDialogService
{
    public void ShowInfo(string message, string title) =>
        Show(message, title, MessageBoxImage.Information);

    public void ShowWarning(string message, string title) =>
        Show(message, title, MessageBoxImage.Warning);

    public void ShowError(string message, string title) =>
        Show(message, title, MessageBoxImage.Error);

    public bool Confirm(string message, string title)
    {
        var owner = ResolveOwner();
        var result = owner == null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName)
            ? dialog.FolderName
            : null;
    }

    public string? PickSaveFile(string title, string defaultFileName, string filter, string defaultExt)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            Filter = filter,
            DefaultExt = defaultExt,
            AddExtension = true
        };

        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FileName)
            ? dialog.FileName
            : null;
    }

    public void SetClipboardText(string text)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(80);
            }
        }

        throw last ?? new InvalidOperationException("无法写入剪贴板。");
    }

    private static void Show(string message, string title, MessageBoxImage icon)
    {
        var owner = ResolveOwner();
        if (owner == null)
            MessageBox.Show(message, title, MessageBoxButton.OK, icon);
        else
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
    }

    private static Window? ResolveOwner()
    {
        var app = Application.Current;
        if (app == null)
            return null;

        return app.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? app.MainWindow;
    }
}
