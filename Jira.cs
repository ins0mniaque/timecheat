using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Timecheat;

[SuppressMessage("Microsoft.Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Future use")]
internal sealed class Jira : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public Jira(string baseUrl, string email, string apiToken)
    {
        _baseUrl = baseUrl.TrimEnd('/');

        var authToken = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{email}:{apiToken}")
        );

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl)
        };

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", authToken);

        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
    }

    /// <summary>
    /// Logs a specific number of hours on a Jira issue.
    /// </summary>
    public async Task LogHoursAsync(
        string projectKey,
        string issueKey,
        DateTime started,
        double hours,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hours);

        var issueIdOrKey = $"{projectKey}-{issueKey}";

        var payload = new WorklogRequest(
            TimeSpentSeconds: (int)(hours * 3600),
            Started: $"{started.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fff+0000}",
            Comment: new Comment(
                Type: "doc",
                Version: 1,
                Content:
                [
                    new Data(
                        Type: "paragraph",
                        Content:
                        [
                            new Data(
                                Type: "text",
                                Text: $"Logged {hours}h via API"
                            )
                        ]
                    )
                ]
            )
        );

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            new Uri($"/rest/api/3/issue/{issueIdOrKey}/worklog"),
            content,
            cancellationToken
        ).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new JiraException(
                $"Failed to log work on {issueIdOrKey}: {response.StatusCode}\n{error}"
            );
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record WorklogRequest(int TimeSpentSeconds, string Started, Comment Comment);
    private sealed record Comment(string Type, int Version, Data[] Content);
    private sealed record Data(string Type, string? Text = null, Data[]? Content = null);
}

internal sealed class JiraException : Exception
{
    public JiraException() { }
    public JiraException(string message) : base(message) { }
    public JiraException(string message, Exception innerException) : base(message, innerException) { }
}