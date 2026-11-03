using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Ytdlp.Ui.IntegrationTests.Tools;

public sealed class RequestBodyProbe : IStartupFilter
{
    public int Reads { get; private set; }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
    {
        application.Use(async (context, following) =>
            {
                context.Request.Body = new CountingStream(context.Request.Body, () => Reads++);
                await following(context);
            }
        );

        next(application);
    };

    private sealed class CountingStream(Stream inner, Action read) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            read();

            return inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            read();

            return inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            read();

            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
