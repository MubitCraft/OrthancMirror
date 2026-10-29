namespace OrthancWadoProxy.Models;

public sealed record CreateChunkedUploadRequest(
    string FileName,
    long FileSizeBytes,
    bool ConvertToDicomWsi = false);

public sealed record ChunkedUploadStatus(
    Guid UploadId,
    long FileSizeBytes,
    long NextOffset,
    string Status,
    string? Sha256,
    string? SopInstanceUid,
    string? Error,
    string? OrthancSeriesId = null,
    int? InstancesStored = null,
    int? InstancesTotal = null);

public sealed record WsiIngestionProgress(
    bool ConversionCompleted,
    int InstancesStored,
    int? InstancesTotal,
    string? OrthancSeriesId);
