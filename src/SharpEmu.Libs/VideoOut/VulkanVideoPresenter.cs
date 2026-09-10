// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core;
using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Media;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.EXT;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct VulkanRenderTargetFormat(
    Format Format,
    Gen5PixelOutputKind OutputKind,
    Gen5ColorComponentMapping ExportMapping)
{
    public bool IsInteger => OutputKind is Gen5PixelOutputKind.Uint or Gen5PixelOutputKind.Sint;
}

internal static class VulkanGraphicsSubgroupPolicy
{
    internal static bool Resolve(uint nativeSubgroupSize, string? overrideValue) =>
        overrideValue switch
        {
            "0" => false,
            "1" => true,
            _ => nativeSubgroupSize == 32,
        };
}

internal sealed record VulkanTranslatedGuestDraw(
    byte[] VertexSpirv,
    byte[] PixelSpirv,
    IReadOnlyList<GuestDrawTexture> Textures,
    IReadOnlyList<GuestMemoryBuffer> GlobalMemoryBuffers,
    IReadOnlyList<GuestVertexBuffer> VertexBuffers,
    uint AttributeCount,
    uint VertexCount,
    uint InstanceCount,
    uint PrimitiveType,
    GuestIndexBuffer? IndexBuffer,
    GuestRenderState RenderState,
    int BaseVertex = 0);

internal sealed record VulkanOffscreenGuestDraw(
    VulkanTranslatedGuestDraw Draw,
    IReadOnlyList<GuestRenderTarget> Targets,
    GuestDepthTarget? DepthTarget,
    bool PublishTarget,
    ulong ShaderAddress);

internal sealed record VulkanOffscreenColorClear(
    IReadOnlyList<GuestRenderTarget> Targets,
    float Red,
    float Green,
    float Blue,
    float Alpha,
    ulong ShaderAddress);

internal sealed record VulkanGuestImageResolve(
    GuestRenderTarget Source,
    GuestRenderTarget Destination);

internal sealed record VulkanComputeGuestDispatch(
    ulong ShaderAddress,
    byte[] ComputeSpirv,
    IReadOnlyList<GuestDrawTexture> Textures,
    IReadOnlyList<GuestMemoryBuffer> GlobalMemoryBuffers,
    uint GroupCountX,
    uint GroupCountY,
    uint GroupCountZ,
    uint BaseGroupX,
    uint BaseGroupY,
    uint BaseGroupZ,
    uint LocalSizeX,
    uint LocalSizeY,
    uint LocalSizeZ,
    bool IsIndirect,
    bool WritesGlobalMemory,
    uint ThreadCountX = uint.MaxValue,
    uint ThreadCountY = uint.MaxValue,
    uint ThreadCountZ = uint.MaxValue);

internal static class VulkanVertexBindingPlanner
{
    public static int BuildUniqueSourceIndices(
        ReadOnlySpan<ulong> handles,
        ReadOnlySpan<bool> perInstance,
        Span<int> sourceIndices,
        ReadOnlySpan<ulong> offsets = default,
        ReadOnlySpan<uint> strides = default)
    {
        if (handles.Length != perInstance.Length || sourceIndices.Length < handles.Length ||
            (!offsets.IsEmpty && offsets.Length != handles.Length) ||
            (!strides.IsEmpty && strides.Length != handles.Length))
        {
            throw new ArgumentException("Vertex binding spans must have compatible lengths.");
        }

        var bindingCount = 0;
        for (var sourceIndex = 0; sourceIndex < handles.Length; sourceIndex++)
        {
            var found = false;
            for (var bindingIndex = 0; bindingIndex < bindingCount; bindingIndex++)
            {
                var previousSourceIndex = sourceIndices[bindingIndex];
                if (handles[previousSourceIndex] == handles[sourceIndex] &&
                    (offsets.IsEmpty || offsets[previousSourceIndex] == offsets[sourceIndex]) &&
                    (strides.IsEmpty || strides[previousSourceIndex] == strides[sourceIndex]) &&
                    perInstance[previousSourceIndex] == perInstance[sourceIndex])
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                sourceIndices[bindingCount++] = sourceIndex;
            }
        }

        return bindingCount;
    }
}

