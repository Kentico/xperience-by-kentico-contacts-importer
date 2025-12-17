using System.Collections.Concurrent;

namespace Kentico.Xperience.Contacts.Importer.Auxiliary;

/// <summary>
/// Single purpose stream - consumer (read) waits for promised data, implementation of stream abstract class is not complete and is not
/// needed (internal class).
/// </summary>
/// <inheritdoc />
internal sealed class AsynchronousStream(int streamWriteCountCache) : Stream
{
    private byte[]? currentBlock;
    private int currentBlockIndex;
    private readonly bool canRead = true;
    private readonly BlockingCollection<byte[]> blocks = new(streamWriteCountCache);


    /// <inheritdoc />
    public override bool CanTimeout => false;


    /// <inheritdoc />
    public override bool CanRead => canRead;


    /// <inheritdoc />
    public override bool CanSeek => false;


    /// <inheritdoc />
    public override bool CanWrite => true;


    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();


    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }


    public long TotalBytesWritten { get; private set; }


    public int WriteCount { get; private set; }


    public int CachedBlocks => blocks.Count;


    /// <inheritdoc />
    public override void Flush()
    {
    }


    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();


    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();


    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);

        int bytesRead = 0;
        while (true)
        {
            if (currentBlock != null)
            {
                int copy = Math.Min(count - bytesRead, currentBlock.Length - currentBlockIndex);
                Array.Copy(currentBlock, currentBlockIndex, buffer, offset + bytesRead, copy);
                currentBlockIndex += copy;
                bytesRead += copy;

                if (currentBlock.Length <= currentBlockIndex)
                {
                    currentBlock = null;
                    currentBlockIndex = 0;
                }

                if (bytesRead == count)
                {
                    return bytesRead;
                }
            }

            if (!blocks.TryTake(out currentBlock, Timeout.Infinite))
            {
                return bytesRead;
            }
        }
    }


    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);

        byte[] newBuf = new byte[count];
        Array.Copy(buffer, offset, newBuf, 0, count);
        blocks.Add(newBuf);
        TotalBytesWritten += count;
        WriteCount++;
    }


    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            blocks.Dispose();
        }
    }


    /// <inheritdoc />
    public override void Close()
    {
        CompleteWriting();

        base.Close();
    }


    public void CompleteWriting() => blocks.CompleteAdding();


    public bool TryCompleteWriting()
    {
        try
        {
            blocks.CompleteAdding();

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateBufferArgs(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("Buffer length minus offset cannot be less than count.");
        }
    }
}
