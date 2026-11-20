using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace BehaviorLock;

/// <summary>
/// Counts the bytes of the response body on their way to the server. Nothing is kept in memory.
/// In Enforce mode it stops the first write that would pass the limit, so the client never gets more
/// bytes than the limit. It also stops every write of a request that the guard has already blocked.
/// </summary>
internal sealed class GuardedResponseBody(
    IHttpResponseBodyFeature inner, IAccessGuard guard, GuardContext context, long? limit) : Stream, IHttpResponseBodyFeature
{
    private PipeWriter? _writer;

    // Only Enforce mode stops a write. Audit mode reports the full size once, when the request has finished.
    private bool CanStopWrites => context.Mode == GuardMode.Enforce && limit is not null;

    /// <summary>The bytes that were passed on to the server.</summary>
    public long Written { get; private set; }

    public Stream Stream => this;

    // The writer of the server would pass this class by, so this one writes into it.
    public PipeWriter Writer => _writer ??= PipeWriter.Create(this, new StreamPipeWriterOptions(leaveOpen: true));

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Passes on the bytes that are still in the writer. Call it when the app has finished the request.</summary>
    public async Task FinishAsync()
    {
        if (_writer is not null)
        {
            await _writer.CompleteAsync();
        }
    }

    public void DisableBuffering() => inner.DisableBuffering();

    /// <summary>
    /// ASP.NET Core starts the response before it writes a text or a JSON body. A response that has started
    /// cannot become a 403 any more. So when this class can stop a write, the start waits for the first
    /// write or flush: the server starts the response there by itself.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        CanStopWrites ? Task.CompletedTask : inner.StartAsync(cancellationToken);

    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
        SendFileFallback.SendFileAsync(this, path, offset, count, cancellationToken);

    public async Task CompleteAsync()
    {
        await FinishAsync();
        await inner.CompleteAsync();
    }

    public override void Flush() => inner.Stream.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.Stream.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        Count(count);
        inner.Stream.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Count(buffer.Length);
        inner.Stream.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Count(count);
        return inner.Stream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Count(buffer.Length);
        return inner.Stream.WriteAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Runs before the bytes are passed on, so a blocked write sends nothing.</summary>
    private void Count(int count)
    {
        if (context.Violation is { } violation)
        {
            throw new GuardViolationException(violation);
        }

        var total = Written + count;

        if (CanStopWrites && total > limit)
        {
            guard.Check(ResourceAccess.ResponseSize(total));
        }

        Written = total;
    }
}
