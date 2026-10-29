using System.Threading.Channels;

namespace OrthancWadoProxy.Services;

public sealed class UploadIngestionQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        return channel.Writer.WriteAsync(uploadId, cancellationToken);
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken)
    {
        return channel.Reader.ReadAllAsync(cancellationToken);
    }
}
