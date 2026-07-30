using GitDailyReport.Models;

namespace GitDailyReport.Services;

/// <summary>
/// 设置管理服务接口
/// </summary>
public interface ISettingsService
{
    /// <summary>加载设置</summary>
    AppSettings LoadSettings();

    /// <summary>保存设置</summary>
    void SaveSettings(AppSettings settings);

    /// <summary>加密存储 API Key（返回加密后的 Base64）</summary>
    string EncryptApiKey(string apiKey);

    /// <summary>解密 API Key</summary>
    string DecryptApiKey(string encryptedBase64);

    /// <summary>获取设置文件路径</summary>
    string GetSettingsFilePath();
}
