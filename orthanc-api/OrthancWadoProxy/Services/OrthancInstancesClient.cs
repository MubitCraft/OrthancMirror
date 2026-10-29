using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OrthancWadoProxy.Configuration;

namespace OrthancWadoProxy.Services;

public sealed record OrthancInstanceResult(string InstanceId, string SeriesId, string Status);

public sealed class OrthancInstancesClient(
    HttpClient httpClient,
    IOptions<ChunkedUploadOptions> uploadOptions)
{
    private readonly long maxInstanceBytes = uploadOptions.Value.MaxOrthancCStoreBytes;

    public async Task<OrthancInstanceResult> StoreFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("A generated DICOM instance is missing.");
        }

        if (fileInfo.Length <= 0 || fileInfo.Length > maxInstanceBytes)
        {
            throw new UploadIngestionLimitException(
                $"Generated DICOM instance size {fileInfo.Length} is outside the configured Orthanc ingestion limit of {maxInstanceBytes} bytes.");
        }

        await using var file = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new StreamContent(file, 1024 * 1024);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/dicom");
        using var request = new HttpRequestMessage(HttpMethod.Post, "instances")
        {
            Content = content
        };
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Orthanc REST ingestion failed with HTTP status {(int)response.StatusCode}.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            responseStream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var instanceId = GetRequiredString(root, "ID");
        var seriesId = GetRequiredString(root, "ParentSeries");
        var status = GetRequiredString(root, "Status");
        if (status is not ("Success" or "AlreadyStored"))
        {
            throw new InvalidOperationException("Orthanc did not confirm storage of the generated instance.");
        }

        return new OrthancInstanceResult(instanceId, seriesId, status);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            return property.GetString()!;
        }

        throw new InvalidDataException($"Orthanc REST response omitted {propertyName}.");
    }
}
