namespace OrthancWadoProxy.Services;

public sealed class UploadNotFoundException() : Exception("Upload was not found.");

public sealed class UploadConflictException(string message) : Exception(message);

public sealed class UploadValidationException(string message) : Exception(message);

public sealed class UploadIngestionLimitException(string message) : Exception(message);

public sealed class UploadStorageException(string message) : Exception(message);

public sealed class WsiPipelineUnavailableException(string message) : Exception(message);

public sealed class WsiConversionException(string message) : Exception(message);
