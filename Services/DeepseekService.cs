using System.Net.Http;
using System.Text;
using GitDailyReport.Models;

namespace GitDailyReport.Services;

/// <summary>
/// Deepseek API 调用服务 — 调用 Deepseek v4 pro 生成日报
/// </summary>
public class DeepseekService : IDeepseekService
{
    private const string ApiEndpoint = "https://api.deepseek.com/v1/chat/completions";
    private readonly HttpClient _httpClient;

    public DeepseekService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromMinutes(3);
    }

    /// <inheritdoc />
    public async Task<string> GenerateDailyReportWithPromptAsync(string fullPrompt, string apiKey, CancellationToken ct = default)
    {
        var request = new DeepseekRequest
        {
            Model = "deepseek-v4-pro",
            Messages =
            [
                new DeepseekMessage { Role = "user", Content = fullPrompt }
            ],
            Temperature = 0.7,
            MaxTokens = 4096
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint);
        httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
        httpRequest.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(httpRequest, ct);
        var jsonResponse = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = TryParseError(jsonResponse);
            throw new HttpRequestException(
                $"Deepseek API 返回错误 (HTTP {(int)response.StatusCode}): {error}");
        }

        var result = System.Text.Json.JsonSerializer.Deserialize<DeepseekResponse>(jsonResponse);
        if (result?.Choices is { Count: > 0 })
        {
            return result.Choices[0].Message.Content;
        }

        if (result?.Error != null)
        {
            throw new InvalidOperationException($"API 错误: {result.Error.Message}");
        }

        throw new InvalidOperationException("API 返回了空的响应内容。");
    }

    private static string TryParseError(string json)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var msg))
            {
                return msg.GetString() ?? json;
            }
        }
        catch { }
        return json;
    }
}
