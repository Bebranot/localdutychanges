using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Win32.SafeHandles;
using Robust.Packaging;
using Robust.Packaging.AssetProcessing;
using Robust.Packaging.AssetProcessing.Passes;
using Robust.Shared;
using Robust.Shared.Collections;
using Robust.Shared.ContentPack;
using Robust.Shared.Log;
using Robust.Shared.Utility;
using SpaceWizards.Sodium;

namespace Robust.Server.ServerStatus;

// Contains source logic for ACZ (Automatic Client Zip)
// This entails the following:
// * Automatic generation of client zip on development servers. ("Magic ACZ")
// * Loading of pre-built client zip on release servers. ("Hybrid ACZ")

internal sealed partial class StatusHost
{
    private IMagicAczProvider? _magicAczProvider;
    private IFullHybridAczProvider? _fullHybridAczProvider;

    // -- Dictionary<string, OnDemandFile> methods --

    private async Task<AczManifestInfo?> PrepareAczInner()
    {
        var streamCompression = _cfg.GetCVar(CVars.AczStreamCompress);
        var blobCompress = _cfg.GetCVar(CVars.AczBlobCompress);
        var blobCompressLevel = _cfg.GetCVar(CVars.AczBlobCompressLevel);
        var blobCompressSaveThresh = _cfg.GetCVar(CVars.AczBlobCompressSaveThreshold);
        var manifestCompress = _cfg.GetCVar(CVars.AczManifestCompress);
        var manifestCompressLevel = _cfg.GetCVar(CVars.AczManifestCompressLevel);

        // Stream compression disables individual compression.
        blobCompress &= !streamCompression;

        var manifestResult = await CalcManifestData(
            blobCompress,
            blobCompressLevel,
            blobCompressSaveThresh);

        if (manifestResult == null)
            return null;

        var (manifestData, manifestEntries, blobStore) = manifestResult!.Value;

        var manifestHash = CryptoGenericHashBlake2B.Hash(32, manifestData, ReadOnlySpan<byte>.Empty);
        var manifestHashString = Convert.ToHexString(manifestHash);

        _aczSawmill.Debug("ACZ Manifest hash: {ManifestHash}", manifestHashString);

        if (manifestCompress)
        {
            _aczSawmill.Debug("Compressing ACZ manifest at level {ManifestCompressLevel}", manifestCompressLevel);

            var beforeSize = manifestData.Length;
            var compressBuffer = ZStd.CompressBound(manifestData.Length);
            var compressed = ArrayPool<byte>.Shared.Rent(compressBuffer);

            var size = ZStd.Compress(compressed, manifestData, manifestCompressLevel);

            manifestData = compressed[..size];

            ArrayPool<byte>.Shared.Return(compressed);

            _aczSawmill.Debug(
                "ACZ manifest compression: {ManifestSize} -> {ManifestSizeCompressed} ({ManifestSizeRatio} ratio)",
                beforeSize, manifestData.Length, manifestData.Length / (float) beforeSize);
        }

        return new AczManifestInfo(
            manifestData,
            manifestCompress,
            manifestHashString,
            blobStore,
            manifestEntries,
            blobCompress);
    }

    private async Task<(byte[] manifestData, AczManifestEntry[] entries, AczBlobStore blobStore)?> CalcManifestData(
        bool blobCompress,
        int blobCompressLevel,
        int blobCompressSaveThresh)
    {
        var logger = new PackageLoggerSawmill(_aczPackagingSawmill);
        using var writerPass = new AssetPassAczWriter(
            blobCompress,
            blobCompressLevel,
            blobCompressSaveThresh,
            GetAczBlobPath(),
            _aczSawmill);

        var result = await SourceAczDictionaryViaFile(writerPass, logger) ||
                     await SourceAczViaMagic(writerPass, logger);

        if (!result)
            return null;

        await writerPass.FinishedTask;

        return (writerPass.ManifestContent!, writerPass.ManifestEntries!, writerPass.TakeBlobStore());
    }

