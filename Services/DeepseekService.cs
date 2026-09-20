using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
    }

    /// <inheritdoc />
    public async Task<string> GenerateDailyReportWithPromptAsync(
        string fullPrompt,
        string apiKey,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var request = new DeepseekRequest
        {
            Model = "deepseek-v4-pro",
            Messages =
            [
                new DeepseekMessage { Role = "user", Content = fullPrompt }
            ],
            Temperature = 0.7,
            MaxTokens = 4096,
            Stream = true
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint);
        httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var jsonResponse = await response.Content.ReadAsStringAsync(ct);
            var error = TryParseError(jsonResponse);
            throw new HttpRequestException(
                $"Deepseek API 返回错误 (HTTP {(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var firstLine = await ReadNextNonEmptyLineAsync(reader, ct);
        if (firstLine is null)
            throw new InvalidOperationException("API 返回了空的响应内容。");

        if (firstLine.TrimStart().StartsWith('{'))
        {
            var json = firstLine + await reader.ReadToEndAsync(ct);
            return ParseCompleteResponse(json, progress);
        }

        var sb = new StringBuilder();
        ProcessSseLine(firstLine, sb, progress);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!ProcessSseLine(line, sb, progress))
                break;
        }

        var result = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("API 返回了空的响应内容。");

        return result;
    }

    private static async Task<string?> ReadNextNonEmptyLineAsync(StreamReader reader, CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return null;
            if (!string.IsNullOrWhiteSpace(line)) return line;
        }
    }

    /// <returns>false 表示流结束</returns>
    private static bool ProcessSseLine(string line, StringBuilder sb, IProgress<string>? progress)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            return true;

        var payload = line[5..].Trim();
        if (payload is "[DONE]")
            return false;

        DeepseekResponse? chunk;
        try
        {
            chunk = JsonSerializer.Deserialize<DeepseekResponse>(payload);
        }
        catch (JsonException)
        {
            return true;
        }

        if (chunk?.Error != null)
            throw new InvalidOperationException($"API 错误: {chunk.Error.Message}");

        var delta = chunk?.Choices is { Count: > 0 } ? chunk.Choices[0].Delta?.Content : null;
        if (string.IsNullOrEmpty(delta))
            return true;

        sb.Append(delta);
        progress?.Report(sb.ToString());
        return true;
    }

    private static string ParseCompleteResponse(string json, IProgress<string>? progress)
    {
        var result = JsonSerializer.Deserialize<DeepseekResponse>(json);
        if (result?.Choices is { Count: > 0 })
        {
            var content = result.Choices[0].Message.Content;
            if (!string.IsNullOrWhiteSpace(content))
            {
                progress?.Report(content);
                return content;
            }
        }

        if (result?.Error != null)
            throw new InvalidOperationException($"API 错误: {result.Error.Message}");

        throw new InvalidOperationException("API 返回了空的响应内容。");
    }

    private static string TryParseError(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
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
