using Microsoft.Extensions.Options;
using OrthancWadoProxy.Configuration;

namespace OrthancWadoProxy.Services;

public sealed class UploadIngestionWorker(
    UploadIngestionQueue queue,
    ChunkedUploadStore uploads,
    DicomStoreClient dicomStore,
    WsiDicomizerClient wsiDicomizer,
    OrthancInstancesClient orthancInstances,
    IOptions<ChunkedUploadOptions> uploadOptions,
    ILogger<UploadIngestionWorker> logger) : BackgroundService
{
    private readonly long maxOrthancInstanceBytes = uploadOptions.Value.MaxOrthancCStoreBytes;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var uploadId in await uploads.RecoverPendingUploadsAsync(stoppingToken))
        {
            await queue.EnqueueAsync(uploadId, stoppingToken);
        }

        await foreach (var uploadId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (!await uploads.TryStartProcessingAsync(uploadId, stoppingToken))
                {
                    continue;
                }

                var payloadPath = await uploads.GetPayloadPathForProcessingAsync(uploadId, stoppingToken);
                if (await uploads.IsWsiUploadForProcessingAsync(uploadId, stoppingToken))
                {
                    await ProcessWsiUploadAsync(uploadId, payloadPath, stoppingToken);
                }
                else
                {
                    var sopInstanceUid = await dicomStore.StoreFileAsync(payloadPath, stoppingToken);
                    await uploads.MarkCompletedAsync(
                        uploadId, sopInstanceUid, null, null, null, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (UploadValidationException exception)
            {
                logger.LogWarning(exception, "Upload {UploadId} failed DICOM validation.", uploadId);
                await uploads.MarkFailedAsync(uploadId, exception.Message, stoppingToken);
            }
            catch (UploadIngestionLimitException exception)
            {
                logger.LogWarning(exception, "Upload {UploadId} exceeded the Orthanc instance size limit.", uploadId);
                await uploads.MarkFailedAsync(uploadId, exception.Message, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "DICOM conversion or ingestion failed for upload {UploadId}.", uploadId);
                await uploads.MarkFailedAsync(
                    uploadId,
                    "The upload could not be converted or accepted by Orthanc. Retry completion or cancel the upload.",
                    stoppingToken);
            }
        }
    }

    private async Task ProcessWsiUploadAsync(
        Guid uploadId,
        string payloadPath,
        CancellationToken cancellationToken)
    {
        var derivedDirectory = await uploads.GetDerivedDirectoryForProcessingAsync(uploadId, cancellationToken);
        var progress = await uploads.GetWsiIngestionProgressAsync(uploadId, cancellationToken);

        if (!progress.ConversionCompleted)
        {
            await wsiDicomizer.ConvertAsync(payloadPath, derivedDirectory, cancellationToken);
            var generatedFiles = GetGeneratedFiles(derivedDirectory);
            ValidateGeneratedFiles(generatedFiles);
            await uploads.MarkWsiConversionCompletedAsync(
                uploadId, generatedFiles.Length, cancellationToken);
            progress = await uploads.GetWsiIngestionProgressAsync(uploadId, cancellationToken);
        }

        var files = GetGeneratedFiles(derivedDirectory);
        if (progress.InstancesTotal != files.Length || progress.InstancesStored > files.Length)
        {
            throw new InvalidDataException("The persisted WSI conversion output does not match its upload manifest.");
        }

        for (var index = progress.InstancesStored; index < files.Length; index++)
        {
            var stored = await orthancInstances.StoreFileAsync(files[index], cancellationToken);
            await uploads.MarkDerivedInstanceStoredAsync(uploadId, stored.SeriesId, cancellationToken);
        }

        progress = await uploads.GetWsiIngestionProgressAsync(uploadId, cancellationToken);
        await uploads.MarkCompletedAsync(
            uploadId,
            null,
            progress.OrthancSeriesId,
            progress.InstancesStored,
            progress.InstancesTotal,
            cancellationToken);
    }

    private string[] GetGeneratedFiles(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.dcm", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
    }

    private void ValidateGeneratedFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            throw new WsiConversionException("OrthancWSIDicomizer produced no DICOM instances.");
        }

        foreach (var path in files)
        {
            var length = new FileInfo(path).Length;
            if (length <= 0 || length > maxOrthancInstanceBytes)
            {
                throw new UploadIngestionLimitException(
                    $"A generated DICOM WSI instance is {length} bytes; the configured Orthanc ingestion limit is {maxOrthancInstanceBytes} bytes.");
            }
        }
    }
}

public sealed class UploadCleanupWorker(
    ChunkedUploadStore uploads,
    ILogger<UploadCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await uploads.DeleteExpiredUploadsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException exception)
            {
                logger.LogError(exception, "Could not clean expired chunked uploads.");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