internal readonly record struct VulkanGuestQueueIdentity(
    string Name,
    ulong SubmissionId)
{
    public static VulkanGuestQueueIdentity Default { get; } = new("host.default", 0);
}

internal static unsafe partial class VulkanVideoPresenter
{
    private static readonly object _gate = new();
    private const string DebugUtilsExtensionName = "VK_EXT_debug_utils";
    private const string SwapchainColorspaceExtensionName = "VK_EXT_swapchain_colorspace";
    private const uint NvidiaVendorId = 0x10DE;
    private const uint AmdVendorId = 0x1002;
    private const int LastResortPenalty = 1000;
    private const string PortabilityEnumerationExtensionName = "VK_KHR_portability_enumeration";
    private const string PortabilitySubsetExtensionName = "VK_KHR_portability_subset";

    private static int _nativeSubgroupSize;

    internal static bool GraphicsSubgroupOperationsEnabled =>
        VulkanGraphicsSubgroupPolicy.Resolve(
            unchecked((uint)Volatile.Read(ref _nativeSubgroupSize)),
            Environment.GetEnvironmentVariable("SHARPEMU_GRAPHICS_SUBGROUPS"));

    private static void SetNativeSubgroupSize(uint subgroupSize) =>
        Volatile.Write(ref _nativeSubgroupSize, checked((int)subgroupSize));

    // Standalone launches use a desktop-sized SDL surface unless configured.
    internal const uint Gen5DepthTileMode = 24;
    internal const uint Gen5TextureType2D = 9;
    internal const uint Gen5TextureType3D = 10;

    internal static bool IsGuestTexture3D(uint type) =>
        type == Gen5TextureType3D;

    internal static uint GetGuestTextureDepth(uint type, uint depth) =>
        IsGuestTexture3D(type) ? Math.Max(depth, 1u) : 1u;

    internal static ImageType GetGuestTextureImageType(uint type) =>
        IsGuestTexture3D(type) ? ImageType.Type3D : ImageType.Type2D;

    internal static ImageViewType GetGuestTextureViewType(
        uint type,
        bool arrayedView = false) =>
        IsGuestTexture3D(type)
            ? ImageViewType.Type3D
            : arrayedView
                ? ImageViewType.Type2DArray
                : ImageViewType.Type2D;

