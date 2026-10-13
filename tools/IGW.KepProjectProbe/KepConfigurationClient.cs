using System.Net.Http.Headers;
using System.Text;

namespace IGW.KepProjectProbe;

public sealed class KepConfigurationClient : IDisposable
{
    private readonly HttpClient _client;

    public KepConfigurationClient(Uri baseAddress, string? username, string? password)
    {
        _client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrEmpty(username))
        {
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password ?? ""}"));
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    public async Task<KepProjectResponse> ExportAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            "config/v1/project", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"kep_project_export_failed:{(int)response.StatusCode}:{content}");
        return new KepProjectResponse(content,
            response.Headers.Server.ToString(), (int)response.StatusCode);
    }

    public async Task<KepLoadResponse> LoadAsync(string json, CancellationToken cancellationToken)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _client.PutAsync(
            "config/v1/project/services/JsonProjectLoad", content, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new KepLoadResponse(response.IsSuccessStatusCode, (int)response.StatusCode, body);
    }

    public void Dispose() => _client.Dispose();
}

public sealed record KepProjectResponse(string Json, string Server, int StatusCode);
public sealed record KepLoadResponse(bool Success, int StatusCode, string Body);
