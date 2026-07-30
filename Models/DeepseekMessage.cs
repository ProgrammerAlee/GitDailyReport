using System.Text.Json.Serialization;

namespace GitDailyReport.Models;

/// <summary>
/// Deepseek API 消息
/// </summary>
public class DeepseekMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Deepseek API 请求体
/// </summary>
public class DeepseekRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "deepseek-v4-pro";

    [JsonPropertyName("messages")]
    public List<DeepseekMessage> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.7;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 4096;
}

/// <summary>
/// Deepseek API 响应
/// </summary>
public class DeepseekResponse
{
    [JsonPropertyName("choices")]
    public List<DeepseekChoice> Choices { get; set; } = [];

    [JsonPropertyName("error")]
    public DeepseekError? Error { get; set; }
}

public class DeepseekChoice
{
    [JsonPropertyName("message")]
    public DeepseekMessage Message { get; set; } = new();
}

public class DeepseekError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}
