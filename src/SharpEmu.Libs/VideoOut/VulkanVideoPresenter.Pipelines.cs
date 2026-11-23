// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Diagnostics;
using System.Collections.Concurrent;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

// This partial creates the graphics and compute pipelines of the shader cache and the presentation pipeline.
internal static unsafe partial class VulkanVideoPresenter
{
    private const string PushDescriptorExtensionName = "VK_KHR_push_descriptor";

    // One created pipeline with the layout it binds and the description its rectangle-list variants derive from.
    private sealed class RenderPipelineEntry
    {
        public ulong Id;
        public Pipeline Pipeline;
        public PipelineLayout Layout;
        public DescriptorSetLayout SetLayout;
        public DescriptorSetDemand Demand;
        public bool UsesPushDescriptors;
        public bool UsesBindlessHeap;
        public GraphicsPipelineDescription? Description;
        public Pipeline StripVariant;
        public Pipeline ListVariant;
        public Pipeline RectangleVariant;
        public ulong ProfileVertexHash;
        public ulong ProfilePixelHash;
        public ulong ProfileComputeHash;

        // Laid out for pipeline libraries, and linked from them unless built whole: SetLayout is the vertex stage's set 0,
        // PixelSetLayout the pixel stage's set 2.
        public bool LinkedFromLibraries;
        public DescriptorSetLayout PixelSetLayout;
        public DescriptorSetDemand PixelDemand;
        public string VertexSetSignature = "";
        public string PixelSetSignature = "";

        public bool RectangleList => Description is { Tessellation: null, StaticParameters.Topology: PrimitiveTopology.PatchList };
    }

    private sealed partial class Presenter : IShaderPipelineHost
    {
        internal bool _supportsTessellation;
        private const string FullscreenBarycentricVertexSpirv =
            "AwIjBwAAAQALAAgAMgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACAAAAAAABAAAAG1haW4AAAAADQAAABoAAAApAAAAAwADAAIAAADCAQAABQAEAAQAAABtYWluAAAAAAUABgALAAAAZ2xfUGVyVmVydGV4AAAAAAYABgALAAAAAAAAAGdsX1Bvc2l0aW9uAAYABwALAAAAAQAAAGdsX1BvaW50U2l6ZQAAAAAGAAcACwAAAAIAAABnbF9DbGlwRGlzdGFuY2UABgAHAAsAAAADAAAAZ2xfQ3VsbERpc3RhbmNlAAUAAwANAAAAAAAAAAUABgAaAAAAZ2xfVmVydGV4SW5kZXgAAAUABQAdAAAAaW5kZXhhYmxlAAAABQAFACkAAABiYXJ5Y2VudHJpYwAFAAUALwAAAGluZGV4YWJsZQAAAEcAAwALAAAAAgAAAEgABQALAAAAAAAAAAsAAAAAAAAASAAFAAsAAAABAAAACwAAAAEAAABIAAUACwAAAAIAAAALAAAAAwAAAEgABQALAAAAAwAAAAsAAAAEAAAARwAEABoAAAALAAAAKgAAAEcABAApAAAAHgAAAAAAAAATAAIAAgAAACEAAwADAAAAAgAAABYAAwAGAAAAIAAAABcABAAHAAAABgAAAAQAAAAVAAQACAAAACAAAAAAAAAAKwAEAAgAAAAJAAAAAQAAABwABAAKAAAABgAAAAkAAAAeAAYACwAAAAcAAAAGAAAACgAAAAoAAAAgAAQADAAAAAMAAAALAAAAOwAEAAwAAAANAAAAAwAAABUABAAOAAAAIAAAAAEAAAArAAQADgAAAA8AAAAAAAAAFwAEABAAAAAGAAAAAgAAACsABAAIAAAAEQAAAAMAAAAcAAQAEgAAABAAAAARAAAAKwAEAAYAAAATAAAAAACAvywABQAQAAAAFAAAABMAAAATAAAAKwAEAAYAAAAVAAAAAABAQCwABQAQAAAAFgAAABUAAAATAAAALAAFABAAAAAXAAAAEwAAABUAAAAsAAYAEgAAABgAAAAUAAAAFgAAABcAAAAgAAQAGQAAAAEAAAAOAAAAOwAEABkAAAAaAAAAAQAAACAABAAcAAAABwAAABIAAAAgAAQAHgAAAAcAAAAQAAAAKwAEAAYAAAAhAAAAAAAAACsABAAGAAAAIgAAAAAAgD8gAAQAJgAAAAMAAAAHAAAAIAAEACgAAAADAAAAEAAAADsABAAoAAAAKQAAAAMAAAAsAAUAEAAAACoAAAAiAAAAIQAAACwABQAQAAAAKwAAACEAAAAiAAAALAAFABAAAAAsAAAAIQAAACEAAAAsAAYAEgAAAC0AAAAqAAAAKwAAACwAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAOwAEABwAAAAdAAAABwAAADsABAAcAAAALwAAAAcAAAA9AAQADgAAABsAAAAaAAAAPgADAB0AAAAYAAAAQQAFAB4AAAAfAAAAHQAAABsAAAA9AAQAEAAAACAAAAAfAAAAUQAFAAYAAAAjAAAAIAAAAAAAAABRAAUABgAAACQAAAAgAAAAAQAAAFAABwAHAAAAJQAAACMAAAAkAAAAIQAAACIAAABBAAUAJgAAACcAAAANAAAADwAAAD4AAwAnAAAAJQAAAD0ABAAOAAAALgAAABoAAAA+AAMALwAAAC0AAABBAAUAHgAAADAAAAAvAAAALgAAAD0ABAAQAAAAMQAAADAAAAA+AAMAKQAAADEAAAD9AAEAOAABAA==";

        private const string FullscreenBarycentricFragmentSpirv =
            "AwIjBwAAAQALAAgAEgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAABAAAAG1haW4AAAAACQAAAAwAAAAQAAMABAAAAAcAAAADAAMAAgAAAMIBAAAFAAQABAAAAG1haW4AAAAABQAFAAkAAABvdXRDb2xvcgAAAAAFAAUADAAAAGJhcnljZW50cmljAEcABAAJAAAAHgAAAAAAAABHAAQADAAAAB4AAAAAAAAAEwACAAIAAAAhAAMAAwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAIAAEAAgAAAADAAAABwAAADsABAAIAAAACQAAAAMAAAAXAAQACgAAAAYAAAACAAAAIAAEAAsAAAABAAAACgAAADsABAALAAAADAAAAAEAAAArAAQABgAAAA4AAAAAAAAANgAFAAIAAAAEAAAAAAAAAAMAAAD4AAIABQAAAD0ABAAKAAAADQAAAAwAAABRAAUABgAAAA8AAAANAAAAAAAAAFEABQAGAAAAEAAAAA0AAAABAAAAUAAHAAcAAAARAAAADwAAABAAAAAOAAAADgAAAD4AAwAJAAAAEQAAAP0AAQA4AAEA";

        private const uint PushConstantBytes = PushData.ByteSize;
        private static int _shaderModuleDumpSequence;
        private static int _vertexSwizzleLines;
        private static int _vertexOutOfBoundsLines;

        // Graphics and compute resolvers can publish modules concurrently; pipeline workers read their metadata too.
        private readonly ConcurrentDictionary<ulong, ShaderModule> _shaderModules = new();
        private readonly ConcurrentDictionary<ulong, int> _shaderModuleSpirvBytes = new();
        private readonly ConcurrentDictionary<ulong, byte[]> _rectangleStageCode = new();
        private readonly ConcurrentDictionary<ulong, string> _shaderModuleCacheIdentities = new();
        private long _pipelineCreationMilliseconds;
        internal KhrPushDescriptor _pushDescriptorApi = null!;
        private uint _maxPushDescriptors;
        private SampleCountFlags _noAttachmentSampleCounts;
        internal DescriptorHeap _descriptorHeap = null!;
        internal SharpEmu.Libs.Gpu.Buffers.ContentUploadArena _contentUploads = null!;

        uint IShaderPipelineHost.MaxPushDescriptors => _maxPushDescriptors;

        StableImageDescriptorLimits IShaderPipelineHost.ComputeImageDescriptorLimits =>
            ShaderDescriptorLimits.ComputeImageLimits(_shaderDescriptorLimits);

        StableImageDescriptorLimits IShaderPipelineHost.GraphicsImageDescriptorLimits =>
            ShaderDescriptorLimits.GraphicsImageLimits(_shaderDescriptorLimits);

        bool IShaderPipelineHost.TryCopyImage(in ImageRequest source, in ImageRequest destination) =>
            _imageCache.TryCopyImage(source, destination);
        bool IShaderPipelineHost.TableBlocks => true;

        uint[] IShaderPipelineHost.NormalizeImageDescriptor(IShaderFrontend frontend, ReadOnlySpan<uint> words,
            ShaderStage stage, ulong shaderHash, string source)
        {
            if (OnResolveWorker)
            {
                return frontend.NormalizeImageForResolution(words, stage, shaderHash, source, false);
            }

            if (OnCommandThread)
            {
                return NormalizeImageOnCommandThread(frontend, words, stage, shaderHash, source);
            }

            return frontend.NormalizeImageForResolution(words, stage, shaderHash, source, true);
        }

        private uint[] NormalizeImageOnCommandThread(IShaderFrontend frontend, ReadOnlySpan<uint> words,
            ShaderStage stage, ulong shaderHash, string source)
        {
            try
            {
                return frontend.NormalizeImageForResolution(words, stage, shaderHash, source, false);
            }
            catch (SerialResolveRequiredException)
            {
            }
            return NormalizeImageOnRenderThread(frontend, words.ToArray(), stage, shaderHash, source);
        }

        private uint[] NormalizeImageOnRenderThread(IShaderFrontend frontend, uint[] words, ShaderStage stage, ulong shaderHash, string source) =>
            _commandThread!.Run(() => frontend.NormalizeImageForResolution(words, stage, shaderHash, source, true), nameof(IShaderPipelineHost.NormalizeImageDescriptor));

        // Wave64 compute runs natively when the device's subgroup is that wide; smaller devices emulate it.
        bool IShaderPipelineHost.ComputeWave64SubgroupNative =>
            Volatile.Read(ref _nativeSubgroupSize) >= 64 && (_canRequireComputeSubgroup64 || Volatile.Read(ref _nativeSubgroupSize) == 64);

        bool IShaderPipelineHost.ComputeWave64Supported => Volatile.Read(ref _nativeSubgroupSize) >= 64;

        bool IShaderPipelineHost.ComputeRunsOn32LaneSubgroups(ComputeInputInfo input) =>
            _fixedComputeSubgroup32 || RequiresComputeSubgroup32(input);

        // Wave32 compute maps a guest wave to 32-lane host subgroups, and a 64-lane host subgroup
        // (AMD's default) left lanes 32..63 inactive, so those pipelines are pinned to 32. Wave64
        // compute is pinned to 64 instead when the host runs it natively.
        private const uint RdnaSubgroupSize = 32;
        private bool _fixedComputeSubgroup32;
        private bool _canRequireComputeSubgroup32;
        private uint _maxComputeWorkgroupSubgroups;

        private const uint Wave64SubgroupSize = 64;
        private bool _canRequireComputeSubgroup64;

        // A native wave64 translation reads one guest wave as one host subgroup, so the driver must
        // not pick 32-lane subgroups for it (RDNA drivers may do so for compute).
        private bool RequiresComputeSubgroup64(ComputeInputInfo input)
        {
            return _canRequireComputeSubgroup64 && input.WaveSize == 64 &&
                   ((IShaderPipelineHost)this).ComputeWave64SubgroupNative;
        }

        private bool RequiresComputeSubgroup32(ComputeInputInfo input)
        {
            var invocations = (ulong)Math.Max(input.ThreadsX, 1) * Math.Max(input.ThreadsY, 1) * Math.Max(input.ThreadsZ, 1);
            return _canRequireComputeSubgroup32 &&
                   !(input.WaveSize == 64 && ((IShaderPipelineHost)this).ComputeWave64SubgroupNative) &&
                   invocations <= (ulong)_maxComputeWorkgroupSubgroups * RdnaSubgroupSize;
        }

