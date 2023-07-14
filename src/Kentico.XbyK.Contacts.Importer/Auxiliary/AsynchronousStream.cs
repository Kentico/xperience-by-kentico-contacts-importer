namespace Kentico.Xperience.Contacts.Importer.Auxiliary;

using System.Collections.Concurrent;

/// <summary>
/// single purpose stream - consumer (read) waits for promised data, implementation of stream abstract class is not complete and is not needed (internal class) 
/// </summary>
internal sealed class AsynchronousStream: Stream
{
    private readonly BlockingCollection<byte[]> _blocks;
    private byte[]? _currentBlock;
    private int _currentBlockIndex;

    public int CachedBlocks => _blocks.Count;

    /// <inheritdoc />
    public AsynchronousStream(int streamWriteCountCache)
    {
        _blocks = new BlockingCollection<byte[]>(streamWriteCountCache);
    }

    /// <inheritdoc />
    public override bool CanTimeout
    {
        get { return false; }
    }

    private bool canRead = true;

    /// <inheritdoc />
    public override bool CanRead
    {
        get => canRead;
    }

    /// <inheritdoc />
    public override bool CanSeek
    {
        get { return false; }
    }

    /// <inheritdoc />
    public override bool CanWrite
    {
        get { return true; }
    }

    /// <inheritdoc />
    public override long Length
    {
        get { throw new NotSupportedException(); }
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }


    public long TotalBytesWritten { get; private set; }

    public int WriteCount { get; private set; }

    public override long Position
    {
        get { throw new NotSupportedException(); }
        set { throw new NotSupportedException(); }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);

        var bytesRead = 0;
        while (true)
        {
            if (_currentBlock != null)
            {
                var copy = Math.Min(count - bytesRead, _currentBlock.Length - _currentBlockIndex);
                Array.Copy(_currentBlock, _currentBlockIndex, buffer, offset + bytesRead, copy);
                _currentBlockIndex += copy;
                bytesRead += copy;

                if (_currentBlock.Length <= _currentBlockIndex)
                {
                    _currentBlock = null;
                    _currentBlockIndex = 0;
                }

                if (bytesRead == count)
                    return bytesRead;
            }

            if (!_blocks.TryTake(out _currentBlock, Timeout.Infinite))
            {
                return bytesRead;
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);

        var newBuf = new byte[count];
        Array.Copy(buffer, offset, newBuf, 0, count);
        _blocks.Add(newBuf);
        TotalBytesWritten += count;
        WriteCount++;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _blocks.Dispose();
        }
    }

    public override void Close()
    {
        CompleteWriting();
        base.Close();
    }

    public void CompleteWriting()
    {
        _blocks.CompleteAdding();
    }
    
    public bool TryCompleteWriting()
    {
        try
        {
            _blocks.CompleteAdding();
            return true;
        }
        catch (Exception ex)
        {
            return false;
        }
    }

    private static void ValidateBufferArgs(byte[] buffer, int offset, int count)
    {
        if (buffer == null)
            throw new ArgumentNullException("buffer");
        if (offset < 0)
            throw new ArgumentOutOfRangeException("offset");
        if (count < 0)
            throw new ArgumentOutOfRangeException("count");
        if (buffer.Length - offset < count)
            throw new ArgumentException("buffer.Length - offset < count");
    }
}