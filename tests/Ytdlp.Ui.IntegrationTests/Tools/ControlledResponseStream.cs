namespace Ytdlp.Ui.IntegrationTests.Tools;

public sealed class ControlledResponseStream(bool failAfterFirstChunk = false) : Stream
{
    private bool _started;
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool CancelObserved { get; private set; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            _started = true;
            buffer.Span[..1024].Fill(42);

            return 1024;
        }

        if (failAfterFirstChunk) throw new IOException("private stream failure");

        Blocked.TrySetResult();

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CancelObserved = true;

            throw;
        }

        return 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Disposed.TrySetResult();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