        // The subgroup width a compute pipeline must be pinned to, or 0 to leave it to the driver.
        private uint ComputeRequiredSubgroupSize(ComputeInputInfo input) =>
            RequiresComputeSubgroup64(input) ? Wave64SubgroupSize
            : RequiresComputeSubgroup32(input) ? RdnaSubgroupSize
            : 0u;

        bool IShaderPipelineHost.GraphicsSubgroupOperationsEnabled => GraphicsSubgroupOperationsEnabled;

        bool IShaderPipelineHost.SharedInt64AtomicsEnabled => SharedInt64AtomicsEnabled;
        bool IShaderPipelineHost.BufferInt64AtomicsEnabled => BufferInt64AtomicsEnabled;
        bool IShaderPipelineHost.ImageInt64AtomicsEnabled => ImageInt64AtomicsEnabled;
        bool IShaderPipelineHost.ShaderDeviceClockEnabled => ShaderDeviceClockEnabled;
        bool IShaderPipelineHost.ShaderSignedZeroInfNanPreserveFloat32Supported =>
            _supportsShaderSignedZeroInfNanPreserveFloat32;
        bool IShaderPipelineHost.RobustBufferAccess2Enabled => _robustBufferAccess2Enabled;
        bool IShaderPipelineHost.NativeFloat16Conversions => _nativeFloat16Conversions;
        // NVIDIA's compiler rejects the elided-EXEC wave64 compute module with NVVM error 3.
        bool IShaderPipelineHost.ExecGuardElisionEnabled => _physicalDeviceVendorId != NvidiaVendorId;
        bool IShaderPipelineHost.PerVertexPixelInputsSupported => _supportsPerVertexPixelInputs;
        bool IShaderPipelineHost.ClipDistanceEnabled => _supportsShaderClipDistance;
        bool IShaderPipelineHost.SupportsDualSourceBlend => _supportsDualSourceBlend;

        bool IShaderPipelineHost.MeshShadersSupported => _supportsMeshShader;

        bool IShaderPipelineHost.SupportsBindlessHeap => _bindlessHeapSupported;

        MeshShaderLimits IShaderPipelineHost.MeshLimits => _meshShaderLimits;
        uint IShaderPipelineHost.MeshSubgroupSize => checked((uint)Volatile.Read(ref _nativeSubgroupSize));
        bool IShaderPipelineHost.ShaderDrawParametersSupported => _supportsShaderDrawParameters;

        RenderHostLimits IShaderPipelineHost.Limits => _renderHostLimits;

        SampleCountFlags IShaderPipelineHost.NoAttachmentSampleCounts => _noAttachmentSampleCounts;

