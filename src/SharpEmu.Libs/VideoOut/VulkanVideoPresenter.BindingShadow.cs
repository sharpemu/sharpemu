// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private static readonly bool NativeStateReuse =
        Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_STATE_REUSE") != "0";

    private sealed partial class RecordingHost
    {
        private const int ShadowVertexBufferCapacity = 32;

        private nint _shadowCommand;
        private ulong _shadowTick;
        private long _shadowEpoch = -1;
        private Pipeline _shadowGraphicsPipeline;
        private Pipeline _shadowComputePipeline;
        private PipelineLayout _shadowPushLayout;
        private ShaderStageFlags _shadowPushStages;
        private uint[]? _shadowPushData;
        private DescriptorShadow? _graphicsDescriptorShadow;
        private DescriptorShadow? _computeDescriptorShadow;
        private ulong[]? _shadowVertexBindings;
        private int _shadowVertexCount = -1;
        private VkBuffer _shadowIndexBuffer;
        private ulong _shadowIndexOffset;
        private IndexType _shadowIndexType;
        private bool _shadowIndexValid;
        private ulong[]? _descriptorKeyScratch;

        private sealed class DescriptorShadow
        {
            public bool Valid;
            public PipelineLayout Layout;
            public ulong[] Key = new ulong[256];
            public int Length;
        }

        private bool SyncNativeShadow(CommandBuffer command)
        {
            if (!NativeStateReuse)
            {
                return false;
            }

            var tick = (ulong)CurrentRecording().Generation;
            var epoch = NativeBindingEpoch.Value;
            if (command.Handle == _shadowCommand && tick == _shadowTick && epoch == _shadowEpoch)
            {
                return true;
            }

            _shadowCommand = command.Handle;
            _shadowTick = tick;
            _shadowEpoch = epoch;
            _shadowGraphicsPipeline = default;
            _shadowComputePipeline = default;
            _shadowPushLayout = default;
            if (_graphicsDescriptorShadow is not null)
            {
                _graphicsDescriptorShadow.Valid = false;
            }

            if (_computeDescriptorShadow is not null)
            {
                _computeDescriptorShadow.Valid = false;
            }

            _shadowVertexCount = -1;
            _shadowIndexValid = false;
            return true;
        }

        private void BindPipelineShadowed(CommandBuffer command, PipelineBindPoint bindPoint, Pipeline pipeline)
        {
            if (SyncNativeShadow(command))
            {
                ref var bound = ref bindPoint == PipelineBindPoint.Compute ? ref _shadowComputePipeline : ref _shadowGraphicsPipeline;
                var reused = bound.Handle == pipeline.Handle;
                CountNativeState(NativeStateKind.Pipeline, reused);
                if (reused)
                {
                    return;
                }

                bound = pipeline;
            }

            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdBindPipeline(command, bindPoint, pipeline);
            }
        }

        private void PushConstantsShadowed(CommandBuffer command, PipelineLayout layout, ShaderStageFlags stages, uint* data)
        {
            if (SyncNativeShadow(command))
            {
                var values = new ReadOnlySpan<uint>(data, (int)PushData.DwordCount);
                var shadowData = _shadowPushData ??= new uint[PushData.DwordCount];
                var reused = _shadowPushLayout.Handle == layout.Handle && _shadowPushStages == stages && values.SequenceEqual(shadowData);
                CountNativeState(NativeStateKind.PushConstants, reused);
                if (reused)
                {
                    return;
                }

                _shadowPushLayout = layout;
                _shadowPushStages = stages;
                values.CopyTo(shadowData);
            }

            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdPushConstants(command, layout, stages, 0, PushData.ByteSize, data);
            }
        }

        private bool DescriptorsAlreadyBound(CommandBuffer command, PipelineBindPoint bindPoint, PipelineLayout layout, ReadOnlySpan<WriteDescriptorSet> writes)
        {
            if (!SyncNativeShadow(command))
            {
                return false;
            }

            var length = DescriptorKey(writes);
            var shadow = bindPoint == PipelineBindPoint.Compute
                ? _computeDescriptorShadow ??= new DescriptorShadow()
                : _graphicsDescriptorShadow ??= new DescriptorShadow();
            var key = _descriptorKeyScratch.AsSpan(0, length);
            var reused = shadow.Valid && shadow.Layout.Handle == layout.Handle && key.SequenceEqual(shadow.Key.AsSpan(0, shadow.Length));
            CountNativeState(NativeStateKind.Descriptors, reused);
            if (reused)
            {
                return true;
            }

            if (shadow.Key.Length < length)
            {
                shadow.Key = new ulong[Math.Max(length, shadow.Key.Length * 2)];
            }

            key.CopyTo(shadow.Key);
            shadow.Length = length;
            shadow.Layout = layout;
            shadow.Valid = true;
            return false;
        }

        private int DescriptorKey(ReadOnlySpan<WriteDescriptorSet> writes)
        {
            var length = 0;
            _descriptorKeyScratch ??= new ulong[256];
            foreach (ref readonly var write in writes)
            {
                var needed = length + 2 + (int)write.DescriptorCount * 3;
                if (_descriptorKeyScratch.Length < needed)
                {
                    Array.Resize(ref _descriptorKeyScratch, Math.Max(needed, _descriptorKeyScratch.Length * 2));
                }

                var key = _descriptorKeyScratch;
                key[length++] = write.DstBinding | ((ulong)write.DescriptorType << 32);
                key[length++] = write.DescriptorCount | (write.PBufferInfo != null ? 1UL << 32 : 0) | (write.PImageInfo != null ? 1UL << 33 : 0);
                for (var index = 0; index < write.DescriptorCount; index++)
                {
                    if (write.PBufferInfo != null)
                    {
                        var info = write.PBufferInfo[index];
                        key[length++] = info.Buffer.Handle;
                        key[length++] = info.Offset;
                        key[length++] = info.Range;
                    }
                    else if (write.PImageInfo != null)
                    {
                        var info = write.PImageInfo[index];
                        key[length++] = info.Sampler.Handle;
                        key[length++] = info.ImageView.Handle;
                        key[length++] = (ulong)info.ImageLayout;
                    }
                }
            }

            return length;
        }

        private bool VertexBuffersAlreadyBound(CommandBuffer command, ReadOnlySpan<BufferBinding> bindings)
        {
            if (!SyncNativeShadow(command) || bindings.Length > ShadowVertexBufferCapacity)
            {
                _shadowVertexCount = -1;
                return false;
            }

            var shadow = _shadowVertexBindings ??= new ulong[ShadowVertexBufferCapacity * 2];
            var reused = _shadowVertexCount == bindings.Length;
            for (var index = 0; reused && index < bindings.Length; index++)
            {
                reused = shadow[index * 2] == bindings[index].Handle && shadow[index * 2 + 1] == bindings[index].Offset;
            }

            CountNativeState(NativeStateKind.VertexBuffers, reused);
            if (reused)
            {
                return true;
            }

            for (var index = 0; index < bindings.Length; index++)
            {
                shadow[index * 2] = bindings[index].Handle;
                shadow[index * 2 + 1] = bindings[index].Offset;
            }

            _shadowVertexCount = bindings.Length;
            return false;
        }

        private bool IndexBufferAlreadyBound(CommandBuffer command, VkBuffer buffer, ulong offset, IndexType type)
        {
            if (!SyncNativeShadow(command))
            {
                return false;
            }

            var reused = _shadowIndexValid && _shadowIndexBuffer.Handle == buffer.Handle && _shadowIndexOffset == offset && _shadowIndexType == type;
            CountNativeState(NativeStateKind.IndexBuffer, reused);
            if (reused)
            {
                return true;
            }

            _shadowIndexBuffer = buffer;
            _shadowIndexOffset = offset;
            _shadowIndexType = type;
            _shadowIndexValid = true;
            return false;
        }

        private static void CountNativeState(NativeStateKind kind, bool reused)
        {
            if (GpuSyncProfile.Enabled)
            {
                GpuSyncProfile.CountNativeState(kind, reused);
            }
        }
    }
}
