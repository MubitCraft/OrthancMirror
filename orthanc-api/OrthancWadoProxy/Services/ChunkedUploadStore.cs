using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OrthancWadoProxy.Configuration;
using OrthancWadoProxy.Models;

namespace OrthancWadoProxy.Services;

public sealed class ChunkedUploadStore
{
    private const string Receiving = "receiving";
    private const string Queued = "queued";
    private const string Processing = "processing";
    private const string Failed = "failed";
    private const string Staged = "staged";
    private const string Completed = "completed";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string rootDirectory;
    private readonly ChunkedUploadOptions options;
    private readonly UploadIngestionQueue queue;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();
    private readonly SemaphoreSlim createLock = new(1, 1);

    public ChunkedUploadStore(IOptions<ChunkedUploadOptions> options, UploadIngestionQueue queue)
    {
        this.options = options.Value;
        this.queue = queue;

        if (this.options.MaxFileBytes <= 0 || this.options.MaxOrthancCStoreBytes <= 0 ||
            this.options.MaxChunkBytes <= 0 || this.options.MaxChunkBytes > 10 * 1024 * 1024 ||
            this.options.MaxActiveUploads <= 0 || this.options.IncompleteUploadHours <= 0 ||
            this.options.CompletedUploadRetentionHours <= 0)
        {
            throw new InvalidOperationException("Chunked upload limits must be positive.");
        }

        var configuredRoot = string.IsNullOrWhiteSpace(this.options.TempDirectory)
            ? Path.Combine(Path.GetTempPath(), "OrthancWadoProxy", "Uploads")
            : this.options.TempDirectory;
        rootDirectory = Path.GetFullPath(configuredRoot);
        Directory.CreateDirectory(rootDirectory);
    }

    public async Task<ChunkedUploadStatus> CreateAsync(
        string owner,
        string fileName,
        long fileSizeBytes,
        bool convertToDicomWsi,
        CancellationToken cancellationToken)
    {
        if (fileSizeBytes <= 0 || fileSizeBytes > options.MaxFileBytes)
        {
            throw new UploadValidationException("The file size is outside the configured upload limit.");
        }

        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName || fileName.Length > 255)
        {
            throw new UploadValidationException("A simple file name is required.");
        }