    /// <summary>
    /// Where to spill the ACZ blob so it doesn't have to sit in the managed heap. Null falls back
    /// to the in-heap store, e.g. when the data dir is virtual.
    /// </summary>
    private string? GetAczBlobPath()
    {
        var rootDir = _deps.Resolve<IResourceManager>().UserData.RootDir;
        if (rootDir == null)
            return null;

        return Path.Combine(rootDir, "acz_blob.bin");
    }

    private async Task<bool> SourceAczDictionaryViaFile(AssetPass pass, IPackageLogger logger)
    {
        var path = PathHelpers.ExecutableRelativeFile("Content.Client.zip");
        if (!FileHelper.TryOpenFileRead(path, out var fileStream))
            return false;

        _aczSawmill.Info($"StatusHost found client zip: {path}");
        using var zip = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false);
        await SourceAczDictionaryViaZipStream(zip, pass, logger);
        return true;
    }

    private async Task SourceAczDictionaryViaZipStream(ZipArchive zip, AssetPass outputPass, IPackageLogger logger)
    {
        var inputPass = new AssetPassPipe { Parallelize = true, Name = "HybridPackageInput" };

        if (_fullHybridAczProvider is { } fullProvider)
        {
            logger.Debug("Using Full Hybrid ACZ with custom provider");

            await fullProvider.Package(inputPass, outputPass, logger, CancellationToken.None);
        }
        else
        {
            logger.Debug("Using standard Hybrid ACZ without custom provider");

            outputPass.AddDependency(inputPass);
            AssetGraph.CalculateGraph(new []{inputPass, outputPass}, logger);
        }

        logger.Verbose("Injecting Hybrid ACZ files");

        foreach (var entry in zip.Entries)
        {
            // Ignore directory entries.
            if (entry.Name == "")
                continue;

            using var stream = entry.Open();
            var file = new AssetFileMemory(entry.FullName, stream.CopyToArray());
            inputPass.InjectFile(file);
        }

        inputPass.InjectFinished();
    }

    private async Task<bool> SourceAczViaMagic(AssetPass pass, IPackageLogger logger)
    {
        _aczSawmill.Debug("Using Magic ACZ");
        var provider = _magicAczProvider;
        if (provider == null)
        {
            _aczSawmill.Verbose("Using default magic ACZ provider");
            // Default provider
            var (binFolderPath, assemblyNames) = ("Content.Client", new[] { "Content.Client", "Content.Shared" });

            var info = new DefaultMagicAczInfo(binFolderPath, assemblyNames);
            provider = new DefaultMagicAczProvider(info, _deps);
        }

        await provider.Package(pass, logger, default);
        return true;
    }

    // -- Information Input --

    public void SetMagicAczProvider(IMagicAczProvider provider)
    {
        _magicAczProvider = provider;
    }

    public void SetFullHybridAczProvider(IFullHybridAczProvider provider)
    {
        _fullHybridAczProvider = provider;
    }

    private sealed class AssetPassAczWriter : AssetPass, IDisposable
    {
        private readonly object _lock = new();

        private readonly bool _blobCompress;
        private readonly int _blobCompressLevel;
        private readonly int _blobCompressSaveThresh;
        private readonly ISawmill _sawmill;

        /// <summary>File we spill the blob into, or null when falling back to the heap.</summary>
        private readonly SafeFileHandle? _blobHandle;
        private readonly string? _blobPath;

        /// <summary>In-heap fallback, only used when <see cref="_blobHandle"/> is null.</summary>
        private readonly SequenceMemoryStream? _blobMemory;

        private long _blobPosition;

        /// <summary>
        /// Content hash -> where that exact content already sits in the blob. A content set that has
        /// the same file under several names (very common with ported audio) would otherwise get one
        /// copy per name, and every copy costs memory or disk for the life of the process.
        /// </summary>
        private readonly Dictionary<string, AczManifestEntry> _dedupe = new();
        private int _dedupeHits;

        private readonly ObjectPool<ZStdCompressionContext> _compressStreamPool =
            ObjectPool.Create<ZStdCompressionContext>();

        private ValueList<InProgressAczManifestInfo> _infos;

        public byte[]? ManifestContent;
        public AczManifestEntry[]? ManifestEntries;

        private AczBlobStore? _blobStore;
        private bool _blobStoreTaken;

        public AssetPassAczWriter(
            bool blobCompress,
            int blobCompressLevel,
            int blobCompressSaveThresh,
            string? blobPath,
            ISawmill sawmill)
        {
            _blobCompress = blobCompress;
            _blobCompressLevel = blobCompressLevel;
            _blobCompressSaveThresh = blobCompressSaveThresh;
            _sawmill = sawmill;

            if (blobPath != null)
            {
                try
                {
                    // DeleteOnClose rather than a manual File.Delete: the blob is multiple gigabytes
                    // and nothing outside this process ever reads it, so let the OS drop it the
                    // moment the last handle closes - including when the server is killed hard,
                    // which used to leave the whole blob sitting in the data dir until the next run.
                    _blobHandle = File.OpenHandle(
                        blobPath,
                        FileMode.Create,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        FileOptions.DeleteOnClose);
                    _blobPath = blobPath;
                }
                catch (Exception e)
                {
                    _sawmill.Warning(
                        $"Couldn't open '{blobPath}' for the ACZ blob, keeping it in memory instead: {e.Message}");
                }
            }

            if (_blobHandle == null)
                _blobMemory = new SequenceMemoryStream();
        }

        /// <summary>
        /// Hands the finished blob store to the caller, who becomes responsible for disposing it.
        /// </summary>
        public AczBlobStore TakeBlobStore()
        {
            DebugTools.AssertNotNull(_blobStore);
            _blobStoreTaken = true;
            return _blobStore!;
        }

        protected override AssetFileAcceptResult AcceptFile(AssetFile file)
        {
            // Logger?.Verbose(file.Path);
            var entryHash = new byte[256 / 8];

            byte[]? dataPool = null;
            Span<byte> data;

            if (file is AssetFileMemory mem)
            {
                data = mem.Memory;
            }
            else
            {
                using var fs = file.Open();

                dataPool = ArrayPool<byte>.Shared.Rent((int) fs.Length);
                data = dataPool.AsSpan(0, (int)fs.Length);

                fs.ReadToEnd(data);
            }

            CryptoGenericHashBlake2B.Hash(entryHash, data, ReadOnlySpan<byte>.Empty);
            var hashString = Convert.ToHexString(entryHash);

            // Same bytes as a file we already wrote: point at the existing copy instead of appending
            // a second one. Compression is deterministic, so the stored entry is byte-identical.
            lock (_lock)
            {
                if (_dedupe.TryGetValue(hashString, out var existing))
                {
                    _infos.Add(new InProgressAczManifestInfo(existing, file.Path, entryHash));
                    _dedupeHits += 1;

                    if (dataPool != null)
                        ArrayPool<byte>.Shared.Return(dataPool);

                    return AssetFileAcceptResult.Consumed;
                }
            }

            ReadOnlySpan<byte> toWrite;

            byte[]? compressBuffer = null;
            if (_blobCompress)
            {
                var compressCtx = _compressStreamPool.Get();
                compressBuffer = ArrayPool<byte>.Shared.Rent(ZStd.CompressBound(data.Length));
                var comprLength = compressCtx.Compress(compressBuffer, data, _blobCompressLevel);

                _compressStreamPool.Return(compressCtx);

                // See if compression was worth it.
                if (comprLength + _blobCompressSaveThresh < data.Length)
                {
                    // Worth it
                    toWrite = compressBuffer.AsSpan(0, comprLength);
                }
                else
                {
                    // Compression not worth it, just send an uncompressed blob instead.
                    toWrite = data;
                }
            }
            else
            {
                toWrite = data;
            }

            lock (_lock)
            {
                var streamPos = _blobPosition;
                WriteBlob(toWrite, streamPos);
                _blobPosition += toWrite.Length;

                var info = new AczManifestEntry(
                    data.Length,
                    streamPos,
                    toWrite.Length == data.Length ? 0 : toWrite.Length);

                _dedupe[hashString] = info;
                _infos.Add(new InProgressAczManifestInfo(info, file.Path, entryHash));
            }

            if (compressBuffer != null)
                ArrayPool<byte>.Shared.Return(compressBuffer);

            if (dataPool != null)
                ArrayPool<byte>.Shared.Return(dataPool);

            return AssetFileAcceptResult.Consumed;
        }

        /// <summary>Must be called under <see cref="_lock"/>.</summary>
        private void WriteBlob(ReadOnlySpan<byte> data, long position)
        {
            if (_blobHandle != null)
                RandomAccess.Write(_blobHandle, data, position);
            else
                _blobMemory!.Write(data);
        }

        protected override void AcceptFinished()
        {
            _infos.Sort(OnDemandFilePathComparer.Instance);

            var manifestStream = new MemoryStream();
            using var manifestWriter = new StreamWriter(manifestStream, EncodingHelpers.UTF8);
            manifestWriter.Write("Robust Content Manifest 1\n");

            var manifestEntries = new AczManifestEntry[_infos.Count];

            for (var i = 0; i < _infos.Count; i++)
            {
                var info = _infos[i];
                manifestWriter.Write($"{Convert.ToHexString(info.Hash)} {info.Path}\n");

                manifestEntries[i] = info.Entry;
            }

            manifestWriter.Flush();

            ManifestContent = manifestStream.ToArray();
            ManifestEntries = manifestEntries;

            if (_blobHandle != null)
            {
                _blobStore = new AczFileBlobStore(_blobHandle, _blobPosition);
                _sawmill.Info(
                    "ACZ blob written to '{BlobPath}': {BlobSize} bytes over {FileCount} files ({DedupeHits} deduplicated)",
                    _blobPath, _blobPosition, _infos.Count, _dedupeHits);
            }
            else
            {
                _blobStore = new AczMemoryBlobStore(_blobMemory!.AsSequence);
                _sawmill.Info(
                    "ACZ blob kept in memory: {BlobSize} bytes over {FileCount} files ({DedupeHits} deduplicated)",
                    _blobPosition, _infos.Count, _dedupeHits);
            }
        }

        public void Dispose()
        {
            // _compressStreamPool is actually a DisposableObjectPool, which is an internal type.
            (_compressStreamPool as IDisposable)?.Dispose();

            if (_blobStoreTaken)
                return;

            // Nobody took the store, so the ACZ attempt failed and the file must not outlive this
            // pass. _blobStore only exists once AcceptFinished ran; when the pipeline found no ACZ
            // source at all (CalcManifestData's `if (!result) return null`) or threw, it is still
            // null and the raw handle is the only thing holding the file open. Leaking it mattered:
            // the file is opened FileShare.None, so the next attempt in the same process could not
            // reopen it and silently fell back to the multi-gigabyte in-heap blob.
            if (_blobStore != null)
            {
                _blobStore.Dispose();
                return;
            }

            _blobHandle?.Dispose();
        }
    }

    private sealed record InProgressAczManifestInfo(AczManifestEntry Entry, string Path, byte[] Hash);

    private sealed class OnDemandFilePathComparer : IComparer<InProgressAczManifestInfo>
    {
        public static readonly OnDemandFilePathComparer Instance = new();

        public int Compare(InProgressAczManifestInfo? x, InProgressAczManifestInfo? y)
        {
            return string.Compare(x!.Path, y!.Path, StringComparison.Ordinal);
        }
    }
}
