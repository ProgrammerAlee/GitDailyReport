namespace GitDailyReport.Services;

/// <summary>
/// Deepseek API 调用服务接口
/// </summary>
public interface IDeepseekService
{
    /// <summary>
    /// 使用自定义 Prompt 发送到 Deepseek，获取格式化的工作日报
    /// </summary>
    /// <param name="fullPrompt">完整 Prompt（已替换占位符）</param>
    /// <param name="apiKey">API Key</param>
    /// <param name="progress">流式输出进度（累计文本）</param>
    /// <param name="ct">取消令牌</param>
    Task<string> GenerateDailyReportWithPromptAsync(
        string fullPrompt,
        string apiKey,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