        var extension = Path.GetExtension(fileName);
        if (convertToDicomWsi && !string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase))
        {
            throw new UploadValidationException(
                "WSI conversion currently accepts tiled TIFF sources (.tif or .tiff) supported by OrthancWSIDicomizer.");
        }

        await createLock.WaitAsync(cancellationToken);
        try
        {
            await DeleteExpiredUploadsAsync(cancellationToken);
            var activeCount = await CountActiveUploadsAsync(cancellationToken);
            if (activeCount >= options.MaxActiveUploads)
            {
                throw new UploadConflictException("The active upload limit has been reached.");
            }

            var driveRoot = Path.GetPathRoot(rootDirectory);
            var requiredFreeSpace = convertToDicomWsi ? checked(fileSizeBytes * 2) : fileSizeBytes;
            var outstandingReservations = await GetOutstandingDiskReservationBytesAsync(cancellationToken);
            if (string.IsNullOrEmpty(driveRoot) ||
                new DriveInfo(driveRoot).AvailableFreeSpace < checked(requiredFreeSpace + outstandingReservations))
            {
                throw new UploadStorageException(
                    convertToDicomWsi
                        ? "There is not enough free space for the source and derived WSI data; reserve at least twice the source size."
                        : "There is not enough free space for this upload.");
            }

            var uploadId = Guid.NewGuid();
            var directory = GetUploadDirectory(uploadId);
            Directory.CreateDirectory(directory);

            var manifest = new UploadManifest
            {
                UploadId = uploadId,
                Owner = owner,
                FileName = fileName,
                FileSizeBytes = fileSizeBytes,
                ConvertToDicomWsi = convertToDicomWsi,
                PayloadFileName = convertToDicomWsi
                    ? "source" + extension.ToLowerInvariant()
                    : "instance.part",
                NextOffset = 0,
                Status = Receiving,
                LastUpdatedUtc = DateTimeOffset.UtcNow
            };

            try
            {
                await using (var payload = new FileStream(
                    GetPayloadPath(manifest), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await payload.FlushAsync(cancellationToken);
                }

                await SaveManifestAsync(manifest, cancellationToken);
            }
            catch
            {
                Directory.Delete(directory, recursive: true);
                throw;
            }

            return ToStatus(manifest);
        }
        finally
        {
            createLock.Release();
        }
    }

    public async Task<bool> IsWsiUploadForProcessingAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(uploadId, cancellationToken);
            if (manifest.Status != Processing)
            {
                throw new UploadConflictException("The upload is not being processed.");
            }

            return manifest.ConvertToDicomWsi;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ChunkedUploadStatus> AppendChunkAsync(
        Guid uploadId,
        string owner,
        long offset,
        long contentLength,
        string? chunkSha256,
        Stream body,
        CancellationToken cancellationToken)
    {
        if (contentLength <= 0 || contentLength > options.MaxChunkBytes)
        {
            throw new UploadValidationException("Each chunk must be between 1 byte and the configured chunk limit.");
        }

        var expectedChunkHash = ParseSha256(chunkSha256);

        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadOwnedManifestAsync(uploadId, owner, cancellationToken);
            if (manifest.Status != Receiving)
            {
                throw new UploadConflictException("This upload is no longer accepting chunks.");
            }

            if (offset != manifest.NextOffset)
            {
                throw new UploadConflictException($"Expected upload offset {manifest.NextOffset}.");
            }

            if (contentLength > manifest.FileSizeBytes - manifest.NextOffset)
            {
                throw new UploadValidationException("The chunk exceeds the declared file size.");
            }

            var payloadPath = GetPayloadPath(manifest);
            await using (var payload = new FileStream(
                payloadPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (payload.Length < manifest.NextOffset)
                {
                    throw new UploadStorageException("The staged upload file is shorter than its recorded offset.");
                }

                if (payload.Length > manifest.NextOffset)
                {
                    payload.SetLength(manifest.NextOffset);
                }

                payload.Position = manifest.NextOffset;
                try
                {
                    var actualChunkHash = await CopyExactlyAndHashAsync(body, payload, contentLength, cancellationToken);
                    if (!CryptographicOperations.FixedTimeEquals(actualChunkHash, expectedChunkHash))
                    {
                        throw new UploadValidationException("The chunk checksum does not match.");
                    }

                    await payload.FlushAsync(cancellationToken);
                }
                catch
                {
                    payload.SetLength(manifest.NextOffset);
                    await payload.FlushAsync(CancellationToken.None);
                    throw;
                }
            }

            manifest.NextOffset += contentLength;
            manifest.LastUpdatedUtc = DateTimeOffset.UtcNow;
            await SaveManifestAsync(manifest, cancellationToken);
            return ToStatus(manifest);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ChunkedUploadStatus> GetStatusAsync(Guid uploadId, string owner, CancellationToken cancellationToken)
    {
        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return ToStatus(await LoadOwnedManifestAsync(uploadId, owner, cancellationToken));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ChunkedUploadStatus> CompleteAsync(Guid uploadId, string owner, CancellationToken cancellationToken)
    {
        var gate = GetLock(uploadId);
        UploadManifest manifest;
        var shouldQueue = false;

        await gate.WaitAsync(cancellationToken);
        try
        {
            manifest = await LoadOwnedManifestAsync(uploadId, owner, cancellationToken);
            if (manifest.Status is Completed or Queued or Processing)
            {
                return ToStatus(manifest);
            }

            if (manifest.Status is not (Receiving or Failed or Staged) || manifest.NextOffset != manifest.FileSizeBytes)
            {
                throw new UploadConflictException("All declared bytes must be uploaded before completion.");
            }

            if (!manifest.ConvertToDicomWsi && manifest.FileSizeBytes > options.MaxOrthancCStoreBytes)
            {
                var reason = $"Staged only. Current Orthanc core ingestion is memory-bound above {options.MaxOrthancCStoreBytes} bytes.";
                manifest.Status = Staged;
                manifest.Error = reason;
                manifest.LastUpdatedUtc = DateTimeOffset.UtcNow;
                await SaveManifestAsync(manifest, cancellationToken);
                throw new UploadIngestionLimitException(reason);
            }

            var payloadPath = GetPayloadPath(manifest);
            var info = new FileInfo(payloadPath);
            if (!info.Exists || info.Length != manifest.FileSizeBytes)
            {
                throw new UploadStorageException("The staged upload size does not match its manifest.");
            }

            await using var stream = new FileStream(
                payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
            manifest.Sha256 = Convert.ToHexString(actualHash);
            manifest.Status = Queued;
            manifest.Error = null;
            manifest.LastUpdatedUtc = DateTimeOffset.UtcNow;
            await SaveManifestAsync(manifest, cancellationToken);
            shouldQueue = true;
        }
        finally
        {
            gate.Release();
        }

        if (shouldQueue)
        {
            await queue.EnqueueAsync(uploadId, CancellationToken.None);
        }

        return ToStatus(manifest);
    }

    public async Task<bool> TryStartProcessingAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(uploadId, cancellationToken);
            if (manifest.Status != Queued)
            {
                return false;
            }

            manifest.Status = Processing;
            manifest.LastUpdatedUtc = DateTimeOffset.UtcNow;
            await SaveManifestAsync(manifest, cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> GetPayloadPathForProcessingAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(uploadId, cancellationToken);
            if (manifest.Status != Processing)
            {
                throw new UploadConflictException("The upload is not being processed.");
            }

            return GetPayloadPath(manifest);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> GetDerivedDirectoryForProcessingAsync(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(uploadId, cancellationToken);
            if (manifest.Status != Processing || !manifest.ConvertToDicomWsi)
            {
                throw new UploadConflictException("The upload is not a WSI conversion job in progress.");
            }

            return GetDerivedDirectory(uploadId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WsiIngestionProgress> GetWsiIngestionProgressAsync(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(uploadId, cancellationToken);
            if (manifest.Status != Processing || !manifest.ConvertToDicomWsi)
            {
                throw new UploadConflictException("The upload is not a WSI conversion job in progress.");
            }

            return new WsiIngestionProgress(
                manifest.WsiConversionCompleted,
                manifest.InstancesStored,
                manifest.InstancesTotal,
                manifest.OrthancSeriesId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkWsiConversionCompletedAsync(
        Guid uploadId,
        int instancesTotal,
        CancellationToken cancellationToken)
    {
        if (instancesTotal <= 0)
        {
            throw new UploadValidationException("The WSI converter did not produce any DICOM instances.");
        }

        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(uploadId, cancellationToken);
            if (manifest.Status != Processing || !manifest.ConvertToDicomWsi)
            {
                throw new UploadConflictException("The upload is not a WSI conversion job in progress.");
            }

            manifest.WsiConversionCompleted = true;
            manifest.InstancesTotal = instancesTotal;
            manifest.LastUpdatedUtc = DateTimeOffset.UtcNow;
            await SaveManifestAsync(manifest, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkDerivedInstanceStoredAsync(
        Guid uploadId,
        string seriesId,
        CancellationToken cancellationToken)
    {
        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(uploadId, cancellationToken);
            if (manifest.Status != Processing || !manifest.ConvertToDicomWsi ||
                !manifest.WsiConversionCompleted || manifest.InstancesTotal is null ||
                manifest.InstancesStored >= manifest.InstancesTotal)
            {
                throw new UploadConflictException("The WSI instance progress is not valid.");
            }

            if (manifest.OrthancSeriesId is not null &&
                !string.Equals(manifest.OrthancSeriesId, seriesId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The converter produced instances from more than one Orthanc series.");
            }

            manifest.OrthancSeriesId = seriesId;
            manifest.InstancesStored++;
            manifest.LastUpdatedUtc = DateTimeOffset.UtcNow;
            await SaveManifestAsync(manifest, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkCompletedAsync(
        Guid uploadId,
        string? sopInstanceUid,
        string? orthancSeriesId,
        int? instancesStored,
        int? instancesTotal,
        CancellationToken cancellationToken)
    {
        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(uploadId, cancellationToken);
            if (manifest.ConvertToDicomWsi &&
                (!manifest.WsiConversionCompleted || manifest.InstancesTotal != manifest.InstancesStored))
            {
                throw new UploadConflictException("All converted WSI instances must be stored before completion.");
            }

            manifest.Status = Completed;
            manifest.SopInstanceUid = sopInstanceUid;
            manifest.OrthancSeriesId = orthancSeriesId ?? manifest.OrthancSeriesId;
            manifest.InstancesStored = instancesStored ?? manifest.InstancesStored;
            manifest.InstancesTotal = instancesTotal ?? manifest.InstancesTotal;
            manifest.Error = null;
            manifest.LastUpdatedUtc = DateTimeOffset.UtcNow;
            await SaveManifestAsync(manifest, cancellationToken);
            File.Delete(GetPayloadPath(manifest));
            var derivedDirectory = GetDerivedDirectory(uploadId);
            if (Directory.Exists(derivedDirectory))
            {
                Directory.Delete(derivedDirectory, recursive: true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkFailedAsync(Guid uploadId, string error, CancellationToken cancellationToken)
    {
        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(uploadId, cancellationToken);
            if (manifest.Status == Processing)
            {
                manifest.Status = Failed;
                manifest.Error = error;
                manifest.LastUpdatedUtc = DateTimeOffset.UtcNow;
                await SaveManifestAsync(manifest, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<Guid>> RecoverPendingUploadsAsync(CancellationToken cancellationToken)
    {
        var result = new List<Guid>();
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadManifestAsync(path, cancellationToken);
            if (manifest.Status is Queued or Processing)
            {
                if (manifest.Status == Processing)
                {
                    manifest.Status = Queued;
                    manifest.LastUpdatedUtc = DateTimeOffset.UtcNow;
                    await SaveManifestAsync(manifest, cancellationToken);
                }

                result.Add(manifest.UploadId);
            }
        }

        return result;
    }

    public async Task DeleteAsync(Guid uploadId, string owner, CancellationToken cancellationToken)
    {
        var gate = GetLock(uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadOwnedManifestAsync(uploadId, owner, cancellationToken);
            if (manifest.Status is Queued or Processing)
            {
                throw new UploadConflictException("An upload cannot be cancelled while Orthanc is ingesting it.");
            }

            Directory.Delete(GetUploadDirectory(uploadId), recursive: true);
        }
        finally
        {
            gate.Release();
            locks.TryRemove(uploadId, out _);
        }
    }

    public async Task DeleteExpiredUploadsAsync(CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uploadId = Guid.Parse(Path.GetFileName(Path.GetDirectoryName(path)!));
            var gate = GetLock(uploadId);
            await gate.WaitAsync(cancellationToken);
            try
            {
                var manifest = await ReadManifestAsync(path, cancellationToken);
                var retention = manifest.Status == Completed
                    ? TimeSpan.FromHours(options.CompletedUploadRetentionHours)
                    : TimeSpan.FromHours(options.IncompleteUploadHours);

                if (manifest.Status is not (Queued or Processing) &&
                    DateTimeOffset.UtcNow - manifest.LastUpdatedUtc > retention)
                {
                    Directory.Delete(GetUploadDirectory(uploadId), recursive: true);
                    locks.TryRemove(uploadId, out _);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<int> CountActiveUploadsAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadManifestAsync(path, cancellationToken);
            if (manifest.Status != Completed)
            {
                count++;
            }
        }

        return count;
    }

    private async Task<long> GetOutstandingDiskReservationBytesAsync(CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadManifestAsync(path, cancellationToken);
            if (manifest.Status == Completed)
            {
                continue;
            }

            if (manifest.ConvertToDicomWsi)
            {
                if (manifest.Status == Receiving)
                {
                    total = checked(total + Math.Max(0, manifest.FileSizeBytes - manifest.NextOffset));
                }

                if (!manifest.WsiConversionCompleted)
                {
                    total = checked(total + manifest.FileSizeBytes);
                }
            }
            else if (manifest.Status == Receiving)
            {
                total = checked(total + Math.Max(0, manifest.FileSizeBytes - manifest.NextOffset));
            }
        }

        return total;
    }

    private async Task<UploadManifest> LoadOwnedManifestAsync(Guid uploadId, string owner, CancellationToken cancellationToken)
    {
        var manifest = await LoadManifestAsync(uploadId, cancellationToken);
        if (!string.Equals(manifest.Owner, owner, StringComparison.Ordinal))
        {
            throw new UploadNotFoundException();
        }

        return manifest;
    }

    private async Task<UploadManifest> LoadManifestAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var path = GetManifestPath(uploadId);
        if (!File.Exists(path))
        {
            throw new UploadNotFoundException();
        }

        return await ReadManifestAsync(path, cancellationToken);
    }

    private static async Task<UploadManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<UploadManifest>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("The upload manifest is empty.");
    }

    private async Task SaveManifestAsync(UploadManifest manifest, CancellationToken cancellationToken)
    {
        var path = GetManifestPath(manifest.UploadId);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private async Task<byte[]> CopyExactlyAndHashAsync(
        Stream source,
        Stream destination,
        long bytesToCopy,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var remaining = bytesToCopy;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                {
                    throw new UploadValidationException("The request body ended before the declared chunk length.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private SemaphoreSlim GetLock(Guid uploadId)
    {
        return locks.GetOrAdd(uploadId, static _ => new SemaphoreSlim(1, 1));
    }

    private string GetUploadDirectory(Guid uploadId) => Path.Combine(rootDirectory, uploadId.ToString("N"));

    private string GetManifestPath(Guid uploadId) => Path.Combine(GetUploadDirectory(uploadId), "manifest.json");

    private string GetPayloadPath(UploadManifest manifest) =>
        Path.Combine(GetUploadDirectory(manifest.UploadId), manifest.PayloadFileName);

    private string GetDerivedDirectory(Guid uploadId) => Path.Combine(GetUploadDirectory(uploadId), "derived");

    private static ChunkedUploadStatus ToStatus(UploadManifest manifest)
    {
        return new ChunkedUploadStatus(
            manifest.UploadId,
            manifest.FileSizeBytes,
            manifest.NextOffset,
            manifest.Status,
            string.IsNullOrEmpty(manifest.Sha256) ? null : manifest.Sha256.ToLowerInvariant(),
            manifest.SopInstanceUid,
            manifest.Error,
            manifest.OrthancSeriesId,
            manifest.ConvertToDicomWsi ? manifest.InstancesStored : null,
            manifest.ConvertToDicomWsi ? manifest.InstancesTotal : null);
    }

    private static byte[] ParseSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UploadValidationException("Upload-Chunk-Sha256 must be a 64-character hexadecimal value.");
        }

        byte[] result;
        try
        {
            result = Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            throw new UploadValidationException("Upload-Chunk-Sha256 must be a 64-character hexadecimal value.");
        }

        if (result.Length == 32)
        {
            return result;
        }

        throw new UploadValidationException("Upload-Chunk-Sha256 must be a 64-character hexadecimal value.");
    }

    private sealed class UploadManifest
    {
        public Guid UploadId { get; set; }
        public string Owner { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public bool ConvertToDicomWsi { get; set; }
        public string PayloadFileName { get; set; } = "instance.part";
        public string Sha256 { get; set; } = string.Empty;
        public long NextOffset { get; set; }
        public string Status { get; set; } = Receiving;
        public string? SopInstanceUid { get; set; }
        public string? OrthancSeriesId { get; set; }
        public bool WsiConversionCompleted { get; set; }
        public int InstancesStored { get; set; }
        public int? InstancesTotal { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset LastUpdatedUtc { get; set; }
    }
}
