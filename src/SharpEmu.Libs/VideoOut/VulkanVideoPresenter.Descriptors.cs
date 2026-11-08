// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.IO.Hashing;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

// This partial binds the resources of one stage: storage buffers, images, samplers and push data.
internal static unsafe partial class VulkanVideoPresenter
{
    // The shader bounds a storage binding in whole dwords (OpArrayLength), so the view ends on a dword: a 16-bit element
    // in the binding's last partial dword stays in range. A view that would not fit keeps its exact size.
    internal static ulong StorageBufferViewRange(ulong bytes, ulong available, ulong maxRange)
    {
        var rounded = (bytes + 3) & ~3UL;
        return rounded <= available && rounded <= maxRange ? rounded : bytes;
    }

    private static long _stageMemoHits;
    private static long _stageMemoMisses;
    private static long _stageMemoCaptures;
    private static long _stageMemoStale;
    private static long _stageMemoInvalidations;
    private static long _stageMemoAcquireReuses;
    private static long _stageMemoContentHits;
    private static long _stageMemoEvictions;

    internal static string TakeStagePreparationReport() =>
        FormattableString.Invariant($"[PERF][STAGE_MEMO] hits={Interlocked.Exchange(ref _stageMemoHits, 0)} misses={Interlocked.Exchange(ref _stageMemoMisses, 0)} captures={Interlocked.Exchange(ref _stageMemoCaptures, 0)} ") +
        FormattableString.Invariant($"stale={Interlocked.Exchange(ref _stageMemoStale, 0)} invalidations={Interlocked.Exchange(ref _stageMemoInvalidations, 0)} acquire_reuses={Interlocked.Exchange(ref _stageMemoAcquireReuses, 0)} ") +
        FormattableString.Invariant($"content_hits={Interlocked.Exchange(ref _stageMemoContentHits, 0)} evictions={Interlocked.Exchange(ref _stageMemoEvictions, 0)}");

    private sealed class StagePreparationSet
    {
        public StagePreparationMemo? Last;
        public readonly Dictionary<ulong, StagePreparationMemo> ByContent = new();
    }

    private sealed class StagePreparationMemo
    {
        public uint[][] Images = [];
        public uint[][] Samplers = [];
        public uint[][] Buffers = [];
        public DeviceAddressRange[] Ranges = [];
        public ulong ShaderBase;
        public long RegistrationSerial;
        public long Spans;
        public TextureResource[] Textures = [];
        public GuestSpan[] TextureRanges = [];
        public ulong LookupGeneration;
        public Sampler[] SamplerHandles = [];
        public (BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer, ulong MappedSize)[] Sources = [];
        public bool Valid;

        public void Invalidate()
        {
            if (Valid)
            {
                Valid = false;
                Interlocked.Increment(ref _stageMemoInvalidations);
            }
        }
    }

    private sealed partial class RecordingHost
    {
        private const ulong NullStorageBufferBytes = 16;
        private const uint MaxMemoryOffsetAdjustment = 256;
        private const uint TransientDataAlignment = 256;
        private const int MaxImageOccurrences = 64;

        private readonly record struct BufferView(VkBuffer Buffer, ulong Offset, ulong Range);

        private sealed class DescriptorScratch
        {
            public readonly RenderScratchPool<TextureResource> Images = new();
            public readonly RenderScratchPool<Sampler> Samplers = new();
            public readonly RenderScratchPool<byte> SamplerClasses = new();
            public readonly RenderScratchPool<uint> ShaderData = new();
            public readonly RenderScratchPool<BufferView> Buffers = new();
            public readonly RenderScratchPool<(BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer, ulong MappedSize)> Sources = new();
        }

        private DescriptorScratch? _descriptorScratch;
        private DescriptorScratch Scratch => _descriptorScratch ??= new();

        // The host descriptors of one stage in the order its binding layout names them.
        private sealed class StageDescriptors
        {
            public BufferView[] Buffers = [];
            public TextureResource[] Images = [];
            public Sampler[] Samplers = [];
            public BufferView GlobalDataShare;
            public BufferView FlattenedTable;
            public BufferView ShaderData;
            public BufferView CallRecords;
        }

        private sealed class PreparedStageBindings : IPreparedBindings
        {
            private ShaderStageResources _stage;
            private ShaderProgramInfo _program = null!;

            public ShaderStageResources Stage => _stage;

            public ShaderProgramInfo Program => _program;

            public void Reset(ShaderStageResources stage, ShaderProgramInfo program)
            {
                _stage = stage;
                _program = program;
                Memoized = false;
                TexturesKept = false;
                Memo = null;
                Descriptors.GlobalDataShare = default;
                Descriptors.FlattenedTable = default;
                Descriptors.ShaderData = default;
                Descriptors.CallRecords = default;
            }

            public SpecializedResourceInfo Resources => _program.Resources!;

            public BindingLayout Layout => _program.Bindings!;

            public StageDescriptors Descriptors { get; } = new();

            public (BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer, ulong MappedSize)[] BufferSources { get; set; } = [];

            public uint[] ShaderData { get; set; } = [];

            // The call records the shader's call entries search, and the memory their callees read through them
            // (PrepareCallRecords).
            public uint[] CallRecordWords { get; set; } = [];

            public DeviceAddressRange[] CallRecordRanges { get; set; } = [];

            public TextureResource[] Textures => Descriptors.Images;

            public bool Memoized { get; set; }

            public bool TexturesKept { get; set; }

            public StagePreparationMemo? Memo { get; set; }
        }

        private static ShaderStage StageOf(ShaderProgramInfo program) => program.Stage switch
        {
            ShaderStageKind.Vertex => ShaderStage.Vertex,
            ShaderStageKind.Pixel => ShaderStage.Pixel,
            ShaderStageKind.Compute => ShaderStage.Compute,
            ShaderStageKind.Mesh => ShaderStage.Mesh,
            ShaderStageKind.TessellationEvaluation => ShaderStage.TessellationEvaluation,
            _ => throw SubmissionScheduler.Fatal($"The stage kind is unknown: stage={program.Stage} hash=0x{program.Hash:X16}."),
        };

