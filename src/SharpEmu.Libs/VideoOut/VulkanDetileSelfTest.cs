// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Opt-in GPU equivalence check for <see cref="VulkanDetilePass"/>. When
/// SHARPEMU_DETILE_SELFTEST=1 it builds a known tiled surface, deswizzles it on
/// the GPU into a real image, reads the image back, and compares against the CPU
/// <see cref="GnmTiling.TryDetile"/> — the same equivalence the unit test proves
/// for the params, now end-to-end through the actual Vulkan pass. It logs
/// [DETILE-SELFTEST] PASS/FAIL and never throws into startup (any failure is
/// caught and logged), so it is safe to leave wired.
/// </summary>
internal static unsafe class VulkanDetileSelfTest
{
    private const uint SwizzleMode = 27; // 64 KiB RB+ R_X, exact-XOR
    private const int BytesPerElement = 4;
    private const uint Width = 256;
    private const uint Height = 256;

    public static void RunIfRequested(
        Vk vk,
        Device device,
        Queue queue,
        PhysicalDevice physicalDevice,
        uint queueFamilyIndex)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_DETILE_SELFTEST") != "1")
        {
            return;
        }

        try
        {
            Run(vk, device, queue, physicalDevice, queueFamilyIndex);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[DETILE-SELFTEST] FAIL (exception): {exception.Message}");
        }
    }

    private static void Run(
        Vk vk,
        Device device,
        Queue queue,
        PhysicalDevice physicalDevice,
        uint queueFamilyIndex)
    {
        var parameters = GnmTiling.GetDetileParams(SwizzleMode, BytesPerElement, (int)Width, (int)Height);
        if (!parameters.IsSupported || !VulkanDetilePass.Supports(parameters))
        {
            Console.Error.WriteLine("[DETILE-SELFTEST] FAIL: params not supported by the GPU pass.");
            return;
        }

        // Whole-block tiled source with a deterministic pattern; the same layout
        // the unit test uses. TryDetile gives the reference linear result.
        var blocksHigh = ((int)Height + parameters.BlockHeight - 1) / parameters.BlockHeight;
        var tiled = new byte[(long)parameters.BlocksPerRow * blocksHigh * parameters.BlockBytes];
        for (var index = 0; index < tiled.Length; index++)
        {
            tiled[index] = (byte)((index * 31 + 7) & 0xFF);
        }

        var expected = new byte[Width * Height * BytesPerElement];
        if (!GnmTiling.TryDetile(tiled, expected, SwizzleMode, (int)Width, (int)Height, BytesPerElement))
        {
            Console.Error.WriteLine("[DETILE-SELFTEST] FAIL: CPU TryDetile declined.");
            return;
        }

        using var pass = new VulkanDetilePass(vk, device, queue, physicalDevice, queueFamilyIndex);
        var commandPool = CreateCommandPool(vk, device, queueFamilyIndex);
        try
        {
            // Phase 1: the one-shot DetileIntoImage (submit + wait in place).
            VerifyPhase(
                vk, device, physicalDevice, queue, commandPool, expected, "DetileIntoImage",
                image => pass.DetileIntoImage(image, ImageLayout.Undefined, Width, Height, tiled, parameters));

            // Phase 2: RecordDetile — the exact code path the render loop uses
            // (record into a command buffer, submit, retire the transients).
            VerifyPhase(
                vk, device, physicalDevice, queue, commandPool, expected, "RecordDetile",
                image => RecordDetileAndSubmit(vk, device, queue, commandPool, pass, image, tiled, parameters));
        }
        finally
        {
            if (commandPool.Handle != 0)
            {
                vk.DestroyCommandPool(device, commandPool, null);
            }
        }
    }

    private static void VerifyPhase(
        Vk vk,
        Device device,
        PhysicalDevice physicalDevice,
        Queue queue,
        CommandPool commandPool,
        byte[] expected,
        string label,
        Func<Image, bool> detile)
    {
        var image = CreateImage(vk, device, physicalDevice, out var imageMemory);
        var readback = CreateHostBuffer(
            vk, device, physicalDevice, (ulong)expected.Length, BufferUsageFlags.TransferDstBit, out var readbackMemory);
        try
        {
            if (!detile(image))
            {
                Console.Error.WriteLine($"[DETILE-SELFTEST] {label} FAIL: declined.");
                return;
            }

            CopyImageToBuffer(vk, device, queue, commandPool, image, readback);

            void* mapped;
            Check(
                vk.MapMemory(device, readbackMemory, 0, (ulong)expected.Length, 0, &mapped),
                $"vkMapMemory(selftest {label})");
            var actual = new Span<byte>(mapped, expected.Length);
            var firstMismatch = -1;
            for (var index = 0; index < expected.Length; index++)
            {
                if (actual[index] != expected[index])
                {
                    firstMismatch = index;
                    break;
                }
            }

            vk.UnmapMemory(device, readbackMemory);

            Console.Error.WriteLine(firstMismatch < 0
                ? $"[DETILE-SELFTEST] {label} PASS: {Width}x{Height} mode {SwizzleMode} matches CPU detile ({expected.Length} bytes)."
                : $"[DETILE-SELFTEST] {label} FAIL: first mismatch at byte {firstMismatch} (texel {firstMismatch / BytesPerElement}).");
        }
        finally
        {
            if (readback.Handle != 0)
            {
                vk.DestroyBuffer(device, readback, null);
            }

            if (readbackMemory.Handle != 0)
            {
                vk.FreeMemory(device, readbackMemory, null);
            }

            if (image.Handle != 0)
            {
                vk.DestroyImage(device, image, null);
            }

            if (imageMemory.Handle != 0)
            {
                vk.FreeMemory(device, imageMemory, null);
            }
        }
    }

    // Records the detile into a fresh command buffer, submits, waits, and retires
    // the transients exactly as the presenter's batch does — verifying the render
    // path's code (RecordDetile) without needing a game to trigger it.
    private static bool RecordDetileAndSubmit(
        Vk vk,
        Device device,
        Queue queue,
        CommandPool commandPool,
        VulkanDetilePass pass,
        Image image,
        ReadOnlySpan<byte> tiled,
        in DetileParams parameters)
    {
        var commandBuffer = AllocateCommandBuffer(vk, device, commandPool);
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Check(vk.BeginCommandBuffer(commandBuffer, &beginInfo), "vkBeginCommandBuffer(selftest record)");

        if (!pass.RecordDetile(
                commandBuffer, image, ImageLayout.Undefined, Width, Height, tiled, parameters, out var transients))
        {
            _ = vk.EndCommandBuffer(commandBuffer);
            vk.FreeCommandBuffers(device, commandPool, 1, &commandBuffer);
            return false;
        }

        Check(vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer(selftest record)");

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(vk.CreateFence(device, &fenceInfo, null, out var fence), "vkCreateFence(selftest record)");
        try
        {
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            Check(vk.QueueSubmit(queue, 1, &submitInfo, fence), "vkQueueSubmit(selftest record)");
            Check(vk.WaitForFences(device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences(selftest record)");
        }
        finally
        {
            vk.DestroyFence(device, fence, null);
            vk.FreeCommandBuffers(device, commandPool, 1, &commandBuffer);
            foreach (var (buffer, memory) in transients.Buffers)
            {
                if (buffer.Handle != 0)
                {
                    vk.DestroyBuffer(device, buffer, null);
                }

                if (memory.Handle != 0)
                {
                    vk.FreeMemory(device, memory, null);
                }
            }

            if (transients.DescriptorPool.Handle != 0)
            {
                vk.DestroyDescriptorPool(device, transients.DescriptorPool, null);
            }
        }

        return true;
    }

    private static Image CreateImage(
        Vk vk,
        Device device,
        PhysicalDevice physicalDevice,
        out DeviceMemory memory)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D(Width, Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Check(vk.CreateImage(device, &imageInfo, null, out var image), "vkCreateImage(selftest)");

        vk.GetImageMemoryRequirements(device, image, out var requirements);
        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(
                vk,
                physicalDevice,
                requirements.MemoryTypeBits,
                MemoryPropertyFlags.DeviceLocalBit),
        };
        Check(vk.AllocateMemory(device, &allocateInfo, null, out memory), "vkAllocateMemory(selftest image)");
        Check(vk.BindImageMemory(device, image, memory, 0), "vkBindImageMemory(selftest)");
        return image;
    }

    private static void CopyImageToBuffer(
        Vk vk,
        Device device,
        Queue queue,
        CommandPool commandPool,
        Image image,
        VkBuffer destination)
    {
        var commandBuffer = AllocateCommandBuffer(vk, device, commandPool);
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Check(vk.BeginCommandBuffer(commandBuffer, &beginInfo), "vkBeginCommandBuffer(selftest readback)");

        // DetileIntoImage left the image ShaderReadOnly; move it to TransferSrc.
        var toTransferSrc = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderReadBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            OldLayout = ImageLayout.ShaderReadOnlyOptimal,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        vk.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.FragmentShaderBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toTransferSrc);

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = default,
            ImageExtent = new Extent3D(Width, Height, 1),
        };
        vk.CmdCopyImageToBuffer(
            commandBuffer,
            image,
            ImageLayout.TransferSrcOptimal,
            destination,
            1,
            &region);

        Check(vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer(selftest readback)");

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(vk.CreateFence(device, &fenceInfo, null, out var fence), "vkCreateFence(selftest)");
        try
        {
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            Check(vk.QueueSubmit(queue, 1, &submitInfo, fence), "vkQueueSubmit(selftest readback)");
            Check(
                vk.WaitForFences(device, 1, &fence, true, ulong.MaxValue),
                "vkWaitForFences(selftest readback)");
        }
        finally
        {
            vk.DestroyFence(device, fence, null);
            vk.FreeCommandBuffers(device, commandPool, 1, &commandBuffer);
        }
    }

    private static CommandPool CreateCommandPool(Vk vk, Device device, uint queueFamilyIndex)
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(vk.CreateCommandPool(device, &poolInfo, null, out var pool), "vkCreateCommandPool(selftest)");
        return pool;
    }

    private static CommandBuffer AllocateCommandBuffer(Vk vk, Device device, CommandPool commandPool)
    {
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Check(
            vk.AllocateCommandBuffers(device, &allocateInfo, out var commandBuffer),
            "vkAllocateCommandBuffers(selftest)");
        return commandBuffer;
    }

    private static VkBuffer CreateHostBuffer(
        Vk vk,
        Device device,
        PhysicalDevice physicalDevice,
        ulong size,
        BufferUsageFlags usage,
        out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Check(vk.CreateBuffer(device, &bufferInfo, null, out var buffer), "vkCreateBuffer(selftest)");

        vk.GetBufferMemoryRequirements(device, buffer, out var requirements);
        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(
                vk,
                physicalDevice,
                requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };
        Check(vk.AllocateMemory(device, &allocateInfo, null, out memory), "vkAllocateMemory(selftest buffer)");
        Check(vk.BindBufferMemory(device, buffer, memory, 0), "vkBindBufferMemory(selftest)");
        return buffer;
    }

    private static uint FindMemoryType(
        Vk vk,
        PhysicalDevice physicalDevice,
        uint typeBits,
        MemoryPropertyFlags requiredFlags)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var properties);
        var memoryTypes = &properties.MemoryTypes.Element0;
        for (uint index = 0; index < properties.MemoryTypeCount; index++)
        {
            if ((typeBits & (1u << (int)index)) != 0 &&
                (memoryTypes[index].PropertyFlags & requiredFlags) == requiredFlags)
            {
                return index;
            }
        }

        throw new InvalidOperationException("No compatible Vulkan memory type for the detile self-test.");
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed: {result}");
        }
    }
}
