namespace GitDailyReport.Models;

/// <summary>
/// Deepseek 生成结果。Truncated 为 true 表示输出碰到了长度上限。
/// </summary>
public sealed record ReportGenerationResult(string Content, bool Truncated);