        private static ShaderProgramInfo RequireProgram(ShaderStageResources stage)
        {
            var program = stage.Program ?? throw SubmissionScheduler.Fatal("The stage has no program.");
            if (program.Resources is null || program.Bindings is null)
            {
                throw SubmissionScheduler.Fatal($"The stage program has no resource plan: stage={program.Stage} hash=0x{program.Hash:X16}.");
            }

            return program;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _tracedTextureBindings = new();
        // The last lookup of one image of one program. A draw binds the same descriptor as the
        // previous draw of its program most of the time; while no image the lookup could find has
        // changed (the cache's lookup generation), the request and the image found are the same.
        private struct ImageBindingMemo
        {
            public ulong Generation;
            public ulong Begin;
            public ulong End;
            public TextureRequestResolution Resolution;
            public TextureRequestResolution OriginalResolution;
            public ResourceSlotIdentifier Found;
            public uint MetadataBaseLayer;
            public bool CompletesLookup;
        }

        private readonly record struct ImageBindingKey(ulong Words01, ulong Words23, ulong Words45, ulong Words67, int Length, ShaderImageShape Shape)
        {
            public static ImageBindingKey Of(uint[] words, ShaderImageShape shape)
            {
                Span<uint> padded = stackalloc uint[8];
                words.AsSpan(0, Math.Min(words.Length, 8)).CopyTo(padded);
                return new ImageBindingKey(
                    padded[0] | ((ulong)padded[1] << 32), padded[2] | ((ulong)padded[3] << 32),
                    padded[4] | ((ulong)padded[5] << 32), padded[6] | ((ulong)padded[7] << 32), words.Length, shape);
            }
        }

        private const int ImageBindingMemoCapacity = 1 << 16;
        private Dictionary<ImageBindingKey, ImageBindingMemo>? _imageBindingMemos;

        private readonly record struct BufferSourceMemo(ulong Address, ulong Requested, long RegionStamp, long Spans, ResourceSlotIdentifier Buffer, ulong Size);
        private Dictionary<ShaderProgramInfo, BufferSourceMemo[]>? _bufferSourceMemos;
        private ShaderProgramInfo? _lastBufferMemoProgram;
        private BufferSourceMemo[] _lastBufferMemos = [];

        private BufferSourceMemo[] BufferSourceMemosOf(ShaderProgramInfo program, int count)
        {
            if (!ReferenceEquals(program, _lastBufferMemoProgram) || _lastBufferMemos.Length < count)
            {
                var all = _bufferSourceMemos ??= new Dictionary<ShaderProgramInfo, BufferSourceMemo[]>(ReferenceEqualityComparer.Instance);
                if (!all.TryGetValue(program, out var memos) || memos.Length < count)
                {
                    memos = new BufferSourceMemo[count];
                    all[program] = memos;
                }

                _lastBufferMemoProgram = program;
                _lastBufferMemos = memos;
            }

            return _lastBufferMemos;
        }

        private readonly HashSet<string> _imageLookupTraceEntries = new(StringComparer.Ordinal);

        private void TraceImageLookup(ImageResource image, uint[] words, ShaderProgramInfo program, int index, in ImageRequest request)
        {
            if (!ImageClearTrace.Enabled ||
                !ImageTraceRange.Overlaps(request.Description.Data.Address, 1) ||
                _imageLookupTraceEntries.Count >= 256)
                return;

            var description = request.Description;
            var message = $"ImageLookup shader=0x{program.Hash:X16} stage={program.Stage} image={index} indirectRoot={image.IndirectRoot} " +
                $"address=0x{description.Data.Address:X16} size=0x{description.Data.Size:X} type={description.Type} tile={description.TileMode} " +
                $"extent={description.Extent.Width}x{description.Extent.Height}x{description.Extent.Depth} levels={description.Resources.Levels} layers={description.Resources.Layers} format={description.PixelFormat} " +
                $"view={request.View} words={string.Join(',', words.Select(word => word.ToString("X8")))}";
            if (!_imageLookupTraceEntries.Add(message)) return;
            Console.Error.WriteLine($"[GPU][TRACE] {message}");
            if (_imageLookupTraceEntries.Count == 256)
                Console.Error.WriteLine("[GPU][TRACE] ImageLookup limit=256. Further entries are omitted.");
        }

        // Render-state discovery for one shader image; the view is acquired later with the draw.
        private const int StagePreparationCapacity = 1024;
        private Dictionary<ShaderProgramInfo, StagePreparationSet>? _stagePreparations;
        private XxHash3? _contentHasher;

        private StagePreparationSet StagePreparationsOf(ShaderProgramInfo program)
        {
            var all = _stagePreparations ??= new Dictionary<ShaderProgramInfo, StagePreparationSet>(ReferenceEqualityComparer.Instance);
            if (!all.TryGetValue(program, out var set))
            {
                set = new StagePreparationSet();
                all[program] = set;
            }

            return set;
        }

        private ulong ContentKeyOf(ResourceSnapshot snapshot, ulong shaderBase)
        {
            var hasher = _contentHasher ??= new XxHash3();
            hasher.Reset();
            Span<byte> head = stackalloc byte[sizeof(ulong)];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(head, shaderBase);
            hasher.Append(head);
            foreach (var words in snapshot.Images)
            {
                hasher.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
            }

            foreach (var words in snapshot.Samplers)
            {
                hasher.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
            }

            return hasher.GetCurrentHashAsUInt64();
        }

        private static bool SameWords(uint[][] left, uint[][] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Length; index++)
            {
                if (!ReferenceEquals(left[index], right[index]) && !left[index].AsSpan().SequenceEqual(right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryFindStagePreparation(StagePreparationSet set, ResourceSnapshot snapshot, ulong shaderBase, out StagePreparationMemo memo)
        {
            if (set.Last is { } last && last.ShaderBase == shaderBase &&
                ReferenceEquals(last.Images, snapshot.Images) && ReferenceEquals(last.Samplers, snapshot.Samplers) && IsFreshStagePreparation(last))
            {
                memo = last;
                Interlocked.Increment(ref _stageMemoHits);
                return true;
            }

            var key = ContentKeyOf(snapshot, shaderBase);
            if (set.ByContent.TryGetValue(key, out var found) && found.ShaderBase == shaderBase &&
                SameWords(snapshot.Images, found.Images) && SameWords(snapshot.Samplers, found.Samplers) && IsFreshStagePreparation(found))
            {
                found.Images = snapshot.Images;
                found.Samplers = snapshot.Samplers;
                found.Ranges = snapshot.DeviceAddressRanges;
                set.Last = found;
                memo = found;
                Interlocked.Increment(ref _stageMemoContentHits);
                return true;
            }

            memo = null!;
            return false;
        }

        private bool IsFreshStagePreparation(StagePreparationMemo memo)
        {
            if (!memo.Valid)
            {
                return false;
            }

            var generation = memo.LookupGeneration;
            for (var index = 0; index < memo.Textures.Length; index++)
            {
                var texture = memo.Textures[index];
                var range = memo.TextureRanges[index];
                var checkedGeneration = memo.LookupGeneration;
                if (IsStaleImage(texture.ImageIdentifier, out _) ||
                    !ImageDescription.IsEmptyRange(texture.Request.Description.Data) && !(_worker
                        ? _imageCache.TryRememberedForSlice(texture.ImageIdentifier, ref checkedGeneration, range.Address, range.End)
                        : _imageCache.TryTouchRemembered(texture.ImageIdentifier, ref checkedGeneration, range.Address, range.End)))
                {
                    memo.Invalidate();
                    Interlocked.Increment(ref _stageMemoStale);
                    return false;
                }

                generation = checkedGeneration;
            }

            memo.LookupGeneration = generation;
            return true;
        }

        private uint[] AssembleShaderData(ShaderStageResources stage, ShaderProgramInfo program, BindingLayout layout)
        {
            var snapshot = stage.Resources;
            var shaderData = Scratch.ShaderData.Rent(checked((int)layout.ShaderDataDwordCount));
            for (var index = 0; index < layout.UserDataRegisters.Count; index++)
            {
                var register = layout.UserDataRegisters[index];
                var userIndex = (int)(register - program.UserDataBase);
                if (register < program.UserDataBase || userIndex >= snapshot.UserData.Length)
                {
                    throw SubmissionScheduler.Fatal($"A user register is outside the draw's user data: register={register} base={program.UserDataBase} count={snapshot.UserData.Length} hash=0x{program.Hash:X16}.");
                }

                shaderData[index] = snapshot.UserData[userIndex];
            }

            if (layout.UsesShaderBase)
            {
                shaderData[layout.ShaderBaseDword] = (uint)stage.ShaderBase;
                shaderData[layout.ShaderBaseDword + 1] = (uint)(stage.ShaderBase >> 32);
            }

            stage.WriteDispatchThreadLimits(shaderData);
            stage.WriteMeshDrawParameters(shaderData);
            stage.WriteTessellationData(shaderData);
            stage.WriteCallData(shaderData);
            return shaderData;
        }

        private PreparedStageBindings ReuseStagePreparation(RenderPreparation preparation, ShaderStageResources stage, ShaderProgramInfo program,
            StagePreparationMemo memo)
        {
            var prepared = RentStage(stage, program);
            prepared.Memoized = true;
            prepared.Memo = memo;
            preparation.Stages.Add(prepared);
            var descriptors = prepared.Descriptors;
            descriptors.Images = Scratch.Images.Rent(memo.Textures.Length);
            memo.Textures.CopyTo(descriptors.Images, 0);
            descriptors.Samplers = Scratch.Samplers.Rent(memo.SamplerHandles.Length);
            memo.SamplerHandles.CopyTo(descriptors.Samplers, 0);
            foreach (var texture in memo.Textures)
            {
                CompleteOverlappingPublications(texture.Request.Description.Data.Address,
                    texture.Request.Description.Data.Size, includeLabels: false);
                SynchronizeColorMetadata(texture.Request);
                EnsureBatchedGuestCommands();
                ApplyNativeColorClear(texture.ImageIdentifier, texture.Request);
                BindImage(texture.ImageIdentifier, texture.IsStorage);
            }

            var layout = program.Bindings!;
            prepared.ShaderData = AssembleShaderData(stage, program, layout);
            if (layout.Find(DescriptorBindingKind.GlobalDataShare) is not null)
            {
                descriptors.GlobalDataShare = new BufferView(_bufferCache.GdsBuffer.Handle, 0, Vk.WholeSize);
            }

            FindDeviceAddressBuffers(prepared);
            var spans = GuestGpuMemoryHook.Current?.SpanVersion ?? 0;
            var sameBuffers = memo.Spans == spans && SameWords(memo.Buffers, stage.Resources.Buffers);
            if (sameBuffers)
            {
                var buffers = prepared.Resources.Info.Buffers;
                for (var index = 0; index < memo.Sources.Length; index++)
                {
                    if (memo.Sources[index].MappedSize != 0)
                    {
                        PublishImagesUnderWrite(buffers[index], memo.Sources[index].Descriptor.Address, memo.Sources[index].MappedSize);
                    }
                }
            }

            if (!sameBuffers || memo.RegistrationSerial != _bufferCache.RegistrationSerial)
            {
                FindBuffers(prepared);
                prepared.BufferSources.CopyTo(memo.Sources, 0);
                memo.Buffers = stage.Resources.Buffers;
                memo.RegistrationSerial = _bufferCache.RegistrationSerial;
                memo.Spans = spans;
            }
            else
            {
                prepared.BufferSources = Scratch.Sources.Rent(memo.Sources.Length);
                memo.Sources.CopyTo(prepared.BufferSources, 0);
            }

            return prepared;
        }

        private void NoteStagePreparation(StagePreparationSet set, PreparedStageBindings prepared, ResourceSnapshot snapshot, ulong shaderBase)
        {
            var key = ContentKeyOf(snapshot, shaderBase);
            if (!set.ByContent.TryGetValue(key, out var memo))
            {
                if (set.ByContent.Count >= StagePreparationCapacity)
                {
                    foreach (var entry in set.ByContent.Values)
                    {
                        entry.Invalidate();
                    }

                    set.ByContent.Clear();
                    set.Last = null;
                    Interlocked.Increment(ref _stageMemoEvictions);
                }

                memo = new StagePreparationMemo();
                set.ByContent[key] = memo;
            }

            var images = prepared.Descriptors.Images;
            if (memo.Textures.Length != images.Length)
            {
                foreach (var old in memo.Textures)
                {
                    old.Preparation = null;
                }

                memo.Textures = new TextureResource[images.Length];
                for (var index = 0; index < images.Length; index++)
                {
                    memo.Textures[index] = new TextureResource { Preparation = memo };
                }
            }

            if (memo.TextureRanges.Length != images.Length)
            {
                memo.TextureRanges = new GuestSpan[images.Length];
            }

            for (var index = 0; index < images.Length; index++)
            {
                memo.Textures[index].CopyBindingFrom(images[index]);
                memo.TextureRanges[index] = _imageCache.RememberedRange(images[index].ImageIdentifier, images[index].Request);
            }

            var samplers = prepared.Descriptors.Samplers;
            if (memo.SamplerHandles.Length != samplers.Length)
            {
                memo.SamplerHandles = new Sampler[samplers.Length];
            }

            memo.LookupGeneration = _imageCache.LookupGeneration;

            samplers.CopyTo(memo.SamplerHandles, 0);
            var sources = prepared.BufferSources;
            if (memo.Sources.Length != sources.Length)
            {
                memo.Sources = new (BufferDescriptorWords, ResourceSlotIdentifier, ulong)[sources.Length];
            }

            sources.CopyTo(memo.Sources, 0);
            memo.Images = snapshot.Images;
            memo.Samplers = snapshot.Samplers;
            memo.Buffers = snapshot.Buffers;
            memo.Ranges = snapshot.DeviceAddressRanges;
            memo.ShaderBase = shaderBase;
            memo.RegistrationSerial = _bufferCache.RegistrationSerial;
            memo.Spans = GuestGpuMemoryHook.Current?.SpanVersion ?? 0;
            memo.Valid = true;
            set.Last = memo;
            Interlocked.Increment(ref _stageMemoCaptures);
        }

        private TextureResource ResolveImageBinding(ImageResource image, uint[] words, ShaderProgramInfo program, int index)
        {
            if (words.Length < 4)
            {
                throw SubmissionScheduler.Fatal($"An image descriptor is too short: image={index} words={words.Length} hash=0x{program.Hash:X16}.");
            }

            var storage = image.ResourceClass == ShaderCompiler.Resources.ImageResourceClass.Storage;
            EnsureBatchedGuestCommands();
            if (!_worker && program.Frontend?.PrepareTexture(words) == false)
                words = new uint[8];
            var shape = ImageRequestBuilders.ShapeOf(image);
            var key = ImageBindingKey.Of(words, shape);
            var memos = _imageBindingMemos ??= new Dictionary<ImageBindingKey, ImageBindingMemo>();
            ref var memo = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(memos, key);
            var known = !System.Runtime.CompilerServices.Unsafe.IsNullRef(ref memo);
            var originalResolution = known
                ? memo.OriginalResolution
                : program.Frontend is { } frontend
                    ? frontend.ResolveTexture(words, shape, program.Hash, index)
                    : ImageRequestBuilders.Texture(words, shape, program.Hash, index);
            TraceImageLookup(image, words, program, index, originalResolution.Request);
            CompleteOverlappingPublications(originalResolution.Request.Description.Data.Address,
                originalResolution.Request.Description.Data.Size, includeLabels: false);
            SynchronizeColorMetadata(originalResolution.Request);
            EnsureBatchedGuestCommands();
            TextureRequestResolution resolution;
            ResourceSlotIdentifier imageIdentifier;
            if (known &&
                (_worker
                    ? _imageCache.TryRememberedForSlice(memo.Found, ref memo.Generation, memo.Begin, memo.End)
                    : _imageCache.TryTouchRemembered(memo.Found, ref memo.Generation, memo.Begin, memo.End)))
            {
                GuestImageCache.CountBindingMemo(hit: true);
                resolution = memo.Resolution;
                imageIdentifier = memo.Found;
                if (memo.CompletesLookup)
                {
                    if (_worker)
                    {
                        if (!_imageCache.CompleteLookupIsNoopForSlice(imageIdentifier, resolution.Request))
                        {
                            throw Serial("dcc lookup");
                        }
                    }
                    else
                    {
                        _imageCache.CompleteLookup(imageIdentifier, resolution.Request, memo.MetadataBaseLayer);
                    }
                }
            }
            else
            {
                GuestImageCache.CountBindingMemo(hit: false);
                resolution = originalResolution;
                var request = resolution.Request;
                var metadataBaseLayer = request.View.BaseLayer;
                imageIdentifier = LookUpImage(ref request, resolution.ExactFormat);
                resolution = resolution with { Request = request };
                if (!ImageDescription.IsEmptyRange(request.Description.Data))
                {
                    if (memos.Count >= ImageBindingMemoCapacity)
                    {
                        memos.Clear();
                    }

                    var remembered = _imageCache.RememberedRange(imageIdentifier, request);
                    memos[key] = new ImageBindingMemo
                    {
                        Generation = _imageCache.LookupGeneration,
                        Begin = remembered.Address,
                        End = remembered.End,
                        Resolution = resolution,
                        OriginalResolution = originalResolution,
                        Found = imageIdentifier,
                        MetadataBaseLayer = metadataBaseLayer,
                        CompletesLookup = request.Description.Metadata.Kind == MetadataKind.Dcc ||
                            Image(imageIdentifier).Description.DccSliceSize != 0,
                    };
                }
            }

            _imageCache.TraceTextureMetadata(resolution.Request);
            if (Gpu.Images.ImageClearTrace.Enabled)
            {
                Gpu.Images.ImageTraceRange.NoteFollowedImage(program.Hash, index,
                    resolution.Request.Description.Data.Address, resolution.Request.Description.Data.Size);
                _imageCache.TraceTextureBinding(program.Hash, index, words, resolution.Request);
            }
            if (MeshDrawTrace.Active) MeshDrawTrace.Write("image-binding", $"stage={program.Stage} hash=0x{program.Hash:X16} slot={index} storage={storage} address=0x{resolution.Request.Description.Data.Address:X16} size=0x{resolution.Request.Description.Data.Size:X} view={resolution.Request.View} words={string.Join(",", words.Select(value => value.ToString("X8")))}");
            imageIdentifier = ImageRequestBuilders.ValidateTextureOwner(_imageCache, imageIdentifier, resolution);
            ApplyNativeColorClear(imageIdentifier, resolution.Request);
            if (Gpu.Images.ImageClearTrace.Enabled)
                _imageCache.TraceVolumeClear("resolved-shader-image", resolution.Request, _imageCache.GetImage(imageIdentifier),
                    $"stage={program.Stage} hash=0x{program.Hash:X16} slot={index}");
            BindImage(imageIdentifier, storage);
            var descriptor = new TextureDescriptorWords(words);
            if (Presenter.ShouldTraceTextureBindings() &&
                (!Presenter._traceVolumeTextureBindingsOnly || image.Dimension == ImageDimension.Dim3D))
            {
                var cached = Image(imageIdentifier);
                var description = cached.Description;
                var line =
                    $"TextureBinding stage={program.Stage} hash=0x{program.Hash:X16} index={index} " +
                    $"address=0x{new TextureDescriptorWords(words).BaseAddress:X16} " +
                    $"descriptor={descriptor.BaseAddress:X16} size={descriptor.Width + 1}x{descriptor.Height + 1} " +
                    $"format={(uint)descriptor.Format} tile={(uint)descriptor.TileMode} " +
                    $"image=0x{description.Data.Address:X16} size=0x{description.Data.Size:X} " +
                    $"extent={description.Extent.Width}x{description.Extent.Height} pitch={description.Pitch} " +
                    $"guestFormat={(uint)description.GuestFormat} imageTile={(uint)description.TileMode} " +
                    $"backing={cached.Backing.Extent.Width}x{cached.Backing.Extent.Height} format={cached.Backing.Format} " +
                    $"depth={descriptor.Depth + 1} type={descriptor.Type} dcc={descriptor.MetadataCompress} " +
                    $"metadata=0x{descriptor.MetadataAddress << 8:X} metaKind={description.Metadata.Kind} " +
                    $"words={string.Join(',', words.Select(static word => word.ToString("X8")))}";
                if (!Presenter._traceVolumeTextureBindingsOnly || _tracedTextureBindings.TryAdd(line, 0))
                {
                    Console.Error.WriteLine(line);
                }
            }
            var texture = RentTextureResource();
            texture.Address = descriptor.BaseAddress;
            texture.ImageIdentifier = imageIdentifier;
            texture.Request = resolution.Request;
            texture.IsStorage = storage;
            texture.DestinationSelect = words[3] & 0xFFFu;
            texture.Width = descriptor.Width;
            texture.Height = descriptor.Height;
            return texture;
        }

        // Binding descriptions are large (an image request carries its whole mip layout) and one is made
        // per shader image per draw, so they are reused once the submission that read them has retired.
        // Only the render thread rents and returns them.
        // Sized for the bindings of every draw still in flight (a few frames of a thousand draws each);
        // a smaller cap drops returns during busy scenes and the pool allocates again.
        private const int MaxPooledTextureResources = 32768;
        private Stack<TextureResource>? _texturePool;

        private TextureResource RentTextureResource()
        {
            if (_texturePool is not null && _texturePool.TryPop(out var texture))
            {
                texture.InPool = false;
                return texture;
            }

            return new TextureResource { Pooled = true };
        }

        internal void ReturnTextureResource(TextureResource texture)
        {
            if (!texture.Pooled)
            {
                if (texture.Preparation is not { Valid: true })
                {
                    texture.Reset();
                }

                return;
            }

            _texturePool ??= new Stack<TextureResource>();
            if (texture.InPool || _texturePool.Count >= MaxPooledTextureResources)
            {
                return;
            }

            texture.Reset();
            texture.InPool = true;
            _texturePool.Push(texture);
        }

        // A float view overrides integer views. Unpaired and movie-only samplers stay unclassified.
        private static void ClassifySamplerViews(ShaderResourceInfo info, TextureResource[] images, byte[] classes)
        {
            Array.Clear(classes);
            if (classes.Length == 0) return;
            foreach (var pair in info.SampledPairs)
            {
                if (pair.Sampler >= info.Samplers.Count || pair.Image >= images.Length || images[pair.Image].IsHostMovie)
                {
                    continue;
                }

                classes[pair.Sampler] |= ViewFormatRules.IsIntegerFormat(images[pair.Image].Request.View.Format)
                    ? (byte)1 : (byte)2;
            }
        }

        private Sampler ResolveSampler(SamplerResource sampler, uint[] words, ShaderProgramInfo program, int index, ShaderStageResources stage,
            bool integerView)
        {
            if (words.Length < 4)
            {
                throw SubmissionScheduler.Fatal($"A sampler descriptor is too short: sampler={index} words={words.Length} hash=0x{program.Hash:X16}.");
            }

            Span<uint> native = stackalloc uint[4] { words[0], words[1], words[2], words[3] };
            if (!sampler.DepthCompare)
            {
                native[0] &= ~(0x7u << 12);
            }

            if (sampler.ForcePointFiltering)
            {
                var mipmapped = ((native[2] >> 26) & 0x3u) != 0;
                native[2] &= ~(0xFFu << 20);
                native[2] |= 1u << 24;
                if (mipmapped)
                {
                    native[2] |= 1u << 26;
                }
            }

            var descriptor = new SamplerDescriptorWords(native);
            if (descriptor.MaxAnisotropyRatio > 4 &&
                (descriptor.MagnifyFilter >= (uint)SamplerFilter.AnisotropicPoint ||
                 descriptor.MinifyFilter >= (uint)SamplerFilter.AnisotropicPoint))
            {
                Console.Error.WriteLine(
                    $"[GPU][ERROR] Sampler source: stage={program.Stage} hash=0x{program.Hash:X16} shader=0x{stage.ShaderBase:X16} " +
                    $"sampler={index} source={sampler.Source} pc=0x{sampler.FirstUsePc:X} " +
                    $"descriptor=[{string.Join(",", words.Select(word => $"{word:X8}"))}] " +
                    $"user_data=[{string.Join(",", stage.Resources.UserData.Select(word => $"{word:X8}"))}]");
            }

            return _samplerStore.GetSampler(descriptor, integerView);
        }

        // The guest textures the movie path matches; built only while a decoded frame is active.
        private static List<GuestDrawTexture>? MovieCandidates(SpecializedResourceInfo resources, ResourceSnapshot snapshot)
        {
            var textures = new List<GuestDrawTexture>(resources.Info.Images.Count);
            for (var index = 0; index < resources.Info.Images.Count; index++)
            {
                var words = snapshot.Images[index];
                var storage = resources.Info.Images[index].ResourceClass == ShaderCompiler.Resources.ImageResourceClass.Storage;
                textures.Add(AgcExports.TryDecodeTextureDescriptor(words, out var descriptor)
                    ? new GuestDrawTexture(descriptor.Address, descriptor.Width, descriptor.Height, descriptor.Format, descriptor.NumberType, [], false, storage, DstSelect: descriptor.DstSelect)
                    : new GuestDrawTexture(0, 1, 1, 0, 0, [], true, storage));
            }

            return textures;
        }

        public void UpdateTessellationData(IPreparedBindings prepared, uint[] data)
        {
            var stage = (PreparedStageBindings)prepared;
            (stage.Stage with { TessellationData = data }).WriteTessellationData(stage.ShaderData);
        }

        public IPreparedBindings PrepareBindings(ShaderStageResources stage)
        {
            if (MeshDrawTrace.Active) MeshDrawTrace.Write("stage-binding", $"stage={stage.Program?.Stage} hash=0x{stage.Program?.Hash:X16} shaderBase=0x{stage.ShaderBase:X16} userData={string.Join(",", stage.Resources.UserData.Select(value => value.ToString("X8")))}");
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorPreparation);
            var program = RequireProgram(stage);
            if (_worker && program.Frontend is { RequiresOrderedImagePreparation: true } orderedFrontend)
            {
                foreach (var words in stage.Resources.Images)
                {
                    if (!orderedFrontend.IsTexturePrepared(words, ordered: false, out var source, out var sourceBytes))
                    {
                        throw Serial("frontend image preparation", resumable: true);
                    }

                    if (sourceBytes == 0)
                    {
                        continue;
                    }

                    if (Requests.WritesImageRange(new GuestSpan(source, sourceBytes)))
                    {
                        throw Serial("frontend image preparation", resumable: true);
                    }

                    Requests.FrontendTextures.Add((orderedFrontend, words));
                }
            }

            var preparation = RequirePreparation();
            var resources = program.Resources!;
            var layout = program.Bindings!;
            var snapshot = stage.Resources;
            var info = resources.Info;
            if (layout.UsesBindlessHeap)
            {
                if (_worker)
                {
                    throw Serial("bindless heap", resumable: true);
                }

                _presenter.PrepareBindlessHeap(preparation);
            }

            if (layout.Find(DescriptorBindingKind.CallMarks) is not null)
            {
                if (_worker)
                {
                    throw Serial("call marks", resumable: true);
                }

                _presenter.PollCallMarks();
            }

            using var stallProfile = DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.Prepare, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count);
            if (snapshot.Images.Length != info.Images.Count || snapshot.Samplers.Length != info.Samplers.Count || snapshot.Buffers.Length != info.Buffers.Count)
            {
                throw SubmissionScheduler.Fatal(
                    $"The resource snapshot does not match the program: hash=0x{program.Hash:X16} images={snapshot.Images.Length}/{info.Images.Count} " +
                    $"samplers={snapshot.Samplers.Length}/{info.Samplers.Count} buffers={snapshot.Buffers.Length}/{info.Buffers.Count}.");
            }

            var frontendReady = true;
            if (!_worker && program.Frontend is { RequiresOrderedImagePreparation: true } frontend)
            {
                foreach (var words in snapshot.Images)
                    frontendReady &= frontend.PrepareTexture(words);
            }

            var preparations = StagePreparationsOf(program);
            var movieActive = _presenter._hostMovieFramePixels is not null;
            if (movieActive && _worker)
            {
                throw Serial("host movie");
            }

            if (frontendReady && !movieActive && !Gpu.Images.ImageClearTrace.Enabled && !MeshDrawTrace.Active &&
                TryFindStagePreparation(preparations, snapshot, stage.ShaderBase, out var memo))
            {
                return ReuseStagePreparation(preparation, stage, program, memo);
            }

            Interlocked.Increment(ref _stageMemoMisses);
            var prepared = RentStage(stage, program);
            // Register ownership before preparation can fail halfway through.
            preparation.Stages.Add(prepared);
            var descriptors = prepared.Descriptors;
            descriptors.Images = Scratch.Images.Rent(info.Images.Count);
            var movieCandidates = _presenter._hostMovieFramePixels is null ? null : MovieCandidates(resources, snapshot);
            var hostMovie = movieCandidates is null ? Presenter.HostMovieTextureBindings.None : _presenter.FindHostMovieTextureBindings(movieCandidates);
            using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.ResolveImages, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
            for (var index = 0; index < info.Images.Count; index++)
            {
                descriptors.Images[index] = index == hostMovie.Luma
                    ? _presenter.CreateHostMovieTextureResource(movieCandidates![index], plane: 0)
                    : index == hostMovie.Chroma
                        ? _presenter.CreateHostMovieTextureResource(movieCandidates![index], plane: 1)
                        : ResolveImageBinding(info.Images[index], snapshot.Images[index], program, index);
            }

            descriptors.Samplers = Scratch.Samplers.Rent(info.Samplers.Count);
            var samplerClasses = Scratch.SamplerClasses.Rent(info.Samplers.Count);
            try
            {
                using var samplerProfile = DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.ResolveSamplers, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count);
                ClassifySamplerViews(info, descriptors.Images, samplerClasses);
                for (var index = 0; index < info.Samplers.Count; index++)
                {
                    descriptors.Samplers[index] = ResolveSampler(info.Samplers[index], snapshot.Samplers[index], program, index, stage,
                        samplerClasses[index] == 1);
                }
            }
            finally
            {
                Scratch.SamplerClasses.Return(samplerClasses);
            }

            var shaderData = AssembleShaderData(stage, program, layout);
            prepared.ShaderData = shaderData;
            if (layout.Find(DescriptorBindingKind.GlobalDataShare) is not null)
            {
                descriptors.GlobalDataShare = new BufferView(_bufferCache.GdsBuffer.Handle, 0, Vk.WholeSize);
            }

            if (program.CallRecordCallees.Count != 0)
            {
                if (_worker)
                {
                    throw Serial("call records", resumable: true);
                }

                (prepared.CallRecordWords, prepared.CallRecordRanges) = _presenter.PrepareCallRecords(program, stage);
            }

            using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.FindAddressBuffers, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                FindDeviceAddressBuffers(prepared);
            using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.FindBuffers, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                FindBuffers(prepared);
            using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.ValidateImages, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                ValidateDrawImageTypes(prepared);
            if (!movieActive && layout.CallTargetCount == 0 && layout.CallArgumentCount == 0 && !layout.UsesBindlessHeap)
            {
                if (frontendReady)
                    NoteStagePreparation(preparations, prepared, snapshot, stage.ShaderBase);
            }

            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write(
                    $"Bindings prepared stage={program.Stage} hash=0x{program.Hash:X16} buffers={info.Buffers.Count} images={info.Images.Count} " +
                    $"samplers={info.Samplers.Count} userData={snapshot.UserData.Length} flattened={snapshot.FlattenedResourceTable.Length} shaderData={shaderData.Length}");
            }

            return prepared;
        }

        // Reject incompatible draw views before buffer writes or image transitions are recorded.
        private void ValidateDrawImageTypes(PreparedStageBindings prepared)
        {
            if (prepared.Program.Stage == ShaderStageKind.Compute)
            {
                return;
            }

            foreach (var binding in prepared.Textures)
            {
                if (binding.IsHostMovie || IsStaleImage(binding.ImageIdentifier, out var image) || image is null)
                {
                    continue;
                }

                var view = binding.IsStorage ? binding.Request.View with { LevelCount = 1 } : binding.Request.View;
                if (!image.SupportsViewType(view))
                {
                    throw new DrawImageTypeMismatchException(
                        prepared.Program.Hash, binding.Address, image.Backing.ImageType, view.Type);
                }
            }
        }

        private static IEnumerable<DeviceAddressRange> DeviceAddressRangesOf(PreparedStageBindings prepared) =>
            prepared.CallRecordRanges.Length == 0
                ? prepared.Stage.Resources.DeviceAddressRanges
                : prepared.Stage.Resources.DeviceAddressRanges.Concat(prepared.CallRecordRanges);

        private const int FoundRangeSetSlots = 16;
        private (DeviceAddressRange[]? Ranges, long RegionStamp, long Spans)[]? _foundRangeSets;
        private int _foundRangeSetNext;

        private void FindDeviceAddressBuffers(PreparedStageBindings prepared)
        {
            DeviceAddressRange[] ranges = prepared.CallRecordRanges.Length == 0
                ? prepared.Stage.Resources.DeviceAddressRanges
                : [.. prepared.Stage.Resources.DeviceAddressRanges, .. prepared.CallRecordRanges];
            if (ranges.Length == 0)
            {
                return;
            }

            var spans = GuestGpuMemoryHook.Current?.SpanVersion ?? 0;
            var foundSets = _worker ? null : _foundRangeSets ??= new (DeviceAddressRange[]?, long, long)[FoundRangeSetSlots];
            var found = false;
            if (foundSets is not null)
            {
                long? stamp = null;
                foreach (var set in foundSets)
                {
                    if (set.Ranges is { } known && set.Spans == spans &&
                        (ReferenceEquals(known, ranges) || known.AsSpan().SequenceEqual(ranges)) &&
                        set.RegionStamp == (stamp ??= RegionStampOf(ranges)))
                    {
                        _foundSetsHit.Increment();
                        found = true;
                        break;
                    }
                }
            }

            foreach (var range in ranges)
            {
                if ((found && !range.Written) || !range.Planned || range.Size == 0 ||
                    range.Base >= PageOwnerTable.AddressSpaceSize || range.Size > PageOwnerTable.AddressSpaceSize - range.Base ||
                    (!range.Written && !_guestMemory.CanRead(range.Base, 1)))
                {
                    continue;
                }

                var size = ClampMappedSize(range.Base, range.Size);
                if (range.Written)
                {
                    PublishImagesUnderWrite(formatted: false, range.Base, size);
                }

                if (!found)
                {
                    _ = FindBuffer(range.Base, size);
                }
            }

            if (!found && foundSets is not null)
            {
                foundSets[_foundRangeSetNext] = (ranges, RegionStampOf(ranges), spans);
                _foundRangeSetNext = (_foundRangeSetNext + 1) % FoundRangeSetSlots;
            }
        }

        private static int _unresidentPrtBufferLines;

        private bool IsUnresidentPrtPage(ulong address)
        {
            if (_guestMemory.CanRead(address, 1) || !Kernel.KernelRuntimeCompatExports.ContainsPrtRange(address, 1))
            {
                return false;
            }

            if (Interlocked.Exchange(ref _unresidentPrtBufferLines, 1) == 0)
            {
                Console.Error.WriteLine($"[GPU][INFO] A buffer starts in an unresident PRT page and binds as a null buffer: address=0x{address:X16}.");
            }

            return true;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Hash, int Buffer), byte> _unmappedBufferStarts = new();

        private bool IsUnmappedBufferStart(PreparedStageBindings prepared, int index, ulong address)
        {
            if (_guestMemory.CanRead(address, 1))
            {
                return false;
            }

            if (_unmappedBufferStarts.TryAdd((prepared.Program.Hash, index), 0))
            {
                var resource = prepared.Resources.Info.Buffers[index];
                Console.Error.WriteLine($"[GPU][WARN] A buffer descriptor starts in unmapped memory and binds as a null buffer: " +
                    $"address=0x{address:X16} stage={prepared.Program.Stage} hash=0x{prepared.Program.Hash:X16} " +
                    $"shader=0x{prepared.Stage.ShaderBase:X16} buffer={index} pc=0x{resource.FirstUsePc:X} " +
                    $"written={resource.Written} descriptor=[{string.Join(",", prepared.Stage.Resources.Buffers[index].Select(word => $"{word:X8}"))}] " +
                    $"user_data=[{string.Join(",", prepared.Stage.Resources.UserData.Select(word => $"{word:X8}"))}]" +
                    $"{DescribeUserDataPointers(prepared.Stage.Resources.UserData)}.");
            }

            return true;
        }

        private string DescribeUserDataPointers(uint[] userData)
        {
            var text = new System.Text.StringBuilder();
            Span<uint> words = stackalloc uint[12];
            for (var index = 0; index + 1 < userData.Length; index += 2)
            {
                var pointer = userData[index] | ((ulong)(userData[index + 1] & 0xFFFF) << 32);
                if ((userData[index + 1] & 0xFFFF) == 0 || !_guestMemory.TryRead(pointer, System.Runtime.InteropServices.MemoryMarshal.AsBytes(words)))
                {
                    continue;
                }

                text.Append($" pointer[{index}]=0x{pointer:X}:[{string.Join(",", words.ToArray().Select(word => $"{word:X8}"))}]");
            }

            return text.ToString();
        }

        // Publishing a partly written image can grow a buffer past this range:
        // every stage prepares here before any stage takes a buffer handle.
        private void PublishImagesUnderWrite(BufferResource resource, ulong address, ulong size)
        {
            if (resource.Written)
            {
                PublishImagesUnderWrite(resource.Formatted, address, size);
            }
        }

        private void PublishImagesUnderWrite(bool formatted, ulong address, ulong size)
        {
            if (_worker)
            {
                if (_imageCache.WouldPublishImagesUnderWrite(address, size, wholeImages: formatted))
                {
                    throw Serial("image publication", resumable: true);
                }
            }
            else if (formatted)
            {
                _imageCache.PublishImagesUnderFormattedWrite(address, size);
            }
            else
            {
                _imageCache.PublishImagesUnderRawWrite(address, size);
            }
        }

        private long RegionStampOf(DeviceAddressRange[] ranges)
        {
            var stamp = 0L;
            foreach (var range in ranges)
            {
                stamp += _bufferCache.RegionStamp(range.Base, range.Size);
            }

            return stamp;
        }

        // The cache buffer each descriptor's range lives in; a null descriptor has none.
        private void FindBuffers(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            var snapshot = prepared.Stage.Resources;
            var count = prepared.Resources.Info.Buffers.Count;
            var sources = prepared.BufferSources.Length == count && count != 0 ? prepared.BufferSources : Scratch.Sources.Rent(count);
            prepared.BufferSources = sources;
            var memos = BufferSourceMemosOf(program, sources.Length);
            var spans = GuestGpuMemoryHook.Current?.SpanVersion ?? 0;
            for (var index = 0; index < sources.Length; index++)
            {
                var words = snapshot.Buffers[index];
                if (words.Length < 4)
                {
                    throw SubmissionScheduler.Fatal($"A buffer descriptor is too short: buffer={index} words={words.Length} hash=0x{program.Hash:X16}.");
                }

                var descriptor = BufferDescriptorWords.From(words);
                var requested = descriptor.Footprint() ?? throw SubmissionScheduler.Fatal(
                    $"A storage buffer descriptor footprint overflows: buffer={index} stride={descriptor.Stride} records={descriptor.RecordCount} hash=0x{program.Hash:X16}.");
                if (descriptor.Address == 0 || requested == 0 || IsUnresidentPrtPage(descriptor.Address) ||
                    IsUnmappedBufferStart(prepared, index, descriptor.Address))
                {
                    sources[index] = (descriptor, default, 0);
                    continue;
                }

                ref var memo = ref memos[index];
                var remembered = memo.Address == descriptor.Address && memo.Requested == requested && memo.Spans == spans;
                var size = remembered ? memo.Size : ClampMappedSize(descriptor.Address, requested, prepared, index);
                PublishImagesUnderWrite(prepared.Resources.Info.Buffers[index], descriptor.Address, size);
                if (remembered && memo.RegionStamp == _bufferCache.RegionStamp(descriptor.Address, requested))
                {
                    sources[index] = (descriptor, memo.Buffer, memo.Size);
                    continue;
                }

                var buffer = FindBuffer(descriptor.Address, size);
                sources[index] = (descriptor, buffer, size);
                memo = new BufferSourceMemo(descriptor.Address, requested, _bufferCache.RegionStamp(descriptor.Address, requested), spans, buffer, size);
            }

            prepared.BufferSources = sources;
        }

        // Uploads every mapped range into the cache before a device-address draw; the fault pass follows.
        public void PrepareDeviceAddresses()
        {
            var preparation = RequirePreparation();
            ulong vertexProgramHash = 0, pixelProgramHash = 0, computeProgramHash = 0;
            if (BufferUploadProfile.Enabled)
            {
                foreach (var stage in preparation.Stages)
                {
                    if (!stage.Program.UsesDeviceAddresses) continue;
                    switch (stage.Program.Stage)
                    {
                        case ShaderStageKind.Vertex: vertexProgramHash = stage.Program.Hash; break;
                        case ShaderStageKind.Pixel: pixelProgramHash = stage.Program.Hash; break;
                        case ShaderStageKind.Compute: computeProgramHash = stage.Program.Hash; break;
                    }
                }
            }
            using var profileScope = BufferUploadProfile.BeginSweep(vertexProgramHash, pixelProgramHash, computeProgramHash);
            var memory = GuestGpuMemoryHook.Current ?? throw SubmissionScheduler.Fatal("A device-address program needs the guest GPU memory registry.");
            var spans = DeviceAddressSpans(memory);
            var traceAddress = GuestGpuMemoryHook.TraceAddress;
            if (traceAddress != 0)
            {
                foreach (var stage in preparation.Stages)
                {
                    if (!stage.Program.UsesDeviceAddresses) continue;
                    GuestGpuMemoryHook.Trace(traceAddress, 1,
                        $"device-address-program submission_tick={_scheduler.CurrentTick} stage={stage.Program.Stage} hash=0x{stage.Program.Hash:X16} shader=0x{stage.Stage.ShaderBase:X16} ranges={stage.Stage.Resources.DeviceAddressRanges.Length}");
                    foreach (var range in stage.Stage.Resources.DeviceAddressRanges)
                        GuestGpuMemoryHook.Trace(traceAddress, 1,
                            $"device-address-range submission_tick={_scheduler.CurrentTick} hash=0x{stage.Program.Hash:X16} handle={range.Handle} base=0x{range.Base:X16} size=0x{range.Size:X} planned={range.Planned} written={range.Written}");
                }
            }
            if (traceAddress != 0 && !memory.Covers(traceAddress, 1))
                GuestGpuMemoryHook.Trace(traceAddress, 1,
                    $"device-address-mapping-check readable={_guestMemory.CanRead(traceAddress, 1)} backed={_guestBacking.IsBackedView(traceAddress)}");
            if (_worker)
            {
                Requests.NeedsBdaSweep = true;
                Requests.BdaSpans = spans;
                Requests.BdaMapping = _bdaSpanMapping;
                return;
            }

            foreach (var stage in preparation.Stages)
            {
                if (!stage.Program.UsesDeviceAddresses) continue;
                foreach (var range in stage.Stage.Resources.DeviceAddressRanges)
                {
                    _bufferCache.NoteDeviceAddressWindow(range.Base);
                }
            }

            _bufferCache.PrepareBda(spans, _bdaSpanMapping);
        }

        private List<GuestSpan>? _bdaSpans;
        private GuestGpuMemory? _bdaSpanMemory;
        private long _bdaSpanVersion = -1;
        private ulong _bdaSpanMapping;

        private List<GuestSpan> DeviceAddressSpans(GuestGpuMemory memory)
        {
            var version = memory.SpanVersion;
            if (_bdaSpans is { } cached && ReferenceEquals(_bdaSpanMemory, memory) && _bdaSpanVersion == version)
            {
                return cached;
            }

            var spans = new List<GuestSpan>();
            memory.ForEachSpan((address, size) => spans.Add(new GuestSpan(address, size)));
            _bdaSpans = spans;
            _bdaSpanMemory = memory;
            _bdaSpanVersion = version;
            _bdaSpanMapping = GuestBufferCache.MappingKey(spans);
            return spans;
        }

