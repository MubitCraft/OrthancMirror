namespace OrthancWadoProxy.Configuration;

public sealed class ChunkedUploadOptions
{
    public string TempDirectory { get; set; } = string.Empty;
    public long MaxFileBytes { get; set; } = 40L * 1024 * 1024 * 1024;
    public long MaxOrthancCStoreBytes { get; set; } = 128L * 1024 * 1024;
    public int MaxChunkBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxActiveUploads { get; set; } = 8;
    public int IncompleteUploadHours { get; set; } = 24;
    public int CompletedUploadRetentionHours { get; set; } = 72;
}

public sealed class OrthancDicomOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4242;
    public string CallingAet { get; set; } = "WADOPROXY";
    public string CalledAet { get; set; } = "ORTHANC";
}

public sealed class WsiDicomizerOptions
{
    public string ExecutablePath { get; set; } = string.Empty;
    public bool GeneratePyramid { get; set; } = true;
    public bool SmoothPyramid { get; set; } = true;
    public int Threads { get; set; }
    public int TimeoutHours { get; set; } = 72;
}
