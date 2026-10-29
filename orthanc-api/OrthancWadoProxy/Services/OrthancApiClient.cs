using System.Net.Http.Json;
using System.Text.Json;

namespace OrthancWadoProxy.Services;

public sealed class OrthancApiClient(HttpClient httpClient)
{
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("system", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public Task<JsonElement> GetStudiesAsync(CancellationToken cancellationToken)
    {
        return GetJsonAsync("studies", cancellationToken);
    }

    public Task<JsonElement> GetStudyAsync(string studyId, CancellationToken cancellationToken)
    {
        return GetJsonAsync($"studies/{Uri.EscapeDataString(studyId)}", cancellationToken);
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
    }
}