        public void BindResources(IPreparedBindings prepared)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorPreparation);
            var stage = (PreparedStageBindings)prepared;
            var info = stage.Resources.Info;
            using var stallProfile = DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.Bind, stage.Program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count);
            if (!_worker)
            {
                _bufferCache.BeginUploadBatch();
            }

            try
            {
                using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.BindBuffers, stage.Program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                    BindBuffers(stage);
                using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.ObtainAddressRanges, stage.Program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                    ObtainDeviceAddressRanges(stage);
            }
            finally
            {
                if (!_worker)
                {
                    _bufferCache.EndUploadBatch();
                }
            }

            if (KeepsTextureSet(stage))
            {
                stage.TexturesKept = true;
                Interlocked.Increment(ref _texturesKept);
            }
            else
            {
                using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.BindImages, stage.Program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                    BindImages(stage);
            }

            _preparedTextures.Add(stage.Textures);
        }

        // Textures bound for the draw being prepared; cleared when its preparation closes.
        private readonly List<TextureResource[]> _preparedTextures = new();

        private BufferView NullStorageBuffer() => new(NullBufferHandle, 0, NullStorageBufferBytes);

        // A storage buffer view on the cache buffer, aligned down with the adjustment carried in the memory offsets.
        private BufferView BindStorageBuffer(
            in BufferDescriptorWords descriptor,
            BufferResource resource,
            ShaderProgramInfo program,
            int slot,
            ResourceSlotIdentifier bufferIdentifier,
            ulong size,
            out uint memoryOffset)
        {
            memoryOffset = 0;
            var address = descriptor.Address;
            if (address == 0 || size == 0 || IsUnresidentPrtPage(address) || !_guestMemory.CanRead(address, 1))
            {
                return NullStorageBuffer();
            }

            if (MeshDrawTrace.Active) MeshDrawTrace.Write("buffer-binding", $"stage={program.Stage} hash=0x{program.Hash:X16} slot={slot} address=0x{address:X16} requested=0x{descriptor.Footprint() ?? 0:X} size=0x{size:X} written={resource.Written} formatted={resource.Formatted}");
            var alignment = _presenter._minStorageBufferOffsetAlignment;
            var maxRange = _deviceInfo.MaxStorageBufferRange;
            if (alignment == 0 || size > maxRange)
            {
                throw SubmissionScheduler.Fatal($"A storage buffer range or the device alignment is unsupported: buffer={slot} size=0x{size:X} alignment={alignment} hash=0x{program.Hash:X16}.");
            }

            var (buffer, offset, available) = ObtainStorageBuffer(address, size, resource, bufferIdentifier, program.Hash);
            var bufferHandle = buffer.Handle;
            _presenter.TraceBufferParameter(program.Hash, slot, address, size, bufferHandle.Handle, offset, resource.Written);
            var alignedOffset = offset - offset % alignment;
            // The shader adds the adjustment to its byte address, so a base that is only two byte aligned
            // keeps the byte phase the guest's 16 bit accesses expect.
            var adjustment = offset - alignedOffset;
            if (adjustment >= MaxMemoryOffsetAdjustment || size > maxRange - adjustment)
            {
                throw SubmissionScheduler.Fatal($"A storage buffer offset adjustment is unsupported: buffer={slot} adjustment={adjustment} hash=0x{program.Hash:X16}.");
            }

            memoryOffset = (uint)adjustment;
            return new BufferView(bufferHandle, alignedOffset,
                StorageBufferViewRange(size + adjustment, available + adjustment, maxRange));
        }

        private (GpuBuffer Buffer, ulong Offset, ulong Available) ObtainStorageBuffer(ulong address, ulong size, BufferResource resource,
            ResourceSlotIdentifier bufferIdentifier, ulong programHash)
        {
            var obtained = _worker
                ? ObtainBufferRangeForSlice(address, size, resource.Written, resource.Formatted, invalidateImages: resource.Formatted && resource.Written)
                : (_bufferCache.ObtainBuffer(address, size, resource.Written, isTexelBuffer: resource.Formatted, bufferIdentifier, programHash) is var found
                    ? (found.Buffer, found.Offset, found.Buffer.Size - found.Offset) : default);
            // The images a raw write covers in part, and all images under a formatted write, were published
            // when the stage was prepared (FindBuffers).
            if (!_worker && resource.Written && resource.Formatted)
            {
                _imageCache.InvalidateMemoryFromGpuAfterPublish(address, size);
            }
            else if (!_worker && resource.Written)
            {
                _imageCache.InvalidateMemoryCopiesFromGpu(address, size);
            }

            return obtained;
        }

        private uint[] _tableBlockWords = new uint[64];
        private static long _tableBlocks;
        private static long _descriptorsShadowed;
        private static readonly ThreadCounter _foundSetsHit = new();
        private static readonly ThreadCounter _sliceUseHits = new();
        private static readonly ThreadCounter _sliceUseMisses = new();
        private static readonly ThreadCounter _sliceUseStale = new();

        internal static string TakeTableReport() =>
            FormattableString.Invariant($"table_blocks={Interlocked.Exchange(ref _tableBlocks, 0)} descriptors_shadowed={Interlocked.Exchange(ref _descriptorsShadowed, 0)} ") +
            FormattableString.Invariant($"vsharp_cache=hit:{_sliceUseHits.Take()},miss:{_sliceUseMisses.Take()},stale:{_sliceUseStale.Take()} found_sets_hit={_foundSetsHit.Take()} ");

        private ulong NullBufferAddress => _nullBufferAddress != 0 ? _nullBufferAddress : _nullBufferAddress = _bufferCache.GetBuffer(GuestBufferCache.NullBufferId).DeviceAddress;

        private ulong _nullBufferAddress;

        private void WriteTableBlock(PreparedStageBindings prepared, uint[] shaderData)
        {
            var program = prepared.Program;
            var layout = prepared.Layout;
            var info = prepared.Resources.Info;
            var flattened = layout.UsesFlattenedTable ? prepared.Stage.Resources.FlattenedResourceTable : [];
            var length = (int)layout.TableFlattenedDword + flattened.Length;
            if (_tableBlockWords.Length < length)
            {
                _tableBlockWords = new uint[Math.Max(length, _tableBlockWords.Length * 2)];
            }

            var words = _tableBlockWords.AsSpan(0, length);
            words[0] = (uint)flattened.Length;
            for (var index = 0; index < info.Buffers.Count; index++)
            {
                var (descriptor, bufferIdentifier, size) = prepared.BufferSources[index];
                var record = (int)(BindingLayout.TableBlockHeaderDwords + (uint)index * BindingLayout.TableBufferDwords);
                var address = NullBufferAddress;
                var dwords = 0u;
                var bias = 0u;
                if (descriptor.Address != 0 && size != 0)
                {
                    var (buffer, offset, available) = ObtainStorageBuffer(descriptor.Address, size, info.Buffers[index], bufferIdentifier, program.Hash);
                    _presenter.TraceBufferParameter(program.Hash, index, descriptor.Address, size, buffer.Handle.Handle, offset, info.Buffers[index].Written);
                    var exact = buffer.DeviceAddress + offset;
                    bias = (uint)(exact % sizeof(uint));
                    address = exact - bias;
                    var bytes = StorageBufferViewRange(size + bias, available + bias, ulong.MaxValue);
                    if (bytes >> 2 > uint.MaxValue)
                    {
                        throw SubmissionScheduler.Fatal($"A table buffer size is unsupported: buffer={index} address=0x{exact:X} size=0x{size:X} hash=0x{program.Hash:X16}.");
                    }

                    dwords = (uint)(bytes >> 2);
                }

                words[record] = (uint)address;
                words[record + 1] = (uint)(address >> 32);
                words[record + 2] = dwords;
                words[record + 3] = bias;
            }

            flattened.CopyTo(words[(int)layout.TableFlattenedDword..]);
            Interlocked.Increment(ref _tableBlocks);
            var block = UploadTableBlock(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words), program);
            shaderData[layout.TableBlockDword] = (uint)block;
            shaderData[layout.TableBlockDword + 1] = (uint)(block >> 32);
        }

        private ulong UploadTableBlock(ReadOnlySpan<byte> bytes, ShaderProgramInfo program)
        {
            if (_worker)
            {
                if (!_arena!.TryReserve((ulong)bytes.Length, TableBlockAlignment, out var reservation))
                {
                    throw Serial("arena", resumable: true);
                }

                bytes.CopyTo(reservation.Destination);
                _arena.Commit(in reservation);
                return reservation.Chunk.Buffer.DeviceAddress + reservation.Offset;
            }

            if (_contentUploads is { } arena && arena.TryUploadAddress(bytes, TableBlockAlignment, out var address))
            {
                return address;
            }

            var stream = _bufferCache.GetUtilityBuffer(GpuBufferUsage.Stream);
            if (stream.TryMap((ulong)bytes.Length, out var offset, TableBlockAlignment))
            {
                bytes.CopyTo(stream.Mapped[(int)offset..]);
                stream.Commit();
                return stream.DeviceAddress + offset;
            }

            throw SubmissionScheduler.Fatal($"No upload ring could take a table block: bytes={bytes.Length} hash=0x{program.Hash:X16}.");
        }

        private const uint TableBlockAlignment = 16;

        private BufferView UploadDwords(uint[] data, string label, ShaderProgramInfo program, bool byIdentity)
        {
            if (data.Length == 0)
            {
                throw SubmissionScheduler.Fatal($"The {label} upload is empty: hash=0x{program.Hash:X16}.");
            }

            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>(data);
            var alignment = Math.Max(TransientDataAlignment, (uint)_presenter._minStorageBufferOffsetAlignment);
            if (!_worker && _contentUploads is { } arena &&
                (byIdentity ? arena.TryUpload(data, alignment, out var arenaBuffer, out var arenaOffset) : arena.TryUpload(bytes, alignment, out arenaBuffer, out arenaOffset)))
            {
                return new BufferView(arenaBuffer, arenaOffset, (ulong)bytes.Length);
            }

            var binding = UploadTransient(bytes, alignment);
            return new BufferView(new VkBuffer(binding.Handle), binding.Offset, (ulong)bytes.Length);
        }

        private void BindBuffers(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            var layout = prepared.Layout;
            var info = prepared.Resources.Info;
            var snapshot = prepared.Stage.Resources;
            var shaderData = prepared.ShaderData;
            if (prepared.BufferSources.Length != info.Buffers.Count || shaderData.Length != layout.ShaderDataDwordCount)
            {
                throw SubmissionScheduler.Fatal($"The prepared bindings are stale: hash=0x{program.Hash:X16}.");
            }

            if (layout.UsesTableBlock)
            {
                WriteTableBlock(prepared, shaderData);
            }
            else
            {
                BindBufferViews(prepared, shaderData);
            }

            if (layout.MipStatisticsCount != 0)
            {
                if (_worker && _presenter._lodStatistics is null)
                {
                    throw Serial("lod statistics", resumable: true);
                }

                _presenter.LodStatistics().EnsureInitialized();
                _presenter.WriteMipStatisticsFields(shaderData, layout, info, snapshot.Images, snapshot.MipStatistics);
            }

            if (layout.Find(DescriptorBindingKind.FlattenedResourceTable) is not null)
            {
                prepared.Descriptors.FlattenedTable = UploadDwords(snapshot.FlattenedResourceTable, "flattened resource table", program, byIdentity: true);
            }

            if (layout.Find(DescriptorBindingKind.ShaderData) is not null)
            {
                prepared.Descriptors.ShaderData = UploadDwords(shaderData, "shader data", program, byIdentity: false);
            }

            if (layout.Find(DescriptorBindingKind.CallRecords) is not null)
            {
                prepared.Descriptors.CallRecords = UploadDwords(prepared.CallRecordWords, "call records", program, byIdentity: false);
            }
        }

        private void BindBufferViews(PreparedStageBindings prepared, uint[] shaderData)
        {
            var program = prepared.Program;
            var layout = prepared.Layout;
            var info = prepared.Resources.Info;
            // Reset only packed buffer offsets; dispatch limits follow them in shader data.
            Array.Clear(shaderData, (int)layout.MemoryOffsetDword, (int)((layout.MemoryOffsetCount + 3) / 4));
            var views = prepared.Descriptors.Buffers;
            if (views.Length != info.Buffers.Count)
            {
                Scratch.Buffers.Return(views);
                views = Scratch.Buffers.Rent(info.Buffers.Count);
                prepared.Descriptors.Buffers = views;
            }
            for (var index = 0; index < views.Length; index++)
            {
                var (descriptor, bufferIdentifier, mappedSize) = prepared.BufferSources[index];
                views[index] = BindStorageBuffer(in descriptor, info.Buffers[index], program, index, bufferIdentifier, mappedSize, out var memoryOffset);
                var dword = layout.MemoryOffsetDword + (uint)index / 4;
                var shift = ((uint)index % 4) * 8;
                shaderData[dword] |= memoryOffset << (int)shift;
                if (layout.UsesBufferLengths)
                    shaderData[layout.BufferLengthsDword + (uint)index] = checked((uint)(views[index].Range / sizeof(uint)));
            }

            prepared.Descriptors.Buffers = views;
        }

        // Device-address reads need persistent page-table entries before the shader runs.
        private void ObtainDeviceAddressRanges(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            foreach (var range in DeviceAddressRangesOf(prepared))
            {
                if (!range.Planned)
                {
                    if (!range.Written) continue;
                    throw SubmissionScheduler.Fatal($"A written device-address range cannot be planned: handle={range.Handle} hash=0x{program.Hash:X16}.");
                }

                if (range.Size == 0)
                {
                    continue;
                }

                if (range.Base >= PageOwnerTable.AddressSpaceSize || range.Size > PageOwnerTable.AddressSpaceSize - range.Base)
                {
                    throw SubmissionScheduler.Fatal($"A device-address range is outside the cache: handle={range.Handle} base=0x{range.Base:X16} size=0x{range.Size:X} hash=0x{program.Hash:X16}.");
                }

                // Unmapped read-only pointers resolve to zero through the page table.
                if (!range.Written && !_guestMemory.CanRead(range.Base, 1))
                {
                    continue;
                }

                var size = ClampMappedSize(range.Base, range.Size);
                if (range.Written)
                {
                    if (_worker)
                    {
                        _ = ObtainBufferForSlice(range.Base, size, isWritten: true, isTexelBuffer: false);
                    }
                    else
                    {
                        _ = _bufferCache.ObtainBuffer(range.Base, size, isWritten: true, traceShaderHash: program.Hash);
                        _imageCache.InvalidateMemoryCopiesFromGpu(range.Base, size);
                    }
                }
                else
                {
                    // A held command write or a pending dump lands before the shader reads the range.
                    CompleteOverlappingPublications(range.Base, size, includeLabels: false);
                    // Stream buffers do not populate the device-address page table.
                    _ = FindBuffer(range.Base, size);
                    if (_worker)
                    {
                        Requests.SyncRanges.Add((range.Base, size));
                    }
                    else
                    {
                        _bufferCache.SynchronizeBuffersInRange(range.Base, size);
                    }
                }
            }
        }

        private const int MaxRebindPasses = 3;
        private static readonly uint[] NullImageWords = new uint[8];
        private static int _nullRebindReports;

        // Finding an image again can replace another image of the same dispatch: two bindings over
        // the same memory with layouts one cached image cannot hold at once, where the guest reads
        // two views of one memory. The stale bindings are found again until none is left; one still
        // losing its image after MaxRebindPasses reads as a null image. Returns how many did.
        internal static int RefindStaleBindings(int count, Func<int, bool> isStale, Action<int, bool> findAgain)
        {
            var nulled = 0;
            for (var pass = 0; ; pass++)
            {
                var refound = false;
                for (var index = 0; index < count; index++)
                {
                    if (!isStale(index))
                    {
                        continue;
                    }

                    if (pass > MaxRebindPasses)
                    {
                        throw SubmissionScheduler.Fatal($"An image binding is stale after it was bound as a null image: image={index}.");
                    }

                    nulled += pass == MaxRebindPasses ? 1 : 0;
                    findAgain(index, pass == MaxRebindPasses);
                    refound = true;
                }

                if (!refound)
                {
                    return nulled;
                }
            }
        }

        // Views for every image after the targets are bound; a stale image is found again first.
        private void BindImages(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            var info = prepared.Resources.Info;
            var snapshot = prepared.Stage.Resources;
            var images = prepared.Descriptors.Images;
            var nulled = RefindStaleBindings(
                images.Length,
                index => !images[index].IsHostMovie && IsStaleImage(images[index].ImageIdentifier, out _),
                (index, asNull) =>
                {
                    if (_imageCache.TryGetImage(images[index].ImageIdentifier, out var stale))
                    {
                        BindingOf(stale) = default;
                    }

                    images[index] = ResolveImageBinding(info.Images[index], asNull ? NullImageWords : snapshot.Images[index], program, index);
                    prepared.Memo?.Invalidate();
                });
            if (nulled != 0 && Interlocked.Increment(ref _nullRebindReports) <= 32)
            {
                Console.Error.WriteLine($"[GPU][WARN] {nulled} image binding(s) of 0x{program.Hash:X16} kept losing their image to another binding over the same memory; they read as null images");
            }

            for (var index = 0; index < images.Length; index++)
            {
                var binding = images[index];
                if (binding.IsHostMovie)
                {
                    continue;
                }

                var resource = info.Images[index];
                var view = binding.Request.View;
                if (prepared.Memoized && !binding.IsStorage && binding.View.Handle != 0 && binding.CachedImage is not null &&
                    (_worker ? _imageCache.CanReuseAcquiredForSlice(binding.ImageIdentifier, binding.Image.Handle) : _imageCache.TryReuseAcquired(binding.ImageIdentifier, binding.Image.Handle)))
                {
                    if (_worker)
                    {
                        Requests.AddImage(new ImageSliceUse(binding.ImageIdentifier, binding.CachedImage, binding.Request));
                    }

                    Interlocked.Increment(ref _stageMemoAcquireReuses);
                    continue;
                }

                var image = Image(binding.ImageIdentifier);
                if (binding.IsStorage && image.Description.HasStencil && ViewFormatRules.IsStencilViewFormat(view.Format))
                {
                    image = AcquireStencilStorage(binding, image);
                    binding.Preparation?.Invalidate();
                    binding.Request = binding.Request with { View = view with { LevelCount = 1 } };
                    binding.View = image.GetOrCreateView(binding.Request.View with { Aspect = ImageAspectFlags.ColorBit });
                    binding.MipViews = [];
                }
                else if (resource.MipMode == ImageMipMode.DynamicStorage)
                {
                    if (resource.MipCount == 0 || resource.MipCount != view.LevelCount)
                    {
                        throw SubmissionScheduler.Fatal(
                            $"A storage image's mip count does not match its view: image={index} mips={resource.MipCount} levels={view.LevelCount} hash=0x{program.Hash:X16}.");
                    }

                    var mipViews = new ImageView[resource.MipCount];
                    for (var mip = 0u; mip < resource.MipCount; mip++)
                    {
                        var mipRequest = binding.Request with { View = view with { BaseLevel = view.BaseLevel + mip, LevelCount = 1 } };
                        mipViews[mip] = _worker ? AcquireTextureViewForSlice(binding.ImageIdentifier, in mipRequest) : _imageCache.AcquireTextureView(binding.ImageIdentifier, mipRequest);
                    }

                    binding.MipViews = mipViews;
                    binding.View = mipViews[0];
                }
                else
                {
                    if (binding.IsStorage)
                    {
                        binding.Request = binding.Request with { View = view with { LevelCount = 1 } };
                    }

                    binding.View = _worker ? AcquireTextureViewForSlice(binding.ImageIdentifier, in binding.Request) : _imageCache.AcquireTextureView(binding.ImageIdentifier, binding.Request);
                    binding.MipViews = [];
                }

                image.Uses.Storage |= binding.IsStorage;
                image.Uses.Texture |= !binding.IsStorage;
                binding.CachedImage = image;
                binding.Image = image.Backing.Handle;
            }
        }

        private void DestroyStageBindings(PreparedStageBindings stage)
        {
            foreach (var texture in stage.Textures)
            {
                if (texture is { StagingBuffer.Handle: not 0 })
                {
                    _presenter.RecycleHostBuffer(texture.StagingBuffer, texture.StagingMemory);
                }
            }
        }

        private readonly Stack<PreparedStageBindings> _spareStages = new();

        private PreparedStageBindings RentStage(ShaderStageResources stage, ShaderProgramInfo program)
        {
            var prepared = _spareStages.Count != 0 ? _spareStages.Pop() : new PreparedStageBindings();
            prepared.Reset(stage, program);
            return prepared;
        }

        private void ReturnStageScratch(PreparedStageBindings stage)
        {
            // CommitBindings copies texture references into submission-owned
            // storage. These CPU descriptions are no longer referenced by GPU work.
            Scratch.Images.Return(stage.Descriptors.Images);
            Scratch.Samplers.Return(stage.Descriptors.Samplers);
            Scratch.Buffers.Return(stage.Descriptors.Buffers);
            Scratch.Sources.Return(stage.BufferSources);
            Scratch.ShaderData.Return(stage.ShaderData);
            stage.Descriptors.Images = [];
            stage.Descriptors.Samplers = [];
            stage.Descriptors.Buffers = [];
            stage.BufferSources = [];
            stage.ShaderData = [];
            stage.Reset(default, null!);
            if (_spareStages.Count < 8)
            {
                _spareStages.Push(stage);
            }
        }

        private static DescriptorImageInfo ImageInfo(TextureResource texture, uint element, ShaderProgramInfo program, int index)
        {
            var view = texture.MipViews.Length == 0
                ? (element == 0 ? texture.View : default)
                : (element < (uint)texture.MipViews.Length ? texture.MipViews[element] : default);
            if (view.Handle == 0)
            {
                throw SubmissionScheduler.Fatal($"An image binding has no view for its element: image={index} element={element} hash=0x{program.Hash:X16}.");
            }

            return new DescriptorImageInfo { ImageView = view, ImageLayout = ImageLayout.General };
        }

        // Joins every stage's writes, records the image work and binds the set by push or from the heap.
        public void CommitBindings(PipelineBindPoint bindPoint, in PipelineHandle pipeline, ReadOnlySpan<IPreparedBindings> stages)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorCommit);
            var preparation = RequirePreparation();
            var entry = RequirePipelineEntry(in pipeline);
            var command = BeginBatchedGuestStateCommands();
            _commandBuffer = command;
            var writeCount = 0;
            var bufferCount = 0;
            var imageCount = 0;
            var textureCount = 0;
            var maxStageImages = 0;
            foreach (var prepared in stages)
            {
                var stage = (PreparedStageBindings)prepared;
                var stageFlag = DescriptorWriter.ShaderStageFlag(StageOf(stage.Program));
                if ((bindPoint == PipelineBindPoint.Graphics && (stageFlag & (ShaderStageFlags.VertexBit | ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TessellationEvaluationBit)) == 0) ||
                    (bindPoint == PipelineBindPoint.Compute && stageFlag != ShaderStageFlags.ComputeBit))
                {
                    throw SubmissionScheduler.Fatal($"A stage does not belong to the bind point: stage={stage.Program.Stage} bindPoint={bindPoint}.");
                }

                // Indexed loops over the layout's lists: a foreach on IReadOnlyList allocates an enumerator per draw.
                var countedBindings = stage.Layout.Descriptors;
                for (var bindingIndex = 0; bindingIndex < countedBindings.Count; bindingIndex++)
                {
                    var binding = countedBindings[bindingIndex];
                    writeCount++;
                    var count = (int)DescriptorWriter.DescriptorCount(binding);
                    if (ImageDescriptorBinding.ResourceClass(binding.Kind) != ShaderCompiler.Resources.ImageResourceClass.None || binding.Kind == DescriptorBindingKind.Samplers)
                    {
                        imageCount += count;
                    }
                    else
                    {
                        bufferCount += count;
                    }
                }

                textureCount += stage.Textures.Length;
                maxStageImages = Math.Max(maxStageImages, stage.Textures.Length);
            }

            // All stages that read the same stencil bytes share the shader's working image.
            if (preparation.StencilStorageImages is { } stencilImages)
            {
                foreach (var prepared in stages)
                {
                    foreach (var texture in ((PreparedStageBindings)prepared).Textures)
                    {
                        if (texture.CachedImage is { } attachment && ViewFormatRules.IsStencilViewFormat(texture.Request.View.Format) &&
                            stencilImages.TryGetValue(attachment, out var storage))
                        {
                            texture.Preparation?.Invalidate();
                            texture.CachedImage = storage;
                            texture.Image = storage.Backing.Handle;
                            texture.View = storage.GetOrCreateView(texture.Request.View with { Aspect = ImageAspectFlags.ColorBit });
                            texture.MipViews = [];
                        }
                    }
                }
            }

            // Vulkan consumes these host-side descriptions during the call.
            // Only the resources themselves remain alive until GPU completion.
            // Bound stack use for unusually large descriptor layouts.
            const int StackDescriptorLimit = 128;
            Span<DescriptorBufferInfo> bufferInfos = bufferCount <= StackDescriptorLimit
                ? stackalloc DescriptorBufferInfo[bufferCount] : new DescriptorBufferInfo[bufferCount];
            Span<DescriptorImageInfo> imageInfos = imageCount <= StackDescriptorLimit
                ? stackalloc DescriptorImageInfo[imageCount] : new DescriptorImageInfo[imageCount];
            Span<WriteDescriptorSet> writes = writeCount <= StackDescriptorLimit
                ? stackalloc WriteDescriptorSet[writeCount] : new WriteDescriptorSet[writeCount];
            Span<uint> occurrenceScratch = maxStageImages <= StackDescriptorLimit
                ? stackalloc uint[maxStageImages] : new uint[maxStageImages];
            var pushData = stackalloc uint[(int)PushData.DwordCount];
            var hasPushData = false;
            var bufferIndex = 0;
            var imageIndex = 0;
            var writeIndex = 0;
            var uploadStage = bindPoint == PipelineBindPoint.Compute ? PipelineStageFlags.ComputeShaderBit : PipelineStageFlags.FragmentShaderBit;
            var textures = textureCount == 0 ? [] : System.Buffers.ArrayPool<TextureResource>.Shared.Rent(textureCount);
            var textureIndex = 0;
            fixed (DescriptorBufferInfo* bufferInfoPointer = bufferInfos)
            fixed (DescriptorImageInfo* imageInfoPointer = imageInfos)
            {
                foreach (var prepared in stages)
                {
                    var stage = (PreparedStageBindings)prepared;
                    var program = stage.Program;
                    var descriptors = stage.Descriptors;
                    var shaderStage = StageOf(program);
                    var stageFlag = DescriptorWriter.ShaderStageFlag(shaderStage);
                    if (descriptors.GlobalDataShare.Buffer.Handle != 0)
                    {
                        // The host and every earlier queue write to the data share complete before the shader reads it.
                        EndRendering(ScopeBreak.GlobalDataShare);
                        command = BeginBatchedGuestCommands();
                        var barrier = GlobalDataShareBarrier.Make(descriptors.GlobalDataShare.Buffer);
                        VulkanSynchronization.PipelineBarrier(_vk,command, GlobalDataShareBarrier.SourceStages, DescriptorWriter.PipelineStageFlag(stageFlag), 0, 0, null, 1, &barrier, 0, null, BarrierSource.GlobalDataShare);
                    }

                    if (!stage.TexturesKept || !KeepsTextureSet(stage))
                    {
                        RecordStageTextureTransitions(stage.Textures);
                    }

                    if (TextureSetSlot(program) is var textureSlot and >= 0)
                    {
                        _committedTextureSets[textureSlot] = stage.Memoized && stage.Memo is { Valid: true } committedMemo ? committedMemo : null;
                    }
                    foreach (var texture in stage.Textures)
                    {
                        if (texture.IsHostMovie && texture.NeedsUpload)
                        {
                            EndRendering();
                            break;
                        }
                    }

                    if (!_worker)
                    {
                        _presenter.RecordHostMovieUploads(stage.Textures, uploadStage);
                    }

                    foreach (var texture in stage.Textures)
                    {
                        textures[textureIndex++] = texture;
                    }

                    var occurrences = occurrenceScratch[..descriptors.Images.Length];
                    occurrences.Clear();
                    var bindings = stage.Layout.Descriptors;
                    for (var bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
                    {
                        var binding = bindings[bindingIndex];
                        var resources = binding.Resources;
                        var write = new WriteDescriptorSet
                        {
                            SType = StructureType.WriteDescriptorSet,
                            DstBinding = BindingLayout.NativeBindingIndex(shaderStage, binding.Kind),
                            DescriptorType = DescriptorWriter.DescriptorType(binding.Kind),
                            DescriptorCount = DescriptorWriter.DescriptorCount(binding),
                        };
                        var bufferStart = bufferIndex;
                        var imageStart = imageIndex;
                        if (ImageDescriptorBinding.ResourceClass(binding.Kind) != ShaderCompiler.Resources.ImageResourceClass.None)
                        {
                            for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
                            {
                                var resource = resources[resourceIndex];
                                imageInfos[imageIndex++] = ImageInfo(descriptors.Images[(int)resource], occurrences[(int)resource]++, program, (int)resource);
                            }
                        }
                        else
                        {
                            switch (binding.Kind)
                            {
                                case DescriptorBindingKind.Buffers:
                                    for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
                                    {
                                        var resource = resources[resourceIndex];
                                        var view = descriptors.Buffers[(int)resource];
                                        if (view.Buffer.Handle == 0)
                                        {
                                            throw SubmissionScheduler.Fatal($"A storage buffer binding has no buffer: buffer={resource} hash=0x{program.Hash:X16}.");
                                        }

                                        bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = view.Buffer, Offset = view.Offset, Range = view.Range };
                                    }

                                    break;
                                case DescriptorBindingKind.MipStatistics:
                                    bufferInfos[bufferIndex++] = _presenter.MipStatisticsDescriptor();
                                    break;
                                case DescriptorBindingKind.CallMarks:
                                    bufferInfos[bufferIndex++] = _worker ? throw Serial("call marks", resumable: true) : _presenter.CallMarksDescriptor();
                                    break;
                                case DescriptorBindingKind.DeviceAddressFallback:
                                {
                                    var fallback = _bufferCache.DeviceAddressFallbackBuffer;
                                    bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = fallback.Handle, Offset = 0, Range = fallback.Size };
                                    break;
                                }
                                case DescriptorBindingKind.DeviceAddressPageTable:
                                case DescriptorBindingKind.FaultBuffer:
                                {
                                    var shared = binding.Kind == DescriptorBindingKind.DeviceAddressPageTable ? _bufferCache.BdaPageTableBuffer : _bufferCache.FaultBuffer;
                                    bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = shared.Handle, Offset = 0, Range = shared.Size };
                                    break;
                                }

                                case DescriptorBindingKind.FlattenedResourceTable:
                                case DescriptorBindingKind.ShaderData:
                                case DescriptorBindingKind.GlobalDataShare:
                                case DescriptorBindingKind.CallRecords:
                                {
                                    var view = binding.Kind switch
                                    {
                                        DescriptorBindingKind.FlattenedResourceTable => descriptors.FlattenedTable,
                                        DescriptorBindingKind.ShaderData => descriptors.ShaderData,
                                        DescriptorBindingKind.CallRecords => descriptors.CallRecords,
                                        _ => descriptors.GlobalDataShare,
                                    };
                                    if (view.Buffer.Handle == 0)
                                    {
                                        throw SubmissionScheduler.Fatal($"A shared buffer binding has no buffer: kind={binding.Kind} hash=0x{program.Hash:X16}.");
                                    }

                                    bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = view.Buffer, Offset = view.Offset, Range = view.Range };
                                    break;
                                }

                                case DescriptorBindingKind.Samplers:
                                    for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
                                    {
                                        var resource = resources[resourceIndex];
                                        var sampler = descriptors.Samplers[(int)resource];
                                        if (sampler.Handle == 0)
                                        {
                                            throw SubmissionScheduler.Fatal($"A sampler binding has no sampler: sampler={resource} hash=0x{program.Hash:X16}.");
                                        }

                                        imageInfos[imageIndex++] = new DescriptorImageInfo { Sampler = sampler, ImageLayout = ImageLayout.Undefined };
                                    }

                                    break;
                                default:
                                    throw SubmissionScheduler.Fatal($"The descriptor binding kind is invalid: kind={binding.Kind}.");
                            }
                        }

                        if (bufferIndex != bufferStart)
                        {
                            write.PBufferInfo = bufferInfoPointer + bufferStart;
                        }

                        if (imageIndex != imageStart)
                        {
                            write.PImageInfo = imageInfoPointer + imageStart;
                        }

                        writes[writeIndex++] = write;
                    }

                    for (var index = 0; index < descriptors.Images.Length; index++)
                    {
                        var resources = stage.Resources.Info;
                        var canonical = resources.GetCanonicalImageBinding((uint)index);
                        if (canonical >= descriptors.Images.Length || resources.GetCanonicalImageBinding(canonical) != canonical)
                        {
                            throw SubmissionScheduler.Fatal($"An image alias has no canonical binding: image={index} canonical={canonical} hash=0x{program.Hash:X16}.");
                        }

                        var expected = canonical == index
                            ? Math.Max(1u, (uint)descriptors.Images[index].MipViews.Length)
                            : 0u;
                        if (occurrences[index] != expected)
                        {
                            throw SubmissionScheduler.Fatal($"An image is bound a different number of times than its views: image={index} occurrences={occurrences[index]} views={expected} hash=0x{program.Hash:X16}.");
                        }
                    }

                    if (stage.ShaderData.Length != stage.Layout.ShaderDataDwordCount)
                    {
                        throw SubmissionScheduler.Fatal($"The shader data does not match the layout: dwords={stage.ShaderData.Length} layout={stage.Layout.ShaderDataDwordCount} hash=0x{program.Hash:X16}.");
                    }

                    if (stage.Layout.UsesPushData)
                    {
                        for (var index = 0; index < stage.ShaderData.Length; index++)
                        {
                            pushData[stage.Layout.PushDataStartDword + index] = stage.ShaderData[index];
                        }

                        hasPushData = true;
                    }
                }

                if (hasPushData)
                {
                    var pushStages = bindPoint == PipelineBindPoint.Graphics
                        ? ((entry.Description?.VertexStage.Stage == ShaderStageKind.Mesh
                            ? ShaderStageFlags.MeshBitExt : ShaderStageFlags.VertexBit) |
                           ShaderStageFlags.FragmentBit |
                           (entry.Description?.Tessellation is null
                               ? 0 : ShaderStageFlags.TessellationControlBit | ShaderStageFlags.TessellationEvaluationBit))
                        : ShaderStageFlags.ComputeBit;
                    PushConstantsShadowed(command, entry.Layout, pushStages, pushData);
                }

                if (writeIndex != 0 && DescriptorsAlreadyBound(command, bindPoint, entry.Layout, writes[..writeIndex]))
                {
                    Interlocked.Increment(ref _descriptorsShadowed);
                }
                else if (writeIndex != 0)
                {
                    fixed (WriteDescriptorSet* writePointer = writes)
                    {
                        if (entry.UsesPushDescriptors)
                        {
                            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                            {
                                _presenter._pushDescriptorApi.CmdPushDescriptorSet(command, bindPoint, entry.Layout, 0, (uint)writeIndex, writePointer);
                            }
                            ShaderCacheCounters.CountPushSet();
                        }
                        else
                        {
                            if (_worker)
                            {
                                throw Serial("descriptor heap", resumable: true);
                            }

                            var set = _descriptorHeap.Commit(entry.SetLayout, in entry.Demand);
                            for (var index = 0; index < writeIndex; index++)
                            {
                                writes[index].DstSet = set;
                            }

                            _vk.UpdateDescriptorSets(_device, (uint)writeIndex, writePointer, 0, null);
                            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                            {
                                _vk.CmdBindDescriptorSets(command, bindPoint, entry.Layout, 0, 1, &set, 0, null);
                            }
                            ShaderCacheCounters.CountHeapSet();
                        }
                    }
                }

                if (entry.UsesBindlessHeap)
                {
                    if (_worker)
                    {
                        throw Serial("bindless heap", resumable: true);
                    }

                    _presenter.BindBindlessHeap(command, bindPoint, entry.Layout);
                }
            }

            var spare = _worker ? null : _presenter._spareSubmissionResources ??= new Stack<SubmissionUploadResources>();
            var uploads = spare is { Count: not 0 } ? spare.Pop() : new SubmissionUploadResources();
            uploads.DebugName = bindPoint == PipelineBindPoint.Compute ? "SharpEmu dispatch" : "SharpEmu draw";
            uploads.Textures = textures;
            uploads.TextureCount = textureCount;
            uploads.FeedbackSnapshots = preparation.FeedbackSnapshots?.ToArray() ?? [];
            uploads.OverflowBuffers = preparation.OverflowBuffers.Count == 0 ? null : preparation.OverflowBuffers.ToArray();
            if (_worker)
            {
                Requests.Resources.Add(uploads);
            }
            else
            {
                _presenter._batchResources.Add(uploads);
            }

            preparation.OverflowBuffers.Clear();
            preparation.Committed = true;
            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write($"Bindings committed bindPoint={bindPoint} pipeline={pipeline.Pipeline} writes={writeIndex} push={entry.UsesPushDescriptors} pushData={hasPushData}");
            }
        }
    }
}
