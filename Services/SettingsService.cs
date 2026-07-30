using System.Security.Cryptography;
using System.Text.Json;
using GitDailyReport.Models;

namespace GitDailyReport.Services;

/// <summary>
/// 设置管理服务 — JSON 文件存储 + DPAPI 加密 API Key
/// </summary>
public class SettingsService : ISettingsService
{
    private static readonly string SettingsDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitDailyReport");

    private static readonly string SettingsFile = System.IO.Path.Combine(SettingsDir, "settings.json");

    /// <inheritdoc />
    public string GetSettingsFilePath() => SettingsFile;

    /// <inheritdoc />
    public AppSettings LoadSettings()
    {
        try
        {
            if (System.IO.File.Exists(SettingsFile))
            {
                var json = System.IO.File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // 文件损坏时返回默认设置
        }

        return new AppSettings();
    }

    /// <inheritdoc />
    public void SaveSettings(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(SettingsFile, json);
    }

    /// <inheritdoc />
    public string EncryptApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return string.Empty;

        var plainBytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
        var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }

    /// <inheritdoc />
    public string DecryptApiKey(string encryptedBase64)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
            return string.Empty;

        try
        {
            var encryptedBytes = Convert.FromBase64String(encryptedBase64);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
