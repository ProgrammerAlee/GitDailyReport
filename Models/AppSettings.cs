namespace GitDailyReport.Models;

/// <summary>
/// 应用配置数据
/// </summary>
public class AppSettings
{
    /// <summary>DPAPI 加密后的 API Key（Base64）</summary>
    public string EncryptedApiKey { get; set; } = string.Empty;

    /// <summary>Git 仓库路径列表</summary>
    public List<string> RepoPaths { get; set; } = [];

    /// <summary>上次选择的日期</summary>
    public string LastSelectedDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");

    /// <summary>用户自定义的 Prompt 模板（为空则用默认）</summary>
    public string CustomPrompt { get; set; } = string.Empty;

    /// <summary>上次选中的作者邮箱列表（用于恢复筛选状态）</summary>
    public List<string> SelectedAuthorEmails { get; set; } = [];
}
