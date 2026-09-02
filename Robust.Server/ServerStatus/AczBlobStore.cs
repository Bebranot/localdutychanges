using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Robust.Server.ServerStatus;

/// <summary>
/// Storage for the ACZ blob: the concatenated (usually zstd-compressed) contents of every file the
/// launcher can download from this server.
/// </summary>
/// <remarks>
/// <para>
/// This blob is the size of the entire client content set — on a content-heavy server that is
/// multiple gigabytes — and it is built once and then kept for the rest of the process lifetime.
/// Where it lives therefore dominates the server's memory footprint.
/// </para>
/// <para>
/// <see cref="AczFileBlobStore"/> keeps it in a file on disk, so it costs no private commit and the
/// OS is free to drop the pages when they are not being served.
/// <see cref="AczMemoryBlobStore"/> is the old behaviour of holding it all in the managed heap; it
/// stays as a fallback for when the file cannot be created.
/// </para>
/// </remarks>
internal abstract class AczBlobStore : IDisposable
{
    /// <summary>Total size of the blob.</summary>
    public abstract long Length { get; }

    /// <summary>
    /// Writes a slice of the blob to <paramref name="destination"/>. Must tolerate being called
    /// concurrently — several launchers can be downloading at once.
    /// </summary>
    public abstract ValueTask WriteSliceAsync(
        Stream destination,
        long offset,
        int length,
        CancellationToken cancel = default);

    public virtual void Dispose()
    {
    }
}

/// <summary>
/// Blob held entirely in the managed heap.
/// </summary>
internal sealed class AczMemoryBlobStore : AczBlobStore
{
    private readonly ReadOnlySequence<byte> _data;

    public AczMemoryBlobStore(ReadOnlySequence<byte> data)
    {
        _data = data;
    }

    public override long Length => _data.Length;

    public override async ValueTask WriteSliceAsync(
        Stream destination,
        long offset,
        int length,
        CancellationToken cancel = default)
    {
        // Segmented rather than a single array: a >2 GB blob cannot be one byte[].
        foreach (var segment in _data.Slice(offset, length))
        {
            await destination.WriteAsync(segment, cancel);
        }
    }
}

/// <summary>
/// Blob backed by a file on disk, read with positional reads so concurrent downloads don't need
/// their own handles.
/// </summary>
internal sealed class AczFileBlobStore : AczBlobStore
{
    private const int CopyBufferSize = 128 * 1024;

    private readonly SafeFileHandle _handle;
    private readonly long _length;

    /// <summary>
    /// Guards <see cref="_readers"/> and <see cref="_disposed"/>. The handle must outlive every
    /// download currently reading through it: <see cref="StatusHost.InvalidateAcz"/> can fire at any
    /// time (a CVar change is enough), and closing the handle under an in-flight launcher would fail
    /// that download with an <see cref="ObjectDisposedException"/>. Upstream did not have this
    /// hazard because the blob was a <c>byte[]</c> that the request kept alive by reference.
    /// </summary>
    private readonly object _lock = new();

    private int _readers;
    private bool _disposed;

    public AczFileBlobStore(SafeFileHandle handle, long length)
    {
        _handle = handle;
        _length = length;
    }

    public override long Length => _length;

    public override async ValueTask WriteSliceAsync(
        Stream destination,
        long offset,
        int length,
        CancellationToken cancel = default)
    {
        if (!TryAcquire())
            throw new ObjectDisposedException(nameof(AczFileBlobStore), "The ACZ blob was invalidated.");

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(length, CopyBufferSize));
        try
        {
            var position = offset;
            var remaining = length;

            while (remaining > 0)
            {
                var toRead = Math.Min(remaining, buffer.Length);
                var read = await RandomAccess.ReadAsync(_handle, buffer.AsMemory(0, toRead), position, cancel);

                if (read == 0)
                    throw new EndOfStreamException($"ACZ blob file ended early at offset {position}.");

                await destination.WriteAsync(buffer.AsMemory(0, read), cancel);

                position += read;
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Release();
        }
    }

    private bool TryAcquire()
    {
        lock (_lock)
        {
            if (_disposed)
                return false;

            _readers += 1;
            return true;
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            _readers -= 1;

            // Whoever leaves last after a Dispose does the actual close.
            if (_disposed && _readers == 0)
                _handle.Dispose();
        }
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;

            // The file itself is FileOptions.DeleteOnClose, so closing the handle removes it.
            if (_readers == 0)
                _handle.Dispose();
        }
    }
}