        // A range the GPU wrote is downloaded first, so the word is what the guest CPU would read.
        public bool TryReadGuestWord(ulong address, out uint word)
        {
            if (OnResolveWorker)
            {
                return ReadWordOnResolveWorker(address, out word);
            }

            if (OnCommandThread)
            {
                return ReadWordOnCommandThread(address, out word);
            }

            using var profile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.GuestRead);
            word = 0;
            CompletePendingPublicationsForResourceRead(address, sizeof(uint));
            var synchronized = false;
            if (IsCleanReadPage(address, sizeof(uint)))
            {
                Span<byte> cachedBytes = stackalloc byte[sizeof(uint)];
                if (TryReadAliasBytes(address, cachedBytes))
                {
                    word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cachedBytes);
                    return true;
                }
            }
            else
            {
                // Resource planning can inspect a dynamic descriptor before the draw has
                // supplied a valid guest address.  Do not pass an invalid range to the
                // page tracker: it treats that as an emulator invariant violation and
                // terminates the process.  A failed read lets the materializer reject or
                // specialize the source normally.
                if (!_guestMemory.CanRead(address, sizeof(uint)))
                {
                    return false;
                }

                if (_bufferCache.HasGpuDirtyBytes(address, sizeof(uint)))
                {
                    if (Diagnostics.GpuReadTrace.Enabled)
                    {
                        Diagnostics.GpuReadTrace.Record(address, ResourceMaterializationCache.ReadingTable);
                    }

                    if (!_bufferCache.TrySynchronizeCpuRead(address, sizeof(uint),
                            SharpEmu.HLE.GuestMemory.GuestMemoryProfile.ReadbackSource.ShaderResourceRead))
                    {
                        return false;
                    }

                    synchronized = true;
                }
                else
                {
                    NoteCleanReadPage(address, sizeof(uint));
                }
            }

            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!_guestMemory.TryRead(address, bytes))
            {
                return false;
            }

            word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (Diagnostics.GpuReadTrace.Enabled && synchronized)
            {
                Diagnostics.GpuReadTrace.RecordValue(address, word);
            }

            return true;
        }

        // A held-back command write or label lands in guest memory only when its publication runs; a resource
        // read behind it in the command stream must see it, as the command processor's own reads do.
        private void CompletePendingPublicationsForResourceRead(ulong address, ulong size)
        {
            if (!HasPendingMemoryPublication(address, size)) return;
            if (OnResolveWorker)
                throw new Gpu.GpuCommands.SerialResolveRequiredException("pending publication read");
            CompleteOverlappingPublications(address, size);
        }

        public string DescribeCleanRead(ulong address) =>
            $"readable={_guestMemory.CanRead(address, sizeof(uint))} gpu_dirty_bytes={_bufferCache.HasGpuDirtyBytes(address, sizeof(uint))} " +
            $"gpu_dirty_pages={_bufferCache.HasGpuDirtyPages(address, sizeof(uint))} image_modified={_imageCache.HasGpuModifiedImageBytes(address, sizeof(uint))} " +
            $"pending_publication={HasPendingMemoryPublication(address, sizeof(uint))} buffer={_bufferCache.IsRegionRegistered(address, sizeof(uint))}";

        // Refused while a GPU buffer or image write may still own the range.
        public bool TryReadCleanGuestWord(ulong address, out uint word)
        {
            using var profile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.CleanGuestRead);
            word = 0;
            CompletePendingPublicationsForResourceRead(address, sizeof(uint));
            if (!_guestMemory.CanRead(address, sizeof(uint)))
            {
                return RefuseOnTicketWorker("unreadable");
            }

            if (OnResolveWorker)
            {
                if (IsGpuOwnedForWorkerCleanRead(address, sizeof(uint)))
                {
                    return RefuseOnTicketWorker("gpu owned");
                }

                if (IsPendingForWorker(address, sizeof(uint)))
                {
                    return RefuseOnTicketWorker("pending");
                }

                Span<byte> cleanBytes = stackalloc byte[sizeof(uint)];
                if (!_commandThread!.ReadOverlayBefore(address, cleanBytes, _resolveWorker!.Sequence))
                {
                    return RefuseOnTicketWorker("overlay");
                }

                _resolveWorker.Reads.Add(address, cleanBytes, clean: true);
                word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cleanBytes);
                return true;
            }

            if (IsGpuOwnedForCleanRead(address, sizeof(uint)) || (OnCommandThread && IsPendingOnCommandThread(address, sizeof(uint))))
            {
                return false;
            }

            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!(OnCommandThread ? _commandThread!.ReadOverlay(address, bytes) : _guestMemory.TryRead(address, bytes)))
            {
                return false;
            }

            word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return true;
        }


        public bool CpuWriteNeedsGpuData(ulong address, ulong size) =>
            address != 0 && size != 0 && new SharpEmu.HLE.GpuMemory.GuestSpan(address, size).IsValid &&
            ((OnCommandThread && IsPendingOnCommandThread(address, size)) ||
             (_bufferCache.MayHaveGpuDirtyPages(address, size) && _bufferCache.HasGpuDirtyPages(address, size)) ||
             _imageCache.HasGpuModifiedImageBytes(address, size));

        private bool IsGpuOwnedForCleanRead(ulong address, ulong size) =>
            (_bufferCache.MayHaveGpuDirtyPages(address, size) && _bufferCache.HasGpuDirtyPages(address, size)) ||
            _bufferCache.HasGpuDirtyBytes(address, size) ||
            _imageCache.HasGpuModifiedImageBytes(address, size);

        private bool IsGpuOwnedForResidentRead(ulong address, ulong size, bool clean) =>
            _bufferCache.HasGpuDirtyBytes(address, size) ||
            (clean && (_bufferCache.HasGpuDirtyPages(address, size) || _imageCache.HasGpuModifiedImageBytes(address, size)));

        private bool ReadWordOnResolveWorker(ulong address, out uint word)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!ReadOnResolveWorker(address, bytes))
            {
                word = 0;
                return false;
            }

            word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return true;
        }

        private static readonly bool DirectPageReads = Environment.GetEnvironmentVariable("SHARPEMU_DIRECT_PAGE_READS") != "0";

        private bool ReadResidentBytesOnResolveWorker(ulong address, Span<byte> destination, bool clean)
        {
            var size = (ulong)destination.Length;
            var worker = _resolveWorker!;
            if (!clean && DirectPageReads && TryGetCleanReadPage(address, size, out var directPage, out _) && directPage == worker.DirectPage &&
                ReferenceEquals(_guestBacking.BackingAliasSnapshot, worker.DirectSnapshot) && _guestBacking.TryEnterBackingAliasAccess())
            {
                try
                {
                    new ReadOnlySpan<byte>((byte*)(worker.DirectAlias + (nint)(address - directPage)), destination.Length).CopyTo(destination);
                }
                finally
                {
                    _guestBacking.ExitBackingAliasAccess();
                }

                worker.Reads.Add(address, destination);
                return true;
            }

            CompletePendingPublicationsForResourceRead(address, size);
            if (!clean && IsCleanReadPage(address, size) && TryReadAliasBytes(address, destination))
            {
                worker.Reads.Add(address, destination);
                RememberDirectPage(worker, address, size);
                return true;
            }

            if (!_guestMemory.CanRead(address, size))
            {
                return RefuseOnTicketWorker("unreadable");
            }

            if (!clean && _commandThread!.TryReadShadowBefore(address, destination, worker.Sequence, out var shadowSequence, out var shadowSource))
            {
                if ((_translation.PendingWrites is { } pending && pending.HasPendingBetween(address, size, shadowSequence, worker.Sequence)) ||
                    _commandThread.HasPendingRangeBetween(address, size, shadowSequence, worker.Sequence))
                {
                    return RefuseOnTicketWorker("shadowed pending");
                }

                RefuseOnTicketWorker("shadow");
                SharpEmu.Libs.Agc.AgcExports.NoteWorkerShadowRead((int)shadowSource);
                return true;
            }

            if (clean ? IsGpuOwnedForWorkerCleanRead(address, size) : IsGpuOwnedForWorkerRead(address, size))
            {
                return RefuseOnTicketWorker("gpu owned");
            }

            if (IsPendingForWorker(address, size))
            {
                return RefuseOnTicketWorker("pending");
            }

            if (!_commandThread!.ReadOverlayBefore(address, destination, worker.Sequence))
            {
                return RefuseOnTicketWorker("overlay");
            }

            worker.Reads.Add(address, destination, clean);
            if (!clean)
            {
                NoteCleanReadPage(address, size);
            }

            return true;
        }

        internal static readonly SharpEmu.HLE.Diagnostics.ThreadCounter[] CommandReadCounts =
            Enumerable.Range(0, 12).Select(_ => new SharpEmu.HLE.Diagnostics.ThreadCounter()).ToArray();

        internal static string TakeCommandReadReport()
        {
            var counts = new long[CommandReadCounts.Length];
            for (var index = 0; index < counts.Length; index++)
            {
                counts[index] = CommandReadCounts[index].Take();
            }

            return FormattableString.Invariant($"cmd_reads=resident:{counts[0]}/fast:{counts[1]}/slow:{counts[0] - counts[1]},") +
                FormattableString.Invariant($"word:{counts[3]}/fast:{counts[4]}/slow:{counts[3] - counts[4]} ") +
                FormattableString.Invariant($"unclean=untagged:{counts[6]},gpu_dirty:{counts[7]},stores:{counts[8]},pending:{counts[9]},shadow:{counts[10]},alias:{counts[11]}");
        }

        private void NoteUncleanRead(ulong address, ulong size)
        {
            if (Reads is not { } pages || !TryGetCleanReadPage(address, size, out var page, out var slot) || pages.Tags[slot] != page + 1)
            {
                CommandReadCounts[6].Increment();
                return;
            }

            var thread = _commandThread!;
            var reason = _bufferCache.MayHaveGpuDirtyPages(page, CleanReadPageBytes) ? 7 :
                thread.HasPendingStores(page, CleanReadPageBytes) ? 8 :
                _translation.PendingWrites is { } pending && pending.MayHavePending(page, CleanReadPageBytes) ? 9 :
                thread.HasShadow(page, CleanReadPageBytes) ? 10 : 11;
            CommandReadCounts[reason].Increment();
        }

        private bool ReadWordOnCommandThread(ulong address, out uint word)
        {
            CommandReadCounts[3].Increment();
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!HasPendingMemoryPublication(address, sizeof(uint)) && IsCleanReadPage(address, sizeof(uint)) && TryReadAliasBytes(address, bytes))
            {
                CommandReadCounts[4].Increment();
                word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                return true;
            }

            NoteUncleanRead(address, sizeof(uint));
            if (!ReadOnCommandThread(address, bytes, RenderReadSource.Word))
            {
                word = 0;
                return false;
            }

            word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return true;
        }

        // Without waiting, a range with pending command-thread work is refused instead.
        private bool ReadResidentBytesOnCommandThread(ulong address, Span<byte> destination, bool clean, bool wait = true)
        {
            CommandReadCounts[0].Increment();
            var size = (ulong)destination.Length;
            if (wait)
            {
                CompletePendingPublicationsForResourceRead(address, size);
            }

            if (!clean && IsCleanReadPage(address, size) && TryReadAliasBytes(address, destination))
            {
                CommandReadCounts[1].Increment();
                return true;
            }

            if (!clean)
            {
                NoteUncleanRead(address, size);
            }

            if (!_guestMemory.CanRead(address, size))
            {
                return false;
            }

            var thread = _commandThread!;
            if (!clean && thread.TryReadShadow(address, destination, out var shadowSequence, out var shadowSource) &&
                !NewerPendingWrite(address, size, shadowSequence) && !thread.HasPendingRangeAfter(address, size, shadowSequence))
            {
                Diagnostics.CommandThreadTally.NoteShadowRead((int)shadowSource);
                return true;
            }

            if (wait)
            {
                thread.WaitForPendingRange(address, size);
            }
            else if (thread.HasPendingRange(address, size))
            {
                return false;
            }

            if (IsPendingOnCommandThread(address, size) || IsGpuOwnedForResidentRead(address, size, clean) || !thread.ReadOverlay(address, destination))
            {
                return false;
            }

            if (!clean)
            {
                NoteCleanReadPage(address, size);
            }

            return true;
        }

        public bool TryPeekResidentGuestBytes(ulong address, Span<byte> destination)
        {
            if (HasPendingMemoryPublication(address, (ulong)destination.Length))
            {
                return false;
            }

            if (OnResolveWorker)
            {
                return ReadResidentBytesOnResolveWorker(address, destination, clean: false);
            }

            return OnCommandThread ? ReadResidentBytesOnCommandThread(address, destination, clean: false, wait: false) : TryReadResidentGuestBytes(address, destination, clean: false);
        }

        // The same ownership rules as the two word readers, checked once for a whole range.
        public bool TryReadResidentGuestBytes(ulong address, Span<byte> destination, bool clean)
        {
            if (OnResolveWorker)
            {
                return ReadResidentBytesOnResolveWorker(address, destination, clean);
            }

            if (OnCommandThread)
            {
                return ReadResidentBytesOnCommandThread(address, destination, clean);
            }

            var size = (ulong)destination.Length;
            CompletePendingPublicationsForResourceRead(address, size);
            if (clean || !IsCleanReadPage(address, size))
            {
                if (!_guestMemory.CanRead(address, size) ||
                    _bufferCache.HasGpuDirtyBytes(address, size) ||
                    (clean && (_bufferCache.HasGpuDirtyPages(address, size) || _imageCache.HasGpuModifiedImageBytes(address, size))))
                {
                    return false;
                }

                NoteCleanReadPage(address, size);
            }

            if (TryReadAliasBytes(address, destination))
            {
                return true;
            }

            return _guestMemory.TryRead(address, destination);
        }

        private const ulong CleanReadPageBytes = 0x1000;
        private const int CleanReadPageSlots = 16384;
        private CleanReadPages? _renderReads;
        private CleanReadPages? _commandReads;

        private CleanReadPages? Reads => OnResolveWorker ? _resolveWorker!.CleanReads ??= new CleanReadPages() : OnCommandThread ? _commandReads : _renderReads;

        private sealed class CleanReadPages
        {
            public readonly ulong[] Tags = new ulong[CleanReadPageSlots];
            public readonly ulong[] Aliases = new ulong[CleanReadPageSlots];
            public readonly object?[] Snapshots = new object?[CleanReadPageSlots];
        }

        private bool TryReadAliasBytes(ulong address, Span<byte> destination)
        {
            if (_guestBacking?.TryEnterBackingAliasAccess() != true) return false;
            try
            {
                if (!TryGetAliasPointer(address, (ulong)destination.Length, out var pointer)) return false;
                new ReadOnlySpan<byte>(pointer, destination.Length).CopyTo(destination);
                return true;
            }
            finally
            {
                // Release the lease before a fallback can wait for a copy reservation.
                _guestBacking.ExitBackingAliasAccess();
            }
        }

        private void RememberDirectPage(ResolveWorkerState worker, ulong address, ulong size)
        {
            if (!DirectPageReads || !TryGetCleanReadPage(address, size, out var page, out var slot) || Reads is not { } pages ||
                pages.Tags[slot] != page + 1 || pages.Aliases[slot] == 0 || _guestBacking.BackingAliasSnapshot is not { } snapshot ||
                !ReferenceEquals(pages.Snapshots[slot], snapshot))
            {
                return;
            }

            worker.DirectPage = page;
            worker.DirectAlias = (nint)pages.Aliases[slot];
            worker.DirectSnapshot = snapshot;
        }

        private bool TryGetAliasPointer(ulong address, ulong size, out byte* pointer)
        {
            pointer = null;
            if (Reads is not { } pages ||
                !TryGetCleanReadPage(address, size, out var page, out var slot) || pages.Tags[slot] != page + 1 ||
                _guestBacking.BackingAliasSnapshot is not { } snapshot)
            {
                return false;
            }

            if (pages.Aliases[slot] == 0 || !ReferenceEquals(pages.Snapshots[slot], snapshot))
            {
                if (!_guestBacking.TryResolveBackingAlias(page, CleanReadPageBytes, out var resolved) || resolved == 0)
                {
                    return false;
                }

                pages.Aliases[slot] = resolved;
                pages.Snapshots[slot] = snapshot;
            }

            pointer = (byte*)(pages.Aliases[slot] + (address - page));
            return true;
        }

        private static bool TryGetCleanReadPage(ulong address, ulong size, out ulong page, out int slot)
        {
            page = address & ~(CleanReadPageBytes - 1);
            slot = (int)((page / CleanReadPageBytes) & (CleanReadPageSlots - 1));
            return size != 0 && address + size > address && ((address + size - 1) & ~(CleanReadPageBytes - 1)) == page;
        }

        private bool IsCleanReadPage(ulong address, ulong size)
        {
            if (Reads is not { } pages ||
                !TryGetCleanReadPage(address, size, out var page, out var slot) ||
                pages.Tags[slot] != page + 1 ||
                _bufferCache.MayHaveGpuDirtyPages(page, CleanReadPageBytes))
            {
                return false;
            }

            if (!OnCommandThread && !OnResolveWorker)
            {
                return true;
            }

            return MayBeCleanReadPage(page);
        }

        // A queued fill or copy that has not landed changes the page for every item after it in the stream, so the page reads
        // through the ordered path, which refuses the bytes or waits for the range, as it does for a queued store.
        private bool MayBeCleanReadPage(ulong page)
        {
            var thread = _commandThread!;
            return !_bufferCache.MayHaveGpuDirtyPages(page, CleanReadPageBytes) && !thread.HasPendingStores(page, CleanReadPageBytes) &&
                !thread.HasPendingRange(page, CleanReadPageBytes) &&
                (_translation.PendingWrites is not { } pending || !pending.MayHavePending(page, CleanReadPageBytes)) &&
                !thread.HasShadow(page, CleanReadPageBytes);
        }

        private void NoteCleanReadPage(ulong address, ulong size)
        {
            if (!TryGetCleanReadPage(address, size, out var page, out var slot))
            {
                return;
            }

            var command = OnCommandThread;
            var worker = OnResolveWorker;
            if ((command || worker) && !MayBeCleanReadPage(page))
            {
                return;
            }

            if (!_guestMemory.CanRead(page, CleanReadPageBytes) || _bufferCache.HasGpuDirtyBytes(page, CleanReadPageBytes) ||
                (command && IsPendingOnCommandThread(page, CleanReadPageBytes)) || (worker && IsPendingForWorker(page, CleanReadPageBytes)))
            {
                return;
            }

            var pages = worker
                ? _resolveWorker!.CleanReads ??= new CleanReadPages()
                : command ? _commandReads ??= new CleanReadPages() : _renderReads ??= new CleanReadPages();
            if (pages.Tags[slot] != page + 1)
            {
                pages.Tags[slot] = page + 1;
                pages.Aliases[slot] = 0;
                pages.Snapshots[slot] = null;
            }
        }

        public ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramCompile);
            var payload = stage == ShaderStage.Pixel && _graphicsPipelineLibraries
                ? WithDescriptorSet(shader.Payload, 0, PixelDescriptorSet)
                : shader.Payload;
            var module = CreateShaderModule(payload);
            SetDebugName(ObjectType.ShaderModule, module.Handle, $"SharpEmu {stage} 0x{hash:X16}");
            _shaderModuleSpirvBytes[module.Handle] = payload.Length;
            if (stage is ShaderStage.Vertex or ShaderStage.Pixel)
                _rectangleStageCode[module.Handle] = payload;
            var identity = VulkanPipelineCacheStorage.CompiledShaderIdentity(payload);
            _shaderModuleCacheIdentities[module.Handle] = identity;
            if (stage == ShaderStage.Compute)
            {
                NoteRuntimeComputeModule(identity);
            }
            else if (stage is ShaderStage.Pixel or ShaderStage.Vertex or ShaderStage.Mesh)
            {
                NoteRuntimeGraphicsModule(identity, stage);
            }

            // Publish the handle only after all metadata is installed. The owning resolver exposes its program next.
            if (!_shaderModules.TryAdd(programId, module))
                throw SubmissionScheduler.Fatal($"Duplicate shader program ID: {programId}.");
            return module.Handle;
        }

        // The Metal shader compiler can spend seconds on one translated program and the
        // command stream cannot advance while it does, so a slow creation is reported with
        // the SPIR-V size that produced it.
        private const long SlowPipelineCreationMilliseconds = 250;

        private int SpirvBytesOf(ulong moduleHandle) =>
            _shaderModuleSpirvBytes.TryGetValue(moduleHandle, out var bytes) ? bytes : -1;

        private void ReportPipelineCreation(long elapsedMilliseconds, string kind, string stages, string spirv)
        {
            Interlocked.Add(ref _pipelineCreationMilliseconds, elapsedMilliseconds);
            if (elapsedMilliseconds < SlowPipelineCreationMilliseconds)
            {
                return;
            }

            Console.Error.WriteLine(
                $"[GPU][WARN] Slow pipeline creation: kind={kind} {stages} spirv_bytes={spirv} " +
                $"ms={elapsedMilliseconds} total_s={Interlocked.Read(ref _pipelineCreationMilliseconds) / 1000.0:F1}");
        }

        private void CreateBarycentricPipeline()
        {
            var vertexBytes = Convert.FromBase64String(FullscreenBarycentricVertexSpirv);
            var fragmentBytes = Convert.FromBase64String(FullscreenBarycentricFragmentSpirv);
            var vertexModule = CreateShaderModule(vertexBytes);
            var fragmentModule = CreateShaderModule(fragmentBytes);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };

                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };
                var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
                var scissor = new Rect2D(new Offset2D(0, 0), _extent);
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask =
                        ColorComponentFlags.RBit |
                        ColorComponentFlags.GBit |
                        ColorComponentFlags.BBit |
                        ColorComponentFlags.ABit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment,
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &colorBlend,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };
                Check(
                    _vk.CreateGraphicsPipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out _barycentricPipeline),
                    "vkCreateGraphicsPipelines");
                MarkPipelineCacheDirty();
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private ShaderModule _meshTaskModule;
        private static readonly object MeshTaskModuleGate = new();

        // The generic task stage of task-launched mesh pipelines; pipelines compile on worker threads.
        private ShaderModule RequireMeshTaskModule()
        {
            lock (MeshTaskModuleGate)
            {
                if (_meshTaskModule.Handle == 0)
                {
                    _meshTaskModule = CreateShaderModule(SharpEmu.ShaderCompiler.Vulkan.SpirvMeshTask.Create());
                }

                return _meshTaskModule;
            }
        }

        // The task stage's specialization: the program's primitives per group and point list, the device's group limits.
        private void FillTaskSpecialization(MeshTaskLaunchInfo task, SpecializationMapEntry* entries, uint* values,
            ref SpecializationInfo specialization)
        {
            // The task stage divides by it, as the conversion did by its push word.
            if (task.PrimitivesPerGroup == 0)
                throw SubmissionScheduler.Fatal("A task-launched mesh program has no primitives per group.");
            values[0] = task.PrimitivesPerGroup;
            values[1] = task.PointList ? 1u : 0u;
            values[2] = _meshShaderLimits.MaxGroupCountX;
            values[3] = _meshShaderLimits.MaxGroupCountY;
            values[4] = _meshShaderLimits.MaxGroupTotalCount;
            uint[] ids =
            [
                SharpEmu.ShaderCompiler.Vulkan.SpirvMeshTask.SpecPrimitivesPerGroup,
                SharpEmu.ShaderCompiler.Vulkan.SpirvMeshTask.SpecPointList,
                SharpEmu.ShaderCompiler.Vulkan.SpirvMeshTask.SpecMaxGroupCountX,
                SharpEmu.ShaderCompiler.Vulkan.SpirvMeshTask.SpecMaxGroupCountY,
                SharpEmu.ShaderCompiler.Vulkan.SpirvMeshTask.SpecMaxGroupTotalCount,
            ];
            for (var index = 0; index < ids.Length; index++)
            {
                entries[index] = new SpecializationMapEntry(ids[index], (uint)(index * sizeof(uint)), sizeof(uint));
            }

            specialization = new SpecializationInfo
            {
                MapEntryCount = (uint)ids.Length,
                PMapEntries = entries,
                DataSize = (nuint)(ids.Length * sizeof(uint)),
                PData = values,
            };
        }

        private const int TaskSpecializationValues = 5;

        // The task stage of a task-launched mesh program's pipeline or pre-rasterization part; the graphics prewarm
        // builds the same one. entries and values hold TaskSpecializationValues each and, like specialization, outlive
        // the create info.
        private PipelineShaderStageCreateInfo TaskStageInfo(MeshTaskLaunchInfo task, SpecializationMapEntry* entries, uint* values,
            SpecializationInfo* specialization, byte* entryPoint)
        {
            FillTaskSpecialization(task, entries, values, ref *specialization);
            return new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.TaskBitExt,
                Module = RequireMeshTaskModule(),
                PName = entryPoint,
                PSpecializationInfo = specialization,
            };
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            if (!_supportsShaderClipDistance && RequiresCapability(code, SpirvCapability.ClipDistance))
            {
                throw new NotSupportedException("The shader requires the shaderClipDistance device feature.");
            }
            if (!_supportsFragmentShaderBarycentric && RequiresCapability(code, SpirvCapability.FragmentBarycentricKhr))
            {
                throw new NotSupportedException(
                    "The shader requires the fragmentShaderBarycentric device feature.");
            }
            if (!_supportsShaderLayer && RequiresCapability(code, SpirvCapability.ShaderLayer))
            {
                throw new NotSupportedException("The shader requires the shaderOutputLayer device feature.");
            }

            string? dumpPath = null;
            var dumpDirectory = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR");
            if (!string.IsNullOrWhiteSpace(dumpDirectory))
            {
                Directory.CreateDirectory(dumpDirectory);

                var sequence = Interlocked.Increment(ref _shaderModuleDumpSequence);
                dumpPath = Path.Combine(dumpDirectory, $"{sequence:D4}.spv");
                File.WriteAllBytes(dumpPath, code);

                _pendingShaderModuleDumpPath = dumpPath;
            }

            try
            {
                fixed (byte* codePointer = code)
                {
                    var createInfo = new ShaderModuleCreateInfo
                    {
                        SType = StructureType.ShaderModuleCreateInfo,
                        CodeSize = (nuint)code.Length,
                        PCode = (uint*)codePointer,
                    };
                    Check(
                        _vk.CreateShaderModule(_device, &createInfo, null, out var module),
                        "vkCreateShaderModule");
                    return module;
                }
            }
            finally
            {
                _pendingShaderModuleDumpPath = null;
            }
        }

        private static bool RequiresCapability(ReadOnlySpan<byte> code, SpirvCapability capability)
        {
            for (var offset = 20; offset + 4 <= code.Length;)
            {
                var header = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(code[offset..]);
                var wordCount = (int)(header >> 16);
                if (wordCount == 0 || wordCount > (code.Length - offset) / 4)
                {
                    throw new InvalidOperationException("The shader contains an invalid SPIR-V instruction.");
                }

                if ((header & 0xFFFF) == (uint)SpirvOp.Capability && wordCount == 2 &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(code[(offset + 4)..]) ==
                        (uint)capability)
                {
                    return true;
                }
                offset += wordCount * 4;
            }
            return false;
        }

        // One layout binding per descriptor binding of the stage, at the stage's native binding numbers.
        private static void CollectLayoutBindings(List<DescriptorSetLayoutBinding> bindings, ShaderProgramInfo program, ShaderStage stage) =>
            CollectLayoutBindings(
                bindings,
                program.Bindings ?? throw SubmissionScheduler.Fatal($"The program has no binding layout: hash=0x{program.Hash:X16}."),
                stage);

        private static bool UsesBindlessHeap(ShaderProgramInfo program) => program.Bindings?.UsesBindlessHeap == true;

        // The bindings of the stage before rasterization, collected before any other stage's.
        private static void CollectVertexStageBindings(List<DescriptorSetLayoutBinding> bindings, GraphicsPipelineDescription description)
        {
            CollectLayoutBindings(bindings, description.VertexStage,
                description.Tessellation is not null ? ShaderStage.TessellationEvaluation
                : description.VertexStage.Stage == ShaderStageKind.Mesh ? ShaderStage.Mesh : ShaderStage.Vertex);
            if (description.Tessellation is not null)
            {
                // The control bridge reads the domain stage's runtime data.
                var dataBinding = BindingLayout.NativeBindingIndex(ShaderStage.TessellationEvaluation, DescriptorBindingKind.ShaderData);
                var dataIndex = bindings.FindIndex(binding => binding.Binding == dataBinding);
                if (dataIndex < 0) throw SubmissionScheduler.Fatal("The tessellation pipeline has no runtime data binding.");
                var dataLayout = bindings[dataIndex];
                dataLayout.StageFlags |= ShaderStageFlags.TessellationControlBit;
                bindings[dataIndex] = dataLayout;
            }
        }

        private static void CollectLayoutBindings(List<DescriptorSetLayoutBinding> bindings, BindingLayout layout, ShaderStage stage)
        {
            foreach (var binding in layout.Descriptors)
            {
                bindings.Add(new DescriptorSetLayoutBinding
                {
                    Binding = BindingLayout.NativeBindingIndex(stage, binding.Kind),
                    DescriptorType = DescriptorWriter.DescriptorType(binding.Kind),
                    DescriptorCount = DescriptorWriter.DescriptorCount(binding),
                    StageFlags = DescriptorWriter.ShaderStageFlag(stage),
                });
            }
        }

        // The set is pushed when its descriptors fit the device limit, else it comes from the heap.
        private PhysicalDeviceLimits _shaderDescriptorLimits;

        private DescriptorSetLayout CreateDescriptorSetLayout(List<DescriptorSetLayoutBinding> bindings, out bool usesPushDescriptors, out DescriptorSetDemand demand) =>
            CreateDescriptorSetLayout(bindings, allowPush: true, out usesPushDescriptors, out demand);

        private DescriptorSetLayout CreateDescriptorSetLayout(List<DescriptorSetLayoutBinding> bindings, bool allowPush, out bool usesPushDescriptors,
            out DescriptorSetDemand demand)
        {
            ShaderDescriptorLimits.Validate(bindings, _shaderDescriptorLimits);
            var descriptorCount = 0u;
            demand = default;
            foreach (var binding in bindings)
            {
                descriptorCount += binding.DescriptorCount;
                demand = demand.Add(DescriptorSetDemand.Of(binding.DescriptorType, binding.DescriptorCount));
            }

            usesPushDescriptors = allowPush && descriptorCount <= _maxPushDescriptors;
            var bindingArray = bindings.ToArray();
            fixed (DescriptorSetLayoutBinding* bindingPointer = bindingArray)
            {
                var create = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    Flags = usesPushDescriptors ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0,
                    BindingCount = (uint)bindingArray.Length,
                    PBindings = bindingArray.Length == 0 ? null : bindingPointer,
                };
                Check(_vk.CreateDescriptorSetLayout(_device, &create, null, out var layout), "vkCreateDescriptorSetLayout");
                return layout;
            }
        }

        // The task stage's own push words (SpirvMeshTask), after the shared push data: in every layout a task-launched mesh
        // pipeline can use when task launches are on.
        private static readonly PushConstantRange TaskPushRange = new()
        {
            StageFlags = ShaderStageFlags.TaskBitExt,
            Offset = SharpEmu.ShaderCompiler.Vulkan.SpirvMeshTask.PushOffsetBytes,
            Size = SharpEmu.ShaderCompiler.Vulkan.SpirvMeshTask.PushWordCount * 4u,
        };

        // A program that reads bindless heap records also binds the heap as set 1.
        private PipelineLayout CreatePipelineLayout(DescriptorSetLayout setLayout, ShaderStageFlags pushStages, bool bindlessHeap = false)
        {
            var pushRanges = stackalloc PushConstantRange[2];
            pushRanges[0] = new PushConstantRange { StageFlags = pushStages, Offset = 0, Size = PushConstantBytes };
            var pushRangeCount = 1u;
            if (_supportsTaskShader && (pushStages & ShaderStageFlags.MeshBitExt) != 0)
            {
                pushRanges[pushRangeCount++] = TaskPushRange;
            }
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = setLayout;
            setLayouts[1] = bindlessHeap ? RequireHeapSetLayout() : default;
            var create = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = bindlessHeap ? 2u : 1u,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = pushRangeCount,
                PPushConstantRanges = pushRanges,
            };
            Check(_vk.CreatePipelineLayout(_device, &create, null, out var layout), "vkCreatePipelineLayout");
            return layout;
        }

        private PipelineHandle RegisterPipeline(RenderPipelineEntry entry, Action? transferOwnership = null)
        {
            entry.Id = Interlocked.Increment(ref _nextPipelineId);
            _pipelineEntries.TryAdd(entry.Id, entry);
            transferOwnership?.Invoke();
            if (_gpuCommandProfile is not null)
            {
                Console.Error.WriteLine($"[PERF][GPU_PIPELINE] pipeline={entry.Id} vertex=0x{entry.ProfileVertexHash:X16} pixel=0x{entry.ProfilePixelHash:X16} compute=0x{entry.ProfileComputeHash:X16}");
                if (entry.Description is { } description)
                {
                    var parameters = description.StaticParameters;
                    Console.Error.WriteLine($"[PERF][GPU_GRAPHICS_STATE] pipeline={entry.Id} stage={description.VertexStage.Stage} " +
                        $"topology={parameters.Topology} samples={parameters.Samples} sample_shading={parameters.SampleShadingEnable} " +
                        $"cull_front={parameters.CullFront} cull_back={parameters.CullBack} depth_clip={parameters.DepthClipEnable} " +
                        $"color_formats={string.Join(',', description.Rendering.ColorFormats.Take((int)description.Rendering.ColorCount))} " +
                        $"depth_format={description.Rendering.DepthFormat} stencil_format={description.Rendering.StencilFormat}");
                }
            }
            return new PipelineHandle(entry.Id, entry.Layout.Handle, entry.UsesPushDescriptors);
        }

        public PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDescription description)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineSetup);
            var entry = CreateGraphicsPipelineEntry(description);
            // Rectangle draws bind a native fill or compatibility variant at draw time.
            if (!entry.RectangleList)
            {
                entry.Pipeline = CreateRenderPipeline(description, description.StaticParameters.Topology, entry.Layout, libraries: entry);
            }

            return RegisterPipeline(entry);
        }

        // The layouts of a graphics pipeline and the entry that holds them, without its pipeline.
        private RenderPipelineEntry CreateGraphicsPipelineEntry(GraphicsPipelineDescription description)
        {
            if (description.Rendering.ColorCount > _maxColorAttachments)
            {
                throw SubmissionScheduler.Fatal($"The draw binds more color attachments than the device supports: count={description.Rendering.ColorCount} max={_maxColorAttachments}.");
            }

            // With libraries every pixel module reads its bindings from set 2 (CreateShaderModule), so every pipeline is laid
            // out for them, a tessellated one too (LinkOrBuildGraphicsPipeline).
            if (_graphicsPipelineLibraries)
            {
                return CreateLibraryPipelineEntry(description);
            }

            var bindings = new List<DescriptorSetLayoutBinding>();
            CollectVertexStageBindings(bindings, description);
            if (description.PixelStage is { } pixelStage)
            {
                CollectLayoutBindings(bindings, pixelStage, ShaderStage.Pixel);
            }

            var setLayout = CreateDescriptorSetLayout(bindings, out var usesPushDescriptors, out var demand);
            var bindlessHeap = UsesBindlessHeap(description.VertexStage) ||
                (description.PixelStage is { } heapPixelStage && UsesBindlessHeap(heapPixelStage));
            return new RenderPipelineEntry
            {
                SetLayout = setLayout,
                Demand = demand,
                Layout = CreatePipelineLayout(setLayout,
                    (description.VertexStage.Stage == ShaderStageKind.Mesh
                        ? ShaderStageFlags.MeshBitExt : ShaderStageFlags.VertexBit) |
                    ShaderStageFlags.FragmentBit |
                    (description.Tessellation is null ? 0 : ShaderStageFlags.TessellationControlBit | ShaderStageFlags.TessellationEvaluationBit), bindlessHeap),
                UsesPushDescriptors = usesPushDescriptors,
                UsesBindlessHeap = bindlessHeap,
                Description = description,
                ProfileVertexHash = description.VertexStage.Hash,
                ProfilePixelHash = description.PixelStage?.Hash ?? 0,
            };
        }

        // One graphics pipeline whose vkCreateGraphicsPipelines call runs on a worker thread. The command stream prepares
        // its layouts, its entry and its pipeline cache source before the compile starts, and registers the pipeline once
        // the compile is done (TryCreateGraphicsPipeline).
        internal sealed class PendingGraphicsPipeline
        {
            public required RenderPipelineEntry Entry;
            public required Lazy<PipelineCache>? CacheSource;
            public required string Stages;
            public required string SpirvBytes;
            public Task<Pipeline> Compile = Task.FromResult(default(Pipeline));
            public long StartTimestamp;
            public long CompileMilliseconds;
        }

        private readonly Dictionary<GraphicsPipelineDescription, PendingGraphicsPipeline> _pendingGraphicsPipelines =
            new(ReferenceEqualityComparer.Instance);

        private static readonly SemaphoreSlim _graphicsCompileSlots = new(Math.Max(2, Environment.ProcessorCount / 4));

        // A host shader compiler can spend seconds on one large translated pixel shader, and a game entering a new area
        // asks for dozens. Graphics pipelines are built on worker threads, and the draw waits for its pipeline outside the
        // pipeline cache's lock (WaitForGraphicsPipeline), so the next draws' programs are translated meanwhile and their
        // pixel stage parts compile beside it (PixelProgramCreated). SHARPEMU_ASYNC_GRAPHICS_PIPELINES=0 builds them on the
        // command stream. Resolve workers create pipelines concurrently, so the pending compiles are read and changed under
        // their own lock.
        private static readonly bool _asyncGraphicsPipelines =
            Environment.GetEnvironmentVariable("SHARPEMU_ASYNC_GRAPHICS_PIPELINES") != "0";

        // The command stream waits here, outside the pipeline cache's lock, for a compile TryCreateGraphicsPipeline left
        // running; the next TryCreateGraphicsPipeline takes the pipeline.
        public void WaitForGraphicsPipeline(GraphicsPipelineDescription description)
        {
            PendingGraphicsPipeline? pending;
            lock (_pendingGraphicsPipelines)
            {
                if (!_pendingGraphicsPipelines.TryGetValue(description, out pending) || pending.Compile.IsCompleted)
                {
                    return;
                }
            }

            using var waitScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.GraphicsPipelineDriver);
            try
            {
                pending.Compile.Wait();
            }
            catch (AggregateException)
            {
                // A failed compile throws when its pipeline is taken.
            }
        }

        public bool TryCreateGraphicsPipeline(GraphicsPipelineDescription description, out PipelineHandle handle)
        {
            lock (_pendingGraphicsPipelines)
            {
                return TryCreateGraphicsPipelineCore(description, out handle);
            }
        }

        private bool TryCreateGraphicsPipelineCore(GraphicsPipelineDescription description, out PipelineHandle handle)
        {
            if (_pendingGraphicsPipelines.TryGetValue(description, out var pending))
            {
                return TryTakeGraphicsPipeline(description, pending, out handle);
            }

            // Rectangle lists build their pipelines at draw time.
            if (!_asyncGraphicsPipelines ||
                (description.Tessellation is null && description.StaticParameters.Topology == PrimitiveTopology.PatchList))
            {
                handle = CreateGraphicsPipeline(description);
                return true;
            }

            RenderPipelineEntry entry;
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineSetup))
            {
                entry = CreateGraphicsPipelineEntry(description);
            }

            var vertexModule = description.VertexProgram.Module;
            var pixelModule = description.PixelStage is null ? 0 : description.PixelProgram.Module;
            var started = new PendingGraphicsPipeline
            {
                Entry = entry,
                CacheSource = GetGuestPipelineCacheSource(GraphicsCacheKey(description.VertexStage.Hash,
                    description.PixelStage?.Hash ?? 0, vertexModule, pixelModule)),
                Stages = $"vs=0x{description.VertexStage.Hash:X16} ps=0x{description.PixelStage?.Hash ?? 0:X16}",
                SpirvBytes = pixelModule == 0 ? $"{SpirvBytesOf(vertexModule)}" : $"{SpirvBytesOf(vertexModule)}/{SpirvBytesOf(pixelModule)}",
                StartTimestamp = Stopwatch.GetTimestamp(),
            };
            var topology = description.StaticParameters.Topology;
            started.Compile = Task.Factory.StartNew(
                () =>
                {
                    _graphicsCompileSlots.Wait();
                    var compileStart = Stopwatch.GetTimestamp();
                    try
                    {
                        return CreateRenderPipeline(description, topology, entry.Layout, worker: started, libraries: entry);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref started.CompileMilliseconds, (long)Stopwatch.GetElapsedTime(compileStart).TotalMilliseconds);
                        _graphicsCompileSlots.Release();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _pendingGraphicsPipelines.Add(description, started);
            return TryTakeGraphicsPipeline(description, started, out handle);
        }

        // Takes a finished compile: reports and names the pipeline and registers its entry. False while the compile runs.
        private bool TryTakeGraphicsPipeline(GraphicsPipelineDescription description, PendingGraphicsPipeline pending, out PipelineHandle handle)
        {
            if (!pending.Compile.IsCompleted)
            {
                handle = default;
                return false;
            }

            var pipeline = pending.Compile.GetAwaiter().GetResult();
            ReportPipelineCreation(
                Interlocked.Read(ref pending.CompileMilliseconds),
                "graphics-async",
                $"{pending.Stages} waited_ms={(long)Stopwatch.GetElapsedTime(pending.StartTimestamp).TotalMilliseconds}",
                pending.SpirvBytes);
            MarkPipelineCacheDirty();
            Interlocked.Increment(ref _perfPipelineCreations);
            SetDebugName(ObjectType.Pipeline, pipeline.Handle,
                $"SharpEmu graphics {pending.Stages} colors={description.StaticParameters.ColorCount}");
            pending.Entry.Pipeline = pipeline;
            handle = RegisterPipeline(pending.Entry, () => _pendingGraphicsPipelines.Remove(description));
            return true;
        }

        private static StencilOpState ToVkStencilOpState(in StencilOperations operations) => new()
        {
            FailOp = operations.FailOperation,
            PassOp = operations.PassOperation,
            DepthFailOp = operations.DepthFailOperation,
            CompareOp = operations.Compare,
        };

        // The host format of each attribute, narrowed to the components the program fetches.
        private static void BuildVertexAttributes(
            GraphicsPipelineDescription description,
            VertexInputAttributeDescription[] attributes,
            VertexInputBindingDescription[] bindings)
        {
            var vertexInput = description.VertexInput;
            var info = description.VertexInfo;
            for (var binding = 0; binding < vertexInput.BindingCount; binding++)
            {
                bindings[binding] = new VertexInputBindingDescription
                {
                    Binding = (uint)binding,
                    Stride = vertexInput.Bindings[binding].Stride,
                    InputRate = vertexInput.Bindings[binding].Instance ? VertexInputRate.Instance : VertexInputRate.Vertex,
                };
            }

            for (var index = 0; index < vertexInput.AttributeCount; index++)
            {
                var resource = info.Attributes[index];
                var descriptor = resource.Descriptor;
                var compiledComponents = description.VertexStage.VertexFetchComponents[index];
                var usedComponents = compiledComponents > 0 ? compiledComponents : (uint)resource.RegisterCount;
                var format = VertexAttributeFormats.Resolve(in descriptor, usedComponents, out var attributeSize);
                if (descriptor.OutOfBounds != 0 && Interlocked.Exchange(ref _vertexOutOfBoundsLines, 1) == 0)
                {
                    Console.Error.WriteLine($"[LOADER][INFO] vertex_input accepted the out-of-bounds mode {descriptor.OutOfBounds} of an attribute buffer");
                }

                if (descriptor.AddThreadId)
                {
                    throw SubmissionScheduler.Fatal($"A vertex attribute buffer adds the thread index: attribute={index} hash=0x{description.VertexStage.Hash:X16}.");
                }

                if (descriptor.SwizzleEnabled)
                {
                    throw SubmissionScheduler.Fatal($"A vertex attribute buffer is swizzled: attribute={index} hash=0x{description.VertexStage.Hash:X16}.");
                }

                CheckVertexSwizzle(in descriptor, (int)usedComponents, attributeSize, index);
                attributes[index] = new VertexInputAttributeDescription
                {
                    Location = (uint)index,
                    Binding = vertexInput.Attributes[index].Binding,
                    Format = format,
                    Offset = vertexInput.Attributes[index].Offset,
                };
            }
        }

        private static uint DestinationSelect(uint x, uint y = 0, uint z = 0, uint w = 0) => x | (y << 3) | (z << 6) | (w << 9);

        // A destination select the fixed-function fetch cannot apply is logged once and accepted.
        private static void CheckVertexSwizzle(in BufferDescriptorWords descriptor, int fetchedComponents, uint attributeSize, int index)
        {
            uint swizzle;
            uint expected;
            var supported = true;
            switch (fetchedComponents)
            {
                case 1:
                    swizzle = descriptor.DestinationSelectX;
                    expected = DestinationSelect(4);
                    supported = swizzle == expected;
                    break;
                case 2:
                    swizzle = descriptor.DestinationSelectXY;
                    expected = attributeSize == 1 ? DestinationSelect(4, 0) : DestinationSelect(4, 5);
                    supported = swizzle == expected;
                    break;
                case 3:
                    swizzle = descriptor.DestinationSelectXYZ;
                    expected = attributeSize switch
                    {
                        1 => DestinationSelect(4, 0, 0),
                        2 => DestinationSelect(4, 5, 0),
                        _ => DestinationSelect(4, 5, 6),
                    };
                    supported = swizzle == expected;
                    break;
                case 4:
                    swizzle = descriptor.DestinationSelectXYZW;
                    switch (attributeSize)
                    {
                        case 1:
                            expected = DestinationSelect(4, 0, 0, 1);
                            supported = swizzle == expected;
                            break;
                        case 2:
                            expected = DestinationSelect(4, 5, 0, 1);
                            supported = swizzle == expected;
                            break;
                        case 3:
                            expected = DestinationSelect(4, 5, 6, 1);
                            supported = swizzle == expected || swizzle == DestinationSelect(4, 5, 6, 0);
                            break;
                        default:
                            expected = DestinationSelect(4, 5, 6, 7);
                            supported = swizzle == expected || swizzle == DestinationSelect(4, 5, 6, 1) || swizzle == DestinationSelect(4, 5, 6, 0);
                            break;
                    }

                    break;
                default:
                    throw SubmissionScheduler.Fatal($"A vertex attribute fetch uses an invalid component count: attribute={index} components={fetchedComponents}.");
            }

            if (!supported && Interlocked.Exchange(ref _vertexSwizzleLines, 1) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] vertex_input accepted an unsupported destination select attribute={index} size={attributeSize} " +
                    $"components={fetchedComponents} swizzle=0x{swizzle:X3} expected=0x{expected:X3}");
            }
        }

        // One graphics pipeline for dynamic rendering: the attachment formats travel in the create info.
        // With worker (a compile TryCreateGraphicsPipeline started), this runs on a compiler thread: the pipeline cache comes
        // from the source the command stream resolved, and the command stream reports and names the pipeline when it takes it.
        // A prewarm pipeline is built in the device pipeline cache and returned unreported.
        internal Pipeline CreateRenderPipeline(GraphicsPipelineDescription description, PrimitiveTopology topology, PipelineLayout layout,
            PolygonMode polygonMode = PolygonMode.Fill, bool expandRectangles = false, PendingGraphicsPipeline? worker = null,
            RenderPipelineEntry? libraries = null, bool prewarm = false)
        {
            var parameters = description.StaticParameters;
            var rendering = description.Rendering;
            var vertexModule = new ShaderModule(description.VertexProgram.Module);
            var pixelModule = description.PixelStage is null ? default : new ShaderModule(description.PixelProgram.Module);
            if (vertexModule.Handle == 0 || (description.PixelStage is not null && pixelModule.Handle == 0))
            {
                throw SubmissionScheduler.Fatal($"A graphics pipeline stage has no module: vertex=0x{vertexModule.Handle:X} pixel=0x{pixelModule.Handle:X}.");
            }

            ShaderModule rectangleModule = default;
            ShaderModule rectangleVertexModule = default;
            ShaderModule rectanglePixelModule = default;
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                if (expandRectangles)
                {
                    var geometryCode = RectangleGeometryShader.Create(_rectangleStageCode[vertexModule.Handle],
                        pixelModule.Handle == 0 ? null : _rectangleStageCode[pixelModule.Handle], out var vertexCode, out var pixelCode);
                    rectangleModule = CreateShaderModule(geometryCode);
                    rectangleVertexModule = CreateShaderModule(vertexCode);
                    if (pixelCode is not null) rectanglePixelModule = CreateShaderModule(pixelCode);
                }
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[5];
                var taskEntries = stackalloc SpecializationMapEntry[TaskSpecializationValues];
                var taskValues = stackalloc uint[TaskSpecializationValues];
                var taskSpecialization = new SpecializationInfo();
                var stageCount = 1u;
                shaderStages[0] = VertexStageInfo(description.VertexStage.Stage == ShaderStageKind.Mesh,
                    expandRectangles ? rectangleVertexModule : vertexModule, entryPoint);
                var tessellationState = new PipelineTessellationStateCreateInfo
                {
                    SType = StructureType.PipelineTessellationStateCreateInfo,
                    PatchControlPoints = description.Tessellation?.InputControlPoints ?? 0,
                };
                if (description.Tessellation is { } tessellation)
                {
                    if (topology != PrimitiveTopology.PatchList || tessellation.InputControlPoints is 0 or > 32 ||
                        tessellation.Control.Module == 0 || tessellation.Evaluation.Module == 0)
                    {
                        throw SubmissionScheduler.Fatal("Invalid native tessellation pipeline stages or patch size.");
                    }

                    shaderStages[stageCount++] = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.TessellationControlBit,
                        Module = new ShaderModule(tessellation.Control.Module),
                        PName = entryPoint,
                    };
                    shaderStages[stageCount++] = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.TessellationEvaluationBit,
                        Module = new ShaderModule(tessellation.Evaluation.Module),
                        PName = entryPoint,
                    };
                }

                if (rectangleModule.Handle != 0)
                {
                    shaderStages[stageCount++] = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.GeometryBit,
                        Module = rectangleModule,
                        PName = entryPoint,
                    };
                }
                if (pixelModule.Handle != 0)
                {
                    shaderStages[stageCount++] = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.FragmentBit,
                        Module = expandRectangles ? rectanglePixelModule : pixelModule,
                        PName = entryPoint,
                    };
                }

                // A task-launched mesh program takes the generic task stage, last, specialized for this program.
                if (description.VertexStage.MeshTask is { } task)
                {
                    if (!_supportsTaskShader || expandRectangles || description.VertexStage.Stage != ShaderStageKind.Mesh)
                    {
                        throw SubmissionScheduler.Fatal("A task-launched mesh program needs task shaders and a mesh pipeline.");
                    }

                    shaderStages[stageCount++] = TaskStageInfo(task, taskEntries, taskValues, &taskSpecialization, entryPoint);
                }

                var vertexBindings = new VertexInputBindingDescription[description.VertexInput.BindingCount];
                var vertexAttributes = new VertexInputAttributeDescription[description.VertexInput.AttributeCount];
                BuildVertexAttributes(description, vertexAttributes, vertexBindings);
                var colorCount = (int)parameters.ColorCount;
                var blends = new PipelineColorBlendAttachmentState[colorCount];
                for (var index = 0; index < colorCount; index++)
                {
                    var mask = parameters.GetColorMask(index);
                    if ((mask & ~0xFu) != 0)
                    {
                        throw SubmissionScheduler.Fatal($"A color write mask has unknown bits: attachment={index} mask=0x{mask:X}.");
                    }

                    var separateAlpha = parameters.GetSeparateAlphaBlend(index);
                    var dualSource = parameters.GetBlendEnable(index) && !parameters.GetBlendBypass(index) &&
                        (parameters.GetColorSourceBlend(index) is >= 15 and <= 18 ||
                         parameters.GetColorDestinationBlend(index) is >= 15 and <= 18 ||
                         (separateAlpha && (parameters.GetAlphaSourceBlend(index) is >= 15 and <= 18 ||
                                            parameters.GetAlphaDestinationBlend(index) is >= 15 and <= 18)));
                    if (dualSource && (!_supportsDualSourceBlend ||
                        (uint)colorCount > _shaderDescriptorLimits.MaxFragmentDualSrcAttachments))
                    {
                        throw SubmissionScheduler.Fatal("The device cannot support the requested dual-source blend attachments.");
                    }
                    blends[index] = new PipelineColorBlendAttachmentState
                    {
                        ColorWriteMask = RecordingHost.ToVkColorWriteMask(mask),
                        BlendEnable = parameters.GetBlendEnable(index) && !parameters.GetBlendBypass(index),
                        SrcColorBlendFactor = RecordingHost.ToVkBlendFactor(parameters.GetColorSourceBlend(index)),
                        DstColorBlendFactor = RecordingHost.ToVkBlendFactor(parameters.GetColorDestinationBlend(index)),
                        ColorBlendOp = RecordingHost.ToVkBlendOp(parameters.GetColorBlendFunction(index)),
                        SrcAlphaBlendFactor = RecordingHost.ToVkBlendFactor(separateAlpha ? parameters.GetAlphaSourceBlend(index) : parameters.GetColorSourceBlend(index)),
                        DstAlphaBlendFactor = RecordingHost.ToVkBlendFactor(separateAlpha ? parameters.GetAlphaDestinationBlend(index) : parameters.GetColorDestinationBlend(index)),
                        AlphaBlendOp = RecordingHost.ToVkBlendOp(separateAlpha ? parameters.GetAlphaBlendFunction(index) : parameters.GetColorBlendFunction(index)),
                    };
                }

                var colorFormats = new Format[colorCount];
                Array.Copy(rendering.ColorFormats, colorFormats, colorCount);
                var cullMode = CullModeFlags.None;
                if (parameters.CullBack)
                {
                    cullMode |= CullModeFlags.BackBit;
                }

                if (parameters.CullFront)
                {
                    cullMode |= CullModeFlags.FrontBit;
                }

                fixed (VertexInputBindingDescription* bindingPointer = vertexBindings)
                fixed (VertexInputAttributeDescription* attributePointer = vertexAttributes)
                fixed (PipelineColorBlendAttachmentState* blendPointer = blends)
                fixed (Format* colorFormatPointer = colorFormats)
                {
                    var vertexInput = new PipelineVertexInputStateCreateInfo
                    {
                        SType = StructureType.PipelineVertexInputStateCreateInfo,
                        VertexBindingDescriptionCount = (uint)vertexBindings.Length,
                        PVertexBindingDescriptions = vertexBindings.Length == 0 ? null : bindingPointer,
                        VertexAttributeDescriptionCount = (uint)vertexAttributes.Length,
                        PVertexAttributeDescriptions = vertexAttributes.Length == 0 ? null : attributePointer,
                    };
                    var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                    {
                        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                        Topology = topology,
                        PrimitiveRestartEnable = parameters.PrimitiveRestartEnable,
                    };
                    var depthClipControl = new PipelineViewportDepthClipControlCreateInfoEXT
                    {
                        SType = StructureType.PipelineViewportDepthClipControlCreateInfoExt,
                        NegativeOneToOne = parameters.NegativeOneToOne,
                    };
                    var viewportState = new PipelineViewportStateCreateInfo
                    {
                        SType = StructureType.PipelineViewportStateCreateInfo,
                        PNext = _supportsDepthClipControl ? &depthClipControl : null,
                        ViewportCount = 1,
                        ScissorCount = 1,
                    };
                    var depthClip = new PipelineRasterizationDepthClipStateCreateInfoEXT
                    {
                        SType = StructureType.PipelineRasterizationDepthClipStateCreateInfoExt,
                        DepthClipEnable = parameters.DepthClipEnable,
                    };
                    var rasterization = new PipelineRasterizationStateCreateInfo
                    {
                        SType = StructureType.PipelineRasterizationStateCreateInfo,
                        PNext = _supportsDepthClipEnable ? &depthClip : null,
                        PolygonMode = polygonMode,
                        CullMode = cullMode,
                        FrontFace = parameters.FrontFaceClockwise ? FrontFace.Clockwise : FrontFace.CounterClockwise,
                        LineWidth = 1,
                    };
                    var multisample = new PipelineMultisampleStateCreateInfo
                    {
                        SType = StructureType.PipelineMultisampleStateCreateInfo,
                        SampleShadingEnable = parameters.SampleShadingEnable,
                        RasterizationSamples = ImageDescription.VulkanSampleCount(parameters.Samples),
                        MinSampleShading = 1f,
                    };
                    var colorBlend = new PipelineColorBlendStateCreateInfo
                    {
                        SType = StructureType.PipelineColorBlendStateCreateInfo,
                        LogicOp = LogicOp.Copy,
                        AttachmentCount = (uint)blends.Length,
                        PAttachments = blends.Length == 0 ? null : blendPointer,
                    };
                    var depthStencil = new PipelineDepthStencilStateCreateInfo
                    {
                        SType = StructureType.PipelineDepthStencilStateCreateInfo,
                        DepthBoundsTestEnable = _supportsDepthBounds && parameters.DepthBoundsTestEnable,
                        StencilTestEnable = parameters.StencilTestEnable,
                        Front = ToVkStencilOpState(parameters.StencilFront),
                        Back = ToVkStencilOpState(parameters.StencilBack),
                        MinDepthBounds = 0f,
                        MaxDepthBounds = 1f,
                    };
                    var baseStates = BaseDynamicStates;
                    var dynamicStates = stackalloc DynamicState[baseStates.Length + 1];
                    for (var index = 0; index < baseStates.Length; index++)
                    {
                        dynamicStates[index] = baseStates[index];
                    }

                    var dynamicStateCount = (uint)baseStates.Length;
                    var baseDynamicStateCount = dynamicStateCount;
                    // Last so a pipeline without color attachments can leave it out.
                    if (_colorWriteEnableApi is not null && colorCount != 0)
                    {
                        dynamicStates[dynamicStateCount++] = DynamicState.ColorWriteEnableExt;
                    }

                    var dynamicState = new PipelineDynamicStateCreateInfo
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = dynamicStateCount,
                        PDynamicStates = dynamicStates,
                    };
                    var baseDynamicState = dynamicState with { DynamicStateCount = baseDynamicStateCount };
                    var renderingInfo = new PipelineRenderingCreateInfo
                    {
                        SType = StructureType.PipelineRenderingCreateInfo,
                        ColorAttachmentCount = (uint)colorCount,
                        PColorAttachmentFormats = colorCount == 0 ? null : colorFormatPointer,
                        DepthAttachmentFormat = rendering.DepthFormat,
                        StencilAttachmentFormat = rendering.StencilFormat,
                    };
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        PNext = &renderingInfo,
                        StageCount = stageCount,
                        PStages = shaderStages,
                        PVertexInputState = description.VertexStage.Stage == ShaderStageKind.Mesh ? null : &vertexInput,
                        PInputAssemblyState = description.VertexStage.Stage == ShaderStageKind.Mesh ? null : &inputAssembly,
                        PTessellationState = description.Tessellation is null ? null : &tessellationState,
                        PViewportState = &viewportState,
                        PRasterizationState = &rasterization,
                        PMultisampleState = &multisample,
                        PDepthStencilState = parameters.WithDepth ? &depthStencil : null,
                        PColorBlendState = &colorBlend,
                        PDynamicState = &dynamicState,
                        Layout = layout,
                    };
                    var reportStart = Stopwatch.GetTimestamp();
                    var cache = prewarm ? _pipelineCache
                        : worker is not null ? ResolveGuestPipelineCache(worker.CacheSource)
                        : GetGuestPipelineCache(GraphicsCacheKey(description.VertexStage.Hash,
                            description.PixelStage?.Hash ?? 0, vertexModule.Handle, pixelModule.Handle));
                    Result creationResult;
                    Pipeline pipeline;
                    double creationMilliseconds;
                    if (worker is not null)
                    {
                        creationResult = LinkOrBuildGraphicsPipeline(libraries, description, expandRectangles, &pipelineInfo,
                            &baseDynamicState, cache, out pipeline, out creationMilliseconds);
                    }
                    else
                    {
                        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.GraphicsPipelineDriver))
                        {
                            creationResult = LinkOrBuildGraphicsPipeline(libraries, description, expandRectangles, &pipelineInfo,
                                &baseDynamicState, cache, out pipeline, out creationMilliseconds);
                        }
                    }
                    if (RenderPhaseProfile.Enabled && !prewarm)
                    {
                        Console.Error.WriteLine($"[PERF][PIPELINE_DRIVER] kind=graphics stage={description.VertexStage.Stage} vertex=0x{description.VertexStage.Hash:X16} pixel=0x{description.PixelStage?.Hash ?? 0:X16} topology={topology} driver_ms={creationMilliseconds:F3} result={creationResult}");
                    }
                    Check(creationResult,
                        $"vkCreateGraphicsPipelines(rendering vs=0x{description.VertexStage.Hash:X16} ps=0x{description.PixelStage?.Hash ?? 0:X16})");
                    if (worker is not null || prewarm)
                    {
                        return pipeline;
                    }

                    ReportPipelineCreation(
                        (long)Stopwatch.GetElapsedTime(reportStart).TotalMilliseconds,
                        "graphics",
                        $"vs=0x{description.VertexStage.Hash:X16} ps=0x{description.PixelStage?.Hash ?? 0:X16}",
                        string.Join(
                            '/',
                            Enumerable
                                .Range(0, (int)stageCount)
                                .Select(index => SpirvBytesOf(shaderStages[index].Module.Handle))));
                    MarkPipelineCacheDirty();
                    Interlocked.Increment(ref _perfPipelineCreations);
                    SetDebugName(
                        ObjectType.Pipeline,
                        pipeline.Handle,
                        $"SharpEmu graphics vs=0x{description.VertexStage.Hash:X16} ps=0x{description.PixelStage?.Hash ?? 0:X16} colors={colorCount}");
                    return pipeline;
                }
            }
            finally
            {
                if (rectangleModule.Handle != 0) _vk.DestroyShaderModule(_device, rectangleModule, null);
                if (rectangleVertexModule.Handle != 0) _vk.DestroyShaderModule(_device, rectangleVertexModule, null);
                if (rectanglePixelModule.Handle != 0) _vk.DestroyShaderModule(_device, rectanglePixelModule, null);
                SilkMarshal.Free((nint)entryPoint);
            }
        }

        public PipelineHandle CreateComputePipeline(ComputePipelineDescription description)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineSetup);
            var bindings = new List<DescriptorSetLayoutBinding>();
            CollectLayoutBindings(bindings, description.Stage, ShaderStage.Compute);
            var setLayout = CreateDescriptorSetLayout(bindings, out var usesPushDescriptors, out var demand);
            var bindlessHeap = UsesBindlessHeap(description.Stage);
            var layout = CreatePipelineLayout(setLayout, ShaderStageFlags.ComputeBit, bindlessHeap);
            var computeModule = new ShaderModule(description.Program.Module);
            if (computeModule.Handle == 0)
            {
                throw SubmissionScheduler.Fatal($"The compute pipeline has no module: hash=0x{description.Stage.Hash:X16}.");
            }

            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            Pipeline pipeline;
            try
            {
                var pin = ComputeRequiredSubgroupSize(description.Input);
                var requiredSubgroupSize = new PipelineShaderStageRequiredSubgroupSizeCreateInfo
                {
                    SType = StructureType.PipelineShaderStageRequiredSubgroupSizeCreateInfo,
                    RequiredSubgroupSize = pin,
                };
                var stageInfo = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    PNext = pin != 0 ? &requiredSubgroupSize : null,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = computeModule,
                    PName = entryPoint,
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stageInfo,
                    Layout = layout,
                };
                var reportStart = Stopwatch.GetTimestamp();
                var cache = GetGuestPipelineCache(ComputeCacheKey(description.Stage.Hash, computeModule.Handle));
                Result creationResult;
                double creationMilliseconds;
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ComputePipelineDriver))
                {
                    creationResult = CreatePipelineWithBinaries(&pipelineInfo, &pipelineInfo.PNext, cache,
                        &CreateComputePipelineCall, out pipeline, out creationMilliseconds);
                }
                if (RenderPhaseProfile.Enabled)
                {
                    Console.Error.WriteLine($"[PERF][PIPELINE_DRIVER] kind=compute compute=0x{description.Stage.Hash:X16} driver_ms={creationMilliseconds:F3} result={creationResult}");
                }
                Check(creationResult, $"vkCreateComputePipelines(rendering) hash=0x{description.Stage.Hash:X16}");
                ReportPipelineCreation(
                    (long)Stopwatch.GetElapsedTime(reportStart).TotalMilliseconds,
                    "compute",
                    $"cs=0x{description.Stage.Hash:X16}",
                    SpirvBytesOf(computeModule.Handle).ToString());
                MarkPipelineCacheDirty();
                Interlocked.Increment(ref _perfPipelineCreations);
                SetDebugName(ObjectType.Pipeline, pipeline.Handle, $"SharpEmu compute cs=0x{description.Stage.Hash:X16}");
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
            }

            var entry = new RenderPipelineEntry
            {
                Pipeline = pipeline,
                Layout = layout,
                SetLayout = setLayout,
                Demand = demand,
                UsesPushDescriptors = usesPushDescriptors,
                UsesBindlessHeap = bindlessHeap,
                ProfileComputeHash = description.Stage.Hash,
            };
            return RegisterPipeline(entry);
        }

        // One compute pipeline whose vkCreateComputePipelines call runs on a worker
        // thread. Everything the command stream owns (descriptor and pipeline layout,
        // the module handle) is prepared before the task starts. The worker loads
        // its optional driver-cache shard and creates the native pipeline.
        private sealed class PendingComputePipeline
        {
            public required DescriptorSetLayout SetLayout;
            public required PipelineLayout Layout;
            public required DescriptorSetDemand Demand;
            public required bool UsesPushDescriptors;
            public required bool UsesBindlessHeap;
            public required ulong Hash;
            public required int SpirvBytes;
            public Task<Pipeline> Compile = Task.FromResult(default(Pipeline));
            public long StartTimestamp;
            public long CompileMilliseconds;
            public long CompletedTimestamp;

            // A linked program's own pipeline cache (CreateLinkedPipelineCache), written once its pipeline is registered.
            public DriverCacheShard? LinkedCache;

            // A linked program (Bindings.CallTargetCount != 0): its compile waits for LinkedCompileAdmission.
            public bool Linked;
        }

        private readonly Dictionary<ulong, PendingComputePipeline> _pendingComputePipelines = new();

        // The compile in flight for each linked guest program (Bindings.CallTargetCount != 0), by program hash.
        private readonly Dictionary<ulong, Task> _linkedComputeCompiles = new();

        public Task? GetComputePipelineCompletion(ShaderProgram program) =>
            _pendingComputePipelines.TryGetValue(program.Id, out var pending) ? pending.Compile : null;
        private static readonly SemaphoreSlim _computeCompileSlots =
            new(Math.Max(2, Environment.ProcessorCount / 2));

        // A linked compile starts only when LinkedCompileAdmission admits it (free commit for its estimate beside
        // the linked compiles already running, one large one at a time). Until then the fallback that serves a pending
        // linked program keeps serving it. Shutdown abandons the ones still waiting.
        private static readonly LinkedCompileAdmission _linkedCompileAdmission =
            new(SharpEmu.Logging.HostSystemInfo.AvailableCommitBytes);
        private volatile bool _linkedAdmissionClosed;

        // Waits until the compile is admitted; false when shutdown abandons it. Without mayWait it answers at once (the
        // prewarm's linked records). An admitted compile calls ReleaseLinkedAdmission when it ends.
        private bool WaitForLinkedAdmission(ulong hash, long spirvBytes, bool mayWait = true)
        {
            var reported = false;
            while (!_linkedAdmissionClosed)
            {
                if (_linkedCompileAdmission.TryAdmit(spirvBytes, out var available, out var reserved))
                {
                    if (reported)
                    {
                        try
                        {
                            Console.Error.WriteLine($"[GPU][LINKED_ADMISSION] admitted hash=0x{hash:X16} available_gb={available / (double)(1L << 30):F1} " +
                                $"reserved_gb={reserved / (double)(1L << 30):F1}");
                        }
                        catch
                        {
                            ReleaseLinkedAdmission(spirvBytes);
                            throw;
                        }
                    }

                    return true;
                }

                if (!mayWait)
                {
                    return false;
                }

                if (!reported)
                {
                    reported = true;
                    var needed = LinkedCompileAdmission.Estimate(spirvBytes) + LinkedCompileAdmission.MarginBytes;
                    Console.Error.WriteLine($"[GPU][LINKED_ADMISSION] waiting hash=0x{hash:X16} spirv_mb={spirvBytes / 1048576.0:F1} " +
                        $"needed_gb={needed / (double)(1L << 30):F1} available_gb={available / (double)(1L << 30):F1} " +
                        $"reserved_gb={reserved / (double)(1L << 30):F1}");
                }

                Thread.Sleep(500);
            }

            return false;
        }

        private static void ReleaseLinkedAdmission(long spirvBytes) => _linkedCompileAdmission.Release(spirvBytes);

        // A host shader compiler can spend tens of seconds on one large translated
        // program. Running the compile on a worker thread keeps it out of the pipeline
        // cache's lock, so other queues can still create their own pipelines meanwhile;
        // SHARPEMU_ASYNC_COMPUTE_PIPELINES=0 compiles inline instead.
        private static readonly bool _asyncComputePipelines =
            !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_ASYNC_COMPUTE_PIPELINES"),
                "0",
                StringComparison.Ordinal);

        public bool TryCreateComputePipeline(ComputePipelineDescription description, out PipelineHandle handle)
        {
            lock (_pendingComputePipelines)
            {
                return TryCreateComputePipelineCore(description, out handle);
            }
        }

        private bool TryCreateComputePipelineCore(ComputePipelineDescription description, out PipelineHandle handle)
        {
            if (!_asyncComputePipelines)
            {
                handle = CreateComputePipeline(description);
                return true;
            }

            handle = default;
            var key = description.Program.Id;
            if (_pendingComputePipelines.TryGetValue(key, out var pending))
            {
                if (!pending.Compile.IsCompleted)
                {
                    return false;
                }

                var compiled = pending.Compile.GetAwaiter().GetResult();
                ComputeCompileProfile.Record(ComputeCompileProfile.Part.ConsumeDelay, pending.CompletedTimestamp);
                ReportPipelineCreation(
                    Interlocked.Read(ref pending.CompileMilliseconds),
                    "compute-async",
                    $"cs=0x{pending.Hash:X16} waited_ms={(long)Stopwatch.GetElapsedTime(pending.StartTimestamp).TotalMilliseconds}",
                    pending.SpirvBytes.ToString());
                MarkPipelineCacheDirty();
                Interlocked.Increment(ref _perfPipelineCreations);
                SetDebugName(ObjectType.Pipeline, compiled.Handle, $"SharpEmu compute cs=0x{pending.Hash:X16}");
                handle = RegisterPipeline(new RenderPipelineEntry
                {
                    Pipeline = compiled,
                    Layout = pending.Layout,
                    SetLayout = pending.SetLayout,
                    Demand = pending.Demand,
                    UsesPushDescriptors = pending.UsesPushDescriptors,
                    UsesBindlessHeap = pending.UsesBindlessHeap,
                    ProfileComputeHash = pending.Hash,
                }, () => _pendingComputePipelines.Remove(key));
                ReleaseLinkedPipelineCache(pending.LinkedCache, pending.Hash, save: true);

                return true;
            }

            // A linked program's module can keep the driver busy for minutes, and a game that streams callees relinks
            // the program before an earlier module is done. Each linked program compiles one module at a time, so the
            // modules of tables the game has already replaced cannot take every compiler slot from the programs its
            // queues wait for.
            var linked = description.Stage.Bindings is { CallTargetCount: not 0 };
            if (linked && _linkedComputeCompiles.TryGetValue(description.Stage.Hash, out var running) && !running.IsCompleted)
            {
                return false;
            }

            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineSetup);
            var bindings = new List<DescriptorSetLayoutBinding>();
            CollectLayoutBindings(bindings, description.Stage, ShaderStage.Compute);
            var setLayout = CreateDescriptorSetLayout(bindings, out var usesPushDescriptors, out var demand);
            var bindlessHeap = UsesBindlessHeap(description.Stage);
            var layout = CreatePipelineLayout(setLayout, ShaderStageFlags.ComputeBit, bindlessHeap);
            var computeModule = new ShaderModule(description.Program.Module);
            if (computeModule.Handle == 0)
            {
                throw SubmissionScheduler.Fatal($"The compute pipeline has no module: hash=0x{description.Stage.Hash:X16}.");
            }

            var device = _device;
            var linkedCache = linked ? CreateLinkedPipelineCache(description.Stage.Hash) : null;
            var vk = _vk;
            var requiredSubgroupSize = ComputeRequiredSubgroupSize(description.Input);
            var started = new PendingComputePipeline
            {
                SetLayout = setLayout,
                Layout = layout,
                Demand = demand,
                UsesPushDescriptors = usesPushDescriptors,
                UsesBindlessHeap = bindlessHeap,
                Hash = description.Stage.Hash,
                SpirvBytes = SpirvBytesOf(computeModule.Handle),
                StartTimestamp = Stopwatch.GetTimestamp(),
                LinkedCache = linkedCache,
                Linked = linked,
            };
            // The prewarm's pipeline for this module and layout is taken instead of compiled again. It keeps the layouts
            // it was compiled with, so once its pending entry is published this request's own, defined the same, go; the
            // completed-compile branch above registers it. A publication that throws destroys what was taken.
            if (!linked && TakePrewarmedComputePipeline(computeModule.Handle, bindings, usesPushDescriptors, bindlessHeap,
                    requiredSubgroupSize, out var prewarmed))
            {
                try
                {
                    started.SetLayout = prewarmed.SetLayout;
                    started.Layout = prewarmed.Layout;
                    started.Compile = Task.FromResult(prewarmed.Pipeline);
                    started.CompletedTimestamp = ComputeCompileProfile.Start();
                    _pendingComputePipelines.Add(key, started);
                }
                catch
                {
                    DestroyPrewarmedComputePipeline(prewarmed);
                    throw;
                }

                _vk.DestroyPipelineLayout(_device, layout, null);
                _vk.DestroyDescriptorSetLayout(_device, setLayout, null);
                return TryCreateComputePipeline(description, out handle);
            }

            var cacheSource = linkedCache?.Cache ?? GetGuestPipelineCacheSource(ComputeCacheKey(description.Stage.Hash, computeModule.Handle));
            started.Compile = Task.Factory.StartNew(
                () =>
                {
                    ComputeCompileProfile.Record(ComputeCompileProfile.Part.StartDelay, started.StartTimestamp);
                    // A linked compile first waits for its admission (LinkedCompileAdmission).
                    var linkedAdmitted = started.Linked && WaitForLinkedAdmission(started.Hash, started.SpirvBytes);
                    if (started.Linked && !linkedAdmitted)
                    {
                        return default;
                    }

                    try
                    {
                    // Each compile blocks its thread inside the Metal compiler service,
                    // so the number in flight is bounded instead of one thread per
                    // program the frame happens to touch.
                    var slotStart = ComputeCompileProfile.Start();
                    _computeCompileSlots.Wait();
                    ComputeCompileProfile.Record(ComputeCompileProfile.Part.SlotWait, slotStart);
                    var compileStart = Stopwatch.GetTimestamp();
                    try
                    {
                        // Importing a MoltenVK cache compiles its MSL libraries.
                        // Keep that work inside the same bounded compiler slot.
                        PipelineCache cache;
                        var cacheStart = ComputeCompileProfile.Start();
                        try { cache = ResolveGuestPipelineCache(cacheSource); }
                        finally { ComputeCompileProfile.Record(ComputeCompileProfile.Part.CacheLoad, cacheStart); }
                        var driverStart = ComputeCompileProfile.Start();
                        try { return CompileComputePipeline(vk, device, cache, computeModule, layout, requiredSubgroupSize); }
                        finally { ComputeCompileProfile.Record(ComputeCompileProfile.Part.DriverCompile, driverStart); }
                    }
                    finally
                    {
                        Interlocked.Exchange(
                            ref started.CompileMilliseconds,
                            (long)Stopwatch.GetElapsedTime(compileStart).TotalMilliseconds);
                        _computeCompileSlots.Release();
                        started.CompletedTimestamp = ComputeCompileProfile.Start();
                    }
                    }
                    finally
                    {
                        if (linkedAdmitted) ReleaseLinkedAdmission(started.SpirvBytes);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _pendingComputePipelines.Add(key, started);
            if (linked)
            {
                _linkedComputeCompiles[description.Stage.Hash] = started.Compile;
            }

            return false;
        }

        // vkCreateComputePipelines is the only call here; Vulkan synchronises host
        // access to the pipeline cache internally, so several may run at once.
        private Pipeline CompileComputePipeline(
            Vk vk,
            Device device,
            PipelineCache cache,
            ShaderModule module,
            PipelineLayout layout,
            uint requiredSubgroupSize)
        {
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var subgroupInfo = new PipelineShaderStageRequiredSubgroupSizeCreateInfo
                {
                    SType = StructureType.PipelineShaderStageRequiredSubgroupSizeCreateInfo,
                    RequiredSubgroupSize = requiredSubgroupSize,
                };
                var stageInfo = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    PNext = requiredSubgroupSize != 0 ? &subgroupInfo : null,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entryPoint,
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stageInfo,
                    Layout = layout,
                };
                var result = CreatePipelineWithBinaries(&pipelineInfo, &pipelineInfo.PNext, cache,
                    &CreateComputePipelineCall, out var pipeline, out _);
                if (result != Result.Success)
                {
                    throw SubmissionScheduler.Fatal($"vkCreateComputePipelines(async) failed: {result}.");
                }

                return pipeline;
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
            }
        }

        private void DrainPendingComputePipelines()
        {
            foreach (var pending in _pendingComputePipelines.Values)
            {
                try
                {
                    var pipeline = pending.Compile.GetAwaiter().GetResult();
                    if (pipeline.Handle != 0) _vk.DestroyPipeline(_device, pipeline, null);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[LOADER][WARN] Pending compute pipeline failed during shutdown: {exception.Message}");
                }
                ReleaseLinkedPipelineCache(pending.LinkedCache, pending.Hash, save: false);
                _vk.DestroyPipelineLayout(_device, pending.Layout, null);
                _vk.DestroyDescriptorSetLayout(_device, pending.SetLayout, null);
            }
            _pendingComputePipelines.Clear();
            _linkedComputeCompiles.Clear();
        }

        private void DrainPendingGraphicsPipelines()
        {
            foreach (var pending in _pendingGraphicsPipelines.Values)
            {
                try
                {
                    var pipeline = pending.Compile.GetAwaiter().GetResult();
                    if (pipeline.Handle != 0) _vk.DestroyPipeline(_device, pipeline, null);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[LOADER][WARN] Pending graphics pipeline failed during shutdown: {exception.Message}");
                }
                _vk.DestroyPipelineLayout(_device, pending.Entry.Layout, null);
                _vk.DestroyDescriptorSetLayout(_device, pending.Entry.SetLayout, null);
                _vk.DestroyDescriptorSetLayout(_device, pending.Entry.PixelSetLayout, null);
            }
            _pendingGraphicsPipelines.Clear();
        }

        private void DestroyRenderPipelines()
        {
            _meshIndirectPipeline?.Dispose();
            _meshIndirectPipeline = null;
            foreach (var entry in _pipelineEntries.Values)
            {
                foreach (var pipeline in new[] { entry.Pipeline, entry.StripVariant, entry.ListVariant, entry.RectangleVariant })
                {
                    if (pipeline.Handle != 0)
                    {
                        _vk.DestroyPipeline(_device, pipeline, null);
                    }
                }

                _vk.DestroyPipelineLayout(_device, entry.Layout, null);
                _vk.DestroyDescriptorSetLayout(_device, entry.SetLayout, null);
                _vk.DestroyDescriptorSetLayout(_device, entry.PixelSetLayout, null);
            }

            _pipelineEntries.Clear();
            DestroyGraphicsLibraries();
            foreach (var module in _shaderModules.Values)
            {
                _vk.DestroyShaderModule(_device, module, null);
            }

            _shaderModules.Clear();
            lock (MeshTaskModuleGate)
            {
                if (_meshTaskModule.Handle != 0) _vk.DestroyShaderModule(_device, _meshTaskModule, null);
                _meshTaskModule = default;
            }

            _rectangleStageCode.Clear();
            _shaderModuleCacheIdentities.Clear();
        }
    }
}
