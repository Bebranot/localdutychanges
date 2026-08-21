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
    private readonly string _path;
    private readonly long _length;

    public AczFileBlobStore(SafeFileHandle handle, string path, long length)
    {
        _handle = handle;
        _path = path;
        _length = length;
    }

    public override long Length => _length;

    public override async ValueTask WriteSliceAsync(
        Stream destination,
        long offset,
        int length,
        CancellationToken cancel = default)
    {
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
        }
    }

    public override void Dispose()
    {
        _handle.Dispose();

        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a stale blob file is harmless, the next run overwrites it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