    // Vulkan's portable upper bound for minStorageBufferOffsetAlignment is
    // 256 bytes. Using that fixed power of two (instead of racing the render
    // thread's physical-device query) gives shader translation and descriptor
    // creation one stable aliasing contract on every conformant device.
    internal const ulong GuestStorageBufferOffsetAlignment = 256;
    private const ulong MaximumCachedHostBufferBytes = 128UL * 1024 * 1024;
    private static readonly int _maxGuestWorkPerRender =
        int.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_MAX_GUEST_WORK_PER_RENDER"),
            out var guestWorkPerRender) && guestWorkPerRender > 0
            ? guestWorkPerRender
            : OperatingSystem.IsMacOS() ? 256 : 1024;
    // On macOS the whole window loop — including Render() and its guest-work
    // drain — runs on the process main thread, so draining a large backlog of
    // slow guest work (heavy compute) blocks the Cocoa event pump and marks the
    // window "Not Responding" while starving the swapchain present. Cap the
    // wall-clock time spent draining per Render() call; leftover work stays
    // queued for the next frame. SHARPEMU_RENDER_WORK_BUDGET_MS overrides
    // (0 disables the cap); default 12ms keeps the macOS window interactive at
    // ~60Hz. Windows and Linux use a dedicated render thread, so they drain
    // without a time budget by default.
    private static readonly long _renderWorkBudgetTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_RENDER_WORK_BUDGET_MS"),
             out var renderBudgetMs) && renderBudgetMs >= 0
            ? renderBudgetMs
            : OperatingSystem.IsMacOS() ? 12L : 0L) *
        System.Diagnostics.Stopwatch.Frequency / 1000L;
    // Diagnostic: skip every compute dispatch (mistranslated compute shaders
    // run long / GPU-hang and starve the present). Isolates whether the
    // geometry+composite path renders on its own.
    private static readonly bool _skipAllCompute =
        Environment.GetEnvironmentVariable("SHARPEMU_SKIP_ALL_COMPUTE") == "1";
    // Diagnostic: skip compute dispatches whose GroupCountZ is at least this,
    // to isolate a specific tall dispatch (e.g. Demon's Souls' 27x15x72 froxel
    // shader that hangs the Metal queue) without needing its ASLR-varying
    // address. 0 disables.
    private static readonly uint _skipTallComputeZ =
        uint.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SKIP_TALL_COMPUTE_Z"), out var z)
            ? z
            : 0;
    private const uint GuestPrimitiveRectList = AgcPrimitiveHelpers.PrimitiveRectListLegacy;
    private const uint GuestPrimitiveRectListNgg = AgcPrimitiveHelpers.PrimitiveRectList;

    internal static int ScorePhysicalDevice(
        PhysicalDeviceProperties properties,
        string name,
        string? deviceOverride)
    {
        if (!string.IsNullOrWhiteSpace(deviceOverride))
        {
            return name.Contains(deviceOverride, StringComparison.OrdinalIgnoreCase) ? 1000 : -1000;
        }

        var score = properties.DeviceType switch
        {
            PhysicalDeviceType.DiscreteGpu => 300,
            PhysicalDeviceType.VirtualGpu => 100,
            PhysicalDeviceType.IntegratedGpu => 50,
            PhysicalDeviceType.Cpu => 20,
            _ => 10,
        };

        if (properties.VendorID == NvidiaVendorId)
        {
            score += 500;
        }

        score -= ComputeDevicePenalty(properties, OperatingSystem.IsWindows());

        return score;
    }

    internal static int ComputeDevicePenalty(PhysicalDeviceProperties properties, bool isWindows)
    {
        var penalty = 0;

        if (isWindows &&
            properties.DeviceType == PhysicalDeviceType.IntegratedGpu &&
            properties.VendorID == AmdVendorId)
        {
            penalty += LastResortPenalty;
        }

        return penalty;
    }

    internal static bool RequiresRealFormatConversion(Format from, Format to)
    {
        static bool Is10Bit(Format f) =>
            f is Format.A2R10G10B10UnormPack32 or Format.A2B10G10R10UnormPack32;
        return (from == Format.R8G8B8A8Unorm && Is10Bit(to)) ||
               (Is10Bit(from) && to == Format.R8G8B8A8Unorm);
    }

    private sealed partial class Presenter : IDisposable
    {

        private long _presentedSequence;
        private long _presentNotTakenLoggedSequence = long.MinValue;
        private bool _vulkanReady;

        internal bool IsVulkanReady => _vulkanReady;
        private bool _firstFramePresented;
        private bool _firstGuestDrawPresented;
        private bool _splashPresented;
        private bool _deviceLost;
        private bool _deviceLostLogged;
        private string _lastSubmitDebugName = string.Empty;
        private int _directPresentationCount;
        private readonly Dictionary<ulong, long> _presentedGuestImageTraceCounts = new();
        private readonly Dictionary<long, GuestImageResource> _guestImageVersions = new();

        private sealed class TranslatedDrawResources
        {
            public string DebugName = "SharpEmu translated";
            public PipelineLayout PipelineLayout;
            public Pipeline Pipeline;
            public bool PipelineCached;
            public bool DescriptorLayoutCached;
            public DescriptorSetLayout DescriptorSetLayout;
            public DescriptorPool DescriptorPool;
            public DescriptorSet DescriptorSet;
            public TextureResource[] Textures = [];
            public SampleCountFlags Samples = SampleCountFlags.Count1Bit;
            public GlobalBufferResource[] GlobalMemoryBuffers = [];
            public VertexBufferResource[] VertexBuffers = [];
            public VkBuffer IndexBuffer;
            public DeviceMemory IndexMemory;
            public ulong IndexBufferOffset;
            public bool OwnsIndexBuffer;
            public bool Index32Bit;
            public uint VertexCount = 3;
            public uint InstanceCount = 1;
            public int BaseVertex;
            public PrimitiveTopology Topology = PrimitiveTopology.TriangleList;
            public GuestBlendState[] Blends = [GuestBlendState.Default];
            public GuestBlendConstant BlendConstant;
            // Vulkan format of this draw's color target. Needed to suppress
            // blending on formats Metal cannot blend (integer / 32-bit float),
            // which otherwise makes vkCreateGraphicsPipelines fail and can
            // trip a Metal validation assertion.
            public Format[] TargetFormats = [Format.Undefined];
            public GuestRect? Scissor;
            public GuestViewport? Viewport;
            public GuestRasterState Raster = GuestRasterState.Default;
            public GuestDepthState Depth = GuestDepthState.Default;
            public bool HasDepthAttachment;
            public Format DepthAttachmentFormat = Format.Undefined;
            // Layout keys are needed twice per draw (pipeline lookup and
            // descriptor-layout lookup); cache the built strings.
            public string? ResourceLayoutKey;
            public string? VertexLayoutKey;
            public RenderPass TransientRenderPass;
            public Framebuffer TransientFramebuffer;
        }

        private const ulong SwapchainAcquireTimeoutNs = 250_000_000;

        public Presenter(uint width, uint height)
        {
            _commandStream = new CommandStreamQueue(this);
            _hostBufferPool = new VulkanHostBufferPool(
                MaximumCachedHostBufferBytes,
                DestroyHostBufferAllocation);
            _window = new SdlHostWindow(
                VideoOutExports.GetWindowTitle(),
                _videoOptions,
                SdlGraphicsApi.Vulkan);
        }

        public void Run()
        {
            _window.Run(
                Initialize,
                Render,
                () =>
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan VideoOut window closing; " +
                        $"requested={Volatile.Read(ref _presenterCloseRequested)} " +
                        $"deviceLost={_deviceLost}");
                    VideoOutExports.NotifyPresentationWindowClosed();
                    DisposeVulkan();
                },
                WaitForRenderWork);
        }

        public void Dispose()
        {
            DisposeVulkan();
            _window.Dispose();
        }

        private static bool AnyTargetAddressMatches(
            IReadOnlyList<ulong>? targets,
            string environmentVariable)
        {
            if (targets is null)
            {
                return false;
            }

            foreach (var target in targets)
            {
                if (AddressListContains(environmentVariable, target))
                {
                    return true;
                }
            }

            return false;
        }

        [ThreadStatic]
        private static string? _pendingShaderModuleDumpPath;

        private void ProcessDeferredTextureDestroys()
        {
            while (_deferredResourceDestroys.TryPeek(out var resourceEntry) &&
                   resourceEntry.RetireTimeline <= _completedTimeline)
            {
                _deferredResourceDestroys.Dequeue();
                DestroyTranslatedDrawResources(resourceEntry.Resources);
            }

            while (_deferredGuestImageVersionDestroys.TryPeek(out var imageEntry) &&
                   imageEntry.RetireTimeline <= _completedTimeline)
            {
                _deferredGuestImageVersionDestroys.Dequeue();
                DestroyGuestImage(imageEntry.Image);
                FlipProgressTracker.RecordFlip(imageEntry.Image.FlipVersion);
                TraceVulkanShader(
                    $"vk.flip_retired version={imageEntry.Image.FlipVersion} " +
                    $"timeline={imageEntry.RetireTimeline} reason=presentation-dropped");
            }
        }

        private static void WriteUInt16(byte[] output, int offset, ushort value)
        {
            output[offset + 0] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] output, int offset, uint value)
        {
            output[offset + 0] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
            output[offset + 2] = (byte)(value >> 16);
            output[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt32(byte[] output, int offset, int value) =>
            WriteUInt32(output, offset, unchecked((uint)value));

        private VkBuffer CreateBuffer(
            ulong size,
            BufferUsageFlags usage,
            MemoryPropertyFlags memoryFlags,
            out DeviceMemory memory,
            MemoryPropertyFlags preferredMemoryFlags = 0)
        {
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };
            Check(_vk.CreateBuffer(_device, &bufferInfo, null, out var buffer), "vkCreateBuffer");

            _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    memoryFlags,
                    preferredMemoryFlags),
            };
            Check(_deviceInfo.AllocateMemory(memoryInfo, out memory), "vkAllocateMemory");
            Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
            return buffer;
        }

        private uint FindMemoryType(
            uint typeBits,
            MemoryPropertyFlags requiredFlags,
            MemoryPropertyFlags preferredFlags = 0)
        {
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var properties);
            var memoryTypes = &properties.MemoryTypes.Element0;

            for (var pass = preferredFlags != 0 ? 0 : 1; pass < 2; pass++)
            {
                var wanted = pass == 0 ? requiredFlags | preferredFlags : requiredFlags;
                for (uint index = 0; index < properties.MemoryTypeCount; index++)
                {
                    if ((typeBits & (1u << (int)index)) != 0 &&
                        (memoryTypes[index].PropertyFlags & wanted) == wanted)
                    {
                        return index;
                    }
                }
            }

            throw new InvalidOperationException("No compatible Vulkan host-visible memory type was found.");
        }

        private static uint ClampMipLevels(
            uint width,
            uint height,
            uint depth,
            uint requestedMipLevels)
        {
            var largestDimension = Math.Max(Math.Max(width, height), depth);
            uint maximumMipLevels = 1;
            while (largestDimension > 1)
            {
                largestDimension >>= 1;
                maximumMipLevels++;
            }

            return Math.Min(Math.Max(requestedMipLevels, 1u), maximumMipLevels);
        }

        private static uint GetMipDimension(uint dimension, uint mipLevel) =>
            mipLevel >= 32
                ? 1
                : Math.Max(dimension >> (int)mipLevel, 1u);

        private void ReleaseUnsubmittedPresentationResources(
            int frameSlot,
            TranslatedDrawResources? translatedResources,
            bool ownsPresentedGuestImageVersion,
            GuestImageResource? presentedGuestImage)
        {
            if (translatedResources is not null)
            {
                DestroyTranslatedDrawResources(translatedResources);
            }

            if (!ownsPresentedGuestImageVersion || presentedGuestImage is null ||
                _frameGuestImageVersions.Length <= frameSlot ||
                !ReferenceEquals(_frameGuestImageVersions[frameSlot], presentedGuestImage))
            {
                return;
            }

            _frameGuestImageVersions[frameSlot] = null;
            DestroyGuestImage(presentedGuestImage);
        }

        // Metal cannot blend into integer render targets or 32-bit-per-channel
        // float targets (unsupported on Apple-family GPUs). Enabling blend on
        // one makes vkCreateGraphicsPipelines fail with ErrorInitializationFailed
        // (and trips a Metal "not blendable" validation assertion), so the draw
        // is silently dropped. Force blend off for those; blending on an integer
        // target is meaningless on real hardware anyway.
        private static bool IsBlendableFormat(Format format) =>
            format switch
            {
                Format.R8Uint or Format.R8Sint or
                Format.R8G8B8A8Uint or Format.R8G8B8A8Sint or
                Format.R16G16Uint or Format.R16G16Sint or
                Format.R16G16B16A16Uint or Format.R16G16B16A16Sint or
                Format.R32Uint or Format.R32Sint or
                Format.R32G32Uint or Format.R32G32Sint or
                Format.R32G32B32A32Uint or Format.R32G32B32A32Sint or
                Format.R32Sfloat or
                Format.R32G32Sfloat or
                Format.R32G32B32A32Sfloat => false,
                _ => true,
            };

        private static ImageSubresourceRange ColorSubresourceRange(
            uint baseMipLevel = 0,
            uint levelCount = 1,
            uint layerCount = 1) =>
            new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                LayerCount = layerCount,
            };

        private static void TraceVulkanShader(string message)
        {
            if (!_traceVulkanShaderEnabled)
            {
                return;
            }

            Console.Error.WriteLine($"[LOADER][TRACE] {message}");
        }
    }
}
