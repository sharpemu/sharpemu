/* Copyright (C) 2026 SharpEmu Emulator Project
 * SPDX-License-Identifier: GPL-2.0-or-later */
#include "sharpemu_gpu_vulkan.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>
#include <SDL3/SDL_vulkan.h>
#include <vulkan/vulkan.h>
#include <algorithm>
#include <cstring>
#include <new>
#include <string>
#include <unordered_map>
#include <vector>

struct guest_image {
    VkImage image{};
    VkDeviceMemory memory{};
    VkImageView view{};
    VkFormat format{};
    VkImageLayout layout{VK_IMAGE_LAYOUT_UNDEFINED};
    uint32_t width{}, height{}, guest_format{}, number_type{};
    uint64_t cpu_fingerprint{};
    size_t cpu_size{};
};

struct host_buffer {
    VkBuffer buffer{};
    VkDeviceMemory memory{};
    VkDeviceSize size{};
    void* mapping{};
    VkBufferUsageFlags usage{};
};

struct se_gpu_backend {
    SDL_Window* window{}; SDL_Gamepad* gamepad{}; VkInstance instance{}; VkSurfaceKHR surface{};
    VkPhysicalDevice physical_device{}; VkDevice device{}; VkQueue queue{};
    uint32_t queue_family{}; std::string error; se_gpu_log_fn log{}; void* log_user{};
    VkSwapchainKHR swapchain{}; VkFormat swapchain_format{}; VkExtent2D extent{};
    std::vector<VkImage> swapchain_images;
    VkCommandPool command_pool{}; VkCommandBuffer command_buffer{};
    VkPipelineCache pipeline_cache{};
    VkSemaphore image_available{}, render_finished{}; VkFence frame_fence{};
    VkBuffer staging{}; VkDeviceMemory staging_memory{}; void* staging_map{};
    VkDeviceSize staging_capacity{}; bool resized{};
    std::unordered_map<uint64_t, guest_image> guest_images;
    std::unordered_map<uint64_t, uint32_t> display_formats;
    std::vector<host_buffer> buffer_pool;
};
namespace {
thread_local std::string global_error;
se_gpu_result ensure_staging(se_gpu_backend* b, VkDeviceSize size);
se_gpu_result create_swapchain(se_gpu_backend* b);
se_gpu_result fail(se_gpu_backend* b, se_gpu_result r, std::string message) {
    (b ? b->error : global_error) = std::move(message);
    if (b && b->log) b->log(3, b->error.c_str(), b->log_user);
    return r;
}
void cleanup(se_gpu_backend* b) {
    if (!b) return;
    if (b->device) {
        vkDeviceWaitIdle(b->device);
        for (auto& [address, image] : b->guest_images) {
            (void)address;
            if (image.view) vkDestroyImageView(b->device, image.view, nullptr);
            if (image.image) vkDestroyImage(b->device, image.image, nullptr);
            if (image.memory) vkFreeMemory(b->device, image.memory, nullptr);
        }
        for (auto& buffer : b->buffer_pool) {
            if (buffer.mapping) vkUnmapMemory(b->device, buffer.memory);
            if (buffer.buffer) vkDestroyBuffer(b->device, buffer.buffer, nullptr);
            if (buffer.memory) vkFreeMemory(b->device, buffer.memory, nullptr);
        }
        if (b->staging_map) vkUnmapMemory(b->device, b->staging_memory);
        if (b->staging) vkDestroyBuffer(b->device, b->staging, nullptr);
        if (b->staging_memory) vkFreeMemory(b->device, b->staging_memory, nullptr);
        if (b->frame_fence) vkDestroyFence(b->device, b->frame_fence, nullptr);
        if (b->render_finished) vkDestroySemaphore(b->device, b->render_finished, nullptr);
        if (b->image_available) vkDestroySemaphore(b->device, b->image_available, nullptr);
        if (b->command_pool) vkDestroyCommandPool(b->device, b->command_pool, nullptr);
        if (b->pipeline_cache) vkDestroyPipelineCache(b->device, b->pipeline_cache, nullptr);
        if (b->swapchain) vkDestroySwapchainKHR(b->device, b->swapchain, nullptr);
        vkDestroyDevice(b->device, nullptr);
    }
    if (b->surface) vkDestroySurfaceKHR(b->instance, b->surface, nullptr);
    if (b->instance) vkDestroyInstance(b->instance, nullptr);
    if (b->gamepad) SDL_CloseGamepad(b->gamepad);
    if (b->window) SDL_DestroyWindow(b->window);
    SDL_Quit(); delete b;
}

uint32_t memory_type(se_gpu_backend* b, uint32_t bits, VkMemoryPropertyFlags required) {
    VkPhysicalDeviceMemoryProperties properties{};
    vkGetPhysicalDeviceMemoryProperties(b->physical_device, &properties);
    for (uint32_t index = 0; index < properties.memoryTypeCount; ++index) {
        if ((bits & (1u << index)) != 0 &&
            (properties.memoryTypes[index].propertyFlags & required) == required) return index;
    }
    return UINT32_MAX;
}

VkFormat guest_format(uint32_t format, uint32_t number_type) {
    if (format == 4) return number_type == 4 ? VK_FORMAT_R32_UINT :
        number_type == 5 ? VK_FORMAT_R32_SINT : VK_FORMAT_R32_SFLOAT;
    if (format == 5) return number_type == 4 ? VK_FORMAT_R16G16_UINT :
        number_type == 5 ? VK_FORMAT_R16G16_SINT : number_type == 7 ? VK_FORMAT_R16G16_SFLOAT :
        VK_FORMAT_R16G16_UNORM;
    if (format == 10) return number_type == 4 ? VK_FORMAT_R8G8B8A8_UINT :
        number_type == 5 ? VK_FORMAT_R8G8B8A8_SINT : VK_FORMAT_R8G8B8A8_UNORM;
    if (format == 12) return number_type == 4 ? VK_FORMAT_R16G16B16A16_UINT :
        number_type == 5 ? VK_FORMAT_R16G16B16A16_SINT : number_type == 7 ? VK_FORMAT_R16G16B16A16_SFLOAT :
        VK_FORMAT_R16G16B16A16_UNORM;
    switch (format) {
    case 1: case 36: return VK_FORMAT_R8_UNORM;
    case 3: return VK_FORMAT_R8G8_UNORM;
    case 7: return VK_FORMAT_B10G11R11_UFLOAT_PACK32;
    case 9: return VK_FORMAT_A2R10G10B10_UNORM_PACK32;
    case 13: case 14: return VK_FORMAT_R32G32B32A32_SFLOAT;
    case 22: case 71: return VK_FORMAT_R16G16B16A16_SFLOAT;
    case 29: return VK_FORMAT_R32_SFLOAT;
    case 49: return VK_FORMAT_R8_UINT;
    case 56: case 62: case 64: return VK_FORMAT_R8G8B8A8_UNORM;
    case 75: return VK_FORMAT_R32G32_SFLOAT;
    case 0x10004: return VK_FORMAT_R32_UINT;
    case 0x20004: return VK_FORMAT_R32_SINT;
    case 0x30004: return VK_FORMAT_R32_SFLOAT;
    case 0x10005: return VK_FORMAT_R16G16_UINT;
    case 0x20005: return VK_FORMAT_R16G16_SINT;
    case 0x30005: return VK_FORMAT_R16G16_SFLOAT;
    case 0x1000a: return VK_FORMAT_R8G8B8A8_UINT;
    case 0x2000a: return VK_FORMAT_R8G8B8A8_SINT;
    case 0x1000c: return VK_FORMAT_R16G16B16A16_UINT;
    case 0x2000c: return VK_FORMAT_R16G16B16A16_SINT;
    default: return VK_FORMAT_UNDEFINED;
    }
}

uint32_t format_bytes(VkFormat format) {
    switch (format) {
    case VK_FORMAT_R8_UNORM: case VK_FORMAT_R8_UINT: return 1;
    case VK_FORMAT_R8G8_UNORM: return 2;
    case VK_FORMAT_R32G32B32A32_SFLOAT: return 16;
    case VK_FORMAT_R16G16B16A16_UNORM: case VK_FORMAT_R16G16B16A16_UINT:
    case VK_FORMAT_R16G16B16A16_SINT: case VK_FORMAT_R16G16B16A16_SFLOAT:
    case VK_FORMAT_R32G32_SFLOAT: return 8;
    default: return 4;
    }
}

[[maybe_unused]] se_gpu_result create_buffer(se_gpu_backend* b, const void* data, size_t data_size,
    VkBufferUsageFlags usage, host_buffer* output) {
    if (!output) return SE_GPU_INVALID_ARGUMENT;
    VkDeviceSize size = std::max<VkDeviceSize>(static_cast<VkDeviceSize>(data_size), 4);
    auto pooled = std::find_if(b->buffer_pool.begin(), b->buffer_pool.end(),
        [size, usage](const host_buffer& candidate) { return candidate.size >= size && candidate.usage == usage; });
    if (pooled != b->buffer_pool.end()) {
        *output = *pooled;
        b->buffer_pool.erase(pooled);
        if (data && data_size) std::memcpy(output->mapping, data, data_size);
        return SE_GPU_OK;
    }
    VkBufferCreateInfo info{}; info.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    info.size = size; info.usage = usage; info.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (vkCreateBuffer(b->device, &info, nullptr, &output->buffer) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "host buffer creation failed");
    VkMemoryRequirements requirements{}; vkGetBufferMemoryRequirements(b->device, output->buffer, &requirements);
    uint32_t type = memory_type(b, requirements.memoryTypeBits,
        VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    if (type == UINT32_MAX) return fail(b, SE_GPU_VULKAN_ERROR, "no host buffer memory type");
    VkMemoryAllocateInfo allocation{}; allocation.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    allocation.allocationSize = requirements.size; allocation.memoryTypeIndex = type;
    if (vkAllocateMemory(b->device, &allocation, nullptr, &output->memory) != VK_SUCCESS ||
        vkBindBufferMemory(b->device, output->buffer, output->memory, 0) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "host buffer memory allocation failed");
    if (vkMapMemory(b->device, output->memory, 0, size, 0, &output->mapping) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "host buffer map failed");
    if (data && data_size) std::memcpy(output->mapping, data, data_size);
    output->size = size;
    output->usage = usage;
    return SE_GPU_OK;
}

[[maybe_unused]] void destroy_buffer(se_gpu_backend* b, host_buffer* buffer) {
    if (buffer->buffer) b->buffer_pool.push_back(*buffer);
    *buffer = {};
}

void destroy_image(se_gpu_backend* b, guest_image* image) {
    if (image->view) vkDestroyImageView(b->device, image->view, nullptr);
    if (image->image) vkDestroyImage(b->device, image->image, nullptr);
    if (image->memory) vkFreeMemory(b->device, image->memory, nullptr);
    *image = {};
}

se_gpu_result begin_immediate(se_gpu_backend* b) {
    if (vkWaitForFences(b->device, 1, &b->frame_fence, VK_TRUE, UINT64_MAX) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "GPU fence wait failed");
    vkResetFences(b->device, 1, &b->frame_fence);
    vkResetCommandBuffer(b->command_buffer, 0);
    VkCommandBufferBeginInfo begin{}; begin.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    return vkBeginCommandBuffer(b->command_buffer, &begin) == VK_SUCCESS ? SE_GPU_OK :
        fail(b, SE_GPU_VULKAN_ERROR, "command buffer begin failed");
}

se_gpu_result end_immediate(se_gpu_backend* b) {
    if (vkEndCommandBuffer(b->command_buffer) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "command buffer end failed");
    VkSubmitInfo submit{}; submit.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    submit.commandBufferCount = 1; submit.pCommandBuffers = &b->command_buffer;
    if (vkQueueSubmit(b->queue, 1, &submit, b->frame_fence) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "GPU queue submit failed");
    return vkWaitForFences(b->device, 1, &b->frame_fence, VK_TRUE, UINT64_MAX) == VK_SUCCESS ? SE_GPU_OK :
        fail(b, SE_GPU_VULKAN_ERROR, "GPU command completion failed");
}

void image_barrier(se_gpu_backend* b, guest_image* image, VkImageLayout next,
    VkAccessFlags source_access, VkAccessFlags destination_access,
    VkPipelineStageFlags source_stage, VkPipelineStageFlags destination_stage) {
    VkImageMemoryBarrier barrier{}; barrier.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    barrier.srcAccessMask = source_access; barrier.dstAccessMask = destination_access;
    barrier.oldLayout = image->layout; barrier.newLayout = next;
    barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED; barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrier.image = image->image; barrier.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    vkCmdPipelineBarrier(b->command_buffer, source_stage, destination_stage, 0,
        0, nullptr, 0, nullptr, 1, &barrier);
    image->layout = next;
}

se_gpu_result create_image(se_gpu_backend* b, uint32_t width, uint32_t height,
    uint32_t format, uint32_t number_type, guest_image* output) {
    VkFormat native_format = guest_format(format, number_type);
    if (!width || !height || native_format == VK_FORMAT_UNDEFINED)
        return fail(b, SE_GPU_INVALID_ARGUMENT, "unsupported guest image format");
    VkImageCreateInfo info{}; info.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    info.imageType = VK_IMAGE_TYPE_2D; info.format = native_format; info.extent = {width, height, 1};
    info.mipLevels = 1; info.arrayLayers = 1; info.samples = VK_SAMPLE_COUNT_1_BIT;
    info.tiling = VK_IMAGE_TILING_OPTIMAL; info.usage = VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
        VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_STORAGE_BIT |
        VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT; info.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (vkCreateImage(b->device, &info, nullptr, &output->image) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "guest image creation failed");
    VkMemoryRequirements requirements{}; vkGetImageMemoryRequirements(b->device, output->image, &requirements);
    uint32_t type = memory_type(b, requirements.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    if (type == UINT32_MAX) return fail(b, SE_GPU_VULKAN_ERROR, "no device-local image memory type");
    VkMemoryAllocateInfo allocation{}; allocation.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    allocation.allocationSize = requirements.size; allocation.memoryTypeIndex = type;
    if (vkAllocateMemory(b->device, &allocation, nullptr, &output->memory) != VK_SUCCESS ||
        vkBindImageMemory(b->device, output->image, output->memory, 0) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "guest image memory allocation failed");
    VkImageViewCreateInfo view{}; view.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    view.image = output->image; view.viewType = VK_IMAGE_VIEW_TYPE_2D; view.format = native_format;
    view.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    if (vkCreateImageView(b->device, &view, nullptr, &output->view) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "guest image view creation failed");
    output->format = native_format; output->width = width; output->height = height;
    output->guest_format = format; output->number_type = number_type; return SE_GPU_OK;
}

se_gpu_result upload_image(se_gpu_backend* b, guest_image* image, const se_gpu_bytes& pixels,
    uint32_t row_length) {
    if (!pixels.data || !pixels.size) return SE_GPU_OK;
    row_length = std::max(row_length, image->width);
    uint32_t source_texel_bytes = image->guest_format == 13 ? 12u : format_bytes(image->format);
    uint32_t upload_texel_bytes = format_bytes(image->format);
    size_t required = static_cast<size_t>(row_length) * image->height * source_texel_bytes;
    if (pixels.size < required) return fail(b, SE_GPU_INVALID_ARGUMENT, "guest image upload is undersized");
    size_t upload_size = static_cast<size_t>(row_length) * image->height * upload_texel_bytes;
    se_gpu_result result = ensure_staging(b, static_cast<VkDeviceSize>(upload_size));
    if (result != SE_GPU_OK) return result;
    if (image->guest_format == 13) {
        const auto* source = static_cast<const uint8_t*>(pixels.data);
        auto* destination = static_cast<uint8_t*>(b->staging_map);
        size_t texels = static_cast<size_t>(row_length) * image->height;
        for (size_t texel = 0; texel < texels; ++texel) {
            std::memcpy(destination + texel * 16, source + texel * 12, 12);
            destination[texel * 16 + 12] = 0;
            destination[texel * 16 + 13] = 0;
            destination[texel * 16 + 14] = 0x80;
            destination[texel * 16 + 15] = 0x3f;
        }
    } else {
        std::memcpy(b->staging_map, pixels.data, upload_size);
    }
    result = begin_immediate(b); if (result != SE_GPU_OK) return result;
    image_barrier(b, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT,
        VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkBufferImageCopy copy{};
    copy.bufferRowLength = row_length > image->width ? row_length : 0;
    copy.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
    copy.imageExtent = {image->width, image->height, 1};
    vkCmdCopyBufferToImage(b->command_buffer, b->staging, image->image,
        VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &copy);
    image_barrier(b, image, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_WRITE_BIT,
        VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT,
        VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT);
    return end_immediate(b);
}

uint64_t fingerprint(const se_gpu_bytes& bytes) {
    const auto* data = static_cast<const uint8_t*>(bytes.data);
    uint64_t hash = 1469598103934665603ull;
    for (size_t index = 0; index < bytes.size; ++index) {
        hash ^= data[index]; hash *= 1099511628211ull;
    }
    return hash;
}

se_gpu_result cached_image(se_gpu_backend* b, uint64_t address, uint32_t width, uint32_t height,
    uint32_t format, uint32_t number_type, uint32_t row_length,
    const se_gpu_bytes* pixels, guest_image** output) {
    if (!address || !output) return SE_GPU_INVALID_ARGUMENT;
    auto [iterator, inserted] = b->guest_images.try_emplace(address);
    guest_image& image = iterator->second;
    VkFormat native_format = guest_format(format, number_type);
    if (!inserted && (image.width != width || image.height != height || image.format != native_format)) {
        vkDeviceWaitIdle(b->device); destroy_image(b, &image); inserted = true;
    }
    se_gpu_result result = SE_GPU_OK;
    if (inserted) result = create_image(b, width, height, format, number_type, &image);
    if (result == SE_GPU_OK && pixels && pixels->data && pixels->size) {
        uint64_t content = fingerprint(*pixels);
        if (image.cpu_size != pixels->size || image.cpu_fingerprint != content) {
            result = upload_image(b, &image, *pixels, row_length);
            if (result == SE_GPU_OK) { image.cpu_size = pixels->size; image.cpu_fingerprint = content; }
        }
    }
    if (result != SE_GPU_OK) { destroy_image(b, &image); b->guest_images.erase(iterator); return result; }
    *output = &image; return SE_GPU_OK;
}

se_gpu_result blit_image(se_gpu_backend* b, guest_image* source, guest_image* destination) {
    se_gpu_result result = begin_immediate(b); if (result != SE_GPU_OK) return result;
    VkImageLayout source_layout = source->layout;
    image_barrier(b, source, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
        VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
        VK_ACCESS_TRANSFER_READ_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    image_barrier(b, destination, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
        VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
        VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkImageBlit region{}; region.srcSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
    region.srcOffsets[1] = {static_cast<int32_t>(source->width), static_cast<int32_t>(source->height), 1};
    region.dstSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
    region.dstOffsets[1] = {static_cast<int32_t>(destination->width), static_cast<int32_t>(destination->height), 1};
    vkCmdBlitImage(b->command_buffer, source->image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
        destination->image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region, VK_FILTER_NEAREST);
    image_barrier(b, source, source_layout == VK_IMAGE_LAYOUT_UNDEFINED ? VK_IMAGE_LAYOUT_GENERAL : source_layout,
        VK_ACCESS_TRANSFER_READ_BIT, VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT,
        VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT);
    image_barrier(b, destination, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_WRITE_BIT,
        VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT,
        VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT);
    return end_immediate(b);
}

se_gpu_result present_image(se_gpu_backend* b, guest_image* source) {
    if (vkWaitForFences(b->device, 1, &b->frame_fence, VK_TRUE, UINT64_MAX) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "presentation fence wait failed");
    if (b->resized) { vkDeviceWaitIdle(b->device); se_gpu_result recreated = create_swapchain(b);
        if (recreated != SE_GPU_OK) return recreated; }
    uint32_t image_index{}; VkResult acquired = vkAcquireNextImageKHR(b->device, b->swapchain,
        UINT64_MAX, b->image_available, VK_NULL_HANDLE, &image_index);
    if (acquired == VK_ERROR_OUT_OF_DATE_KHR) { b->resized = true; return SE_GPU_NOT_READY; }
    if (acquired != VK_SUCCESS && acquired != VK_SUBOPTIMAL_KHR)
        return fail(b, SE_GPU_VULKAN_ERROR, "guest image swapchain acquisition failed");
    vkResetFences(b->device, 1, &b->frame_fence); vkResetCommandBuffer(b->command_buffer, 0);
    VkCommandBufferBeginInfo begin{}; begin.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    if (vkBeginCommandBuffer(b->command_buffer, &begin) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "guest image command begin failed");
    VkImageLayout original = source->layout;
    image_barrier(b, source, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
        VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
        VK_ACCESS_TRANSFER_READ_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkImageMemoryBarrier swapchain{}; swapchain.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    swapchain.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT; swapchain.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    swapchain.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    swapchain.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED; swapchain.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    swapchain.image = b->swapchain_images[image_index];
    swapchain.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    vkCmdPipelineBarrier(b->command_buffer, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
        0, 0, nullptr, 0, nullptr, 1, &swapchain);
    VkImageBlit region{}; region.srcSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
    region.srcOffsets[1] = {static_cast<int32_t>(source->width), static_cast<int32_t>(source->height), 1};
    region.dstSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
    region.dstOffsets[1] = {static_cast<int32_t>(b->extent.width), static_cast<int32_t>(b->extent.height), 1};
    vkCmdBlitImage(b->command_buffer, source->image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
        b->swapchain_images[image_index], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region, VK_FILTER_NEAREST);
    image_barrier(b, source, original == VK_IMAGE_LAYOUT_UNDEFINED ? VK_IMAGE_LAYOUT_GENERAL : original,
        VK_ACCESS_TRANSFER_READ_BIT, VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT,
        VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT);
    swapchain.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT; swapchain.dstAccessMask = 0;
    swapchain.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL; swapchain.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    vkCmdPipelineBarrier(b->command_buffer, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
        0, 0, nullptr, 0, nullptr, 1, &swapchain);
    if (vkEndCommandBuffer(b->command_buffer) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "guest image command end failed");
    VkPipelineStageFlags wait = VK_PIPELINE_STAGE_TRANSFER_BIT; VkSubmitInfo submit{};
    submit.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO; submit.waitSemaphoreCount = 1;
    submit.pWaitSemaphores = &b->image_available; submit.pWaitDstStageMask = &wait;
    submit.commandBufferCount = 1; submit.pCommandBuffers = &b->command_buffer;
    submit.signalSemaphoreCount = 1; submit.pSignalSemaphores = &b->render_finished;
    if (vkQueueSubmit(b->queue, 1, &submit, b->frame_fence) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "guest image queue submit failed");
    VkPresentInfoKHR present{}; present.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    present.waitSemaphoreCount = 1; present.pWaitSemaphores = &b->render_finished;
    present.swapchainCount = 1; present.pSwapchains = &b->swapchain; present.pImageIndices = &image_index;
    VkResult result = vkQueuePresentKHR(b->queue, &present);
    if (result == VK_ERROR_OUT_OF_DATE_KHR || result == VK_SUBOPTIMAL_KHR) { b->resized = true; return SE_GPU_OK; }
    if (result != VK_SUCCESS) return fail(b, SE_GPU_VULKAN_ERROR, "guest image present failed");
    return vkWaitForFences(b->device, 1, &b->frame_fence, VK_TRUE, UINT64_MAX) == VK_SUCCESS ? SE_GPU_OK :
        fail(b, SE_GPU_VULKAN_ERROR, "guest image presentation completion failed");
}

se_gpu_result create_commands(se_gpu_backend* b) {
    VkPipelineCacheCreateInfo cache{}; cache.sType = VK_STRUCTURE_TYPE_PIPELINE_CACHE_CREATE_INFO;
    if (vkCreatePipelineCache(b->device, &cache, nullptr, &b->pipeline_cache) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "vkCreatePipelineCache failed");
    VkCommandPoolCreateInfo pool{};
    pool.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    pool.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pool.queueFamilyIndex = b->queue_family;
    if (vkCreateCommandPool(b->device, &pool, nullptr, &b->command_pool) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "vkCreateCommandPool failed");
    VkCommandBufferAllocateInfo allocate{};
    allocate.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    allocate.commandPool = b->command_pool; allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    allocate.commandBufferCount = 1;
    if (vkAllocateCommandBuffers(b->device, &allocate, &b->command_buffer) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "vkAllocateCommandBuffers failed");
    VkSemaphoreCreateInfo semaphore{}; semaphore.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
    if (vkCreateSemaphore(b->device, &semaphore, nullptr, &b->image_available) != VK_SUCCESS ||
        vkCreateSemaphore(b->device, &semaphore, nullptr, &b->render_finished) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "vkCreateSemaphore failed");
    VkFenceCreateInfo fence{}; fence.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    fence.flags = VK_FENCE_CREATE_SIGNALED_BIT;
    if (vkCreateFence(b->device, &fence, nullptr, &b->frame_fence) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "vkCreateFence failed");
    return SE_GPU_OK;
}

se_gpu_result create_swapchain(se_gpu_backend* b) {
    VkSurfaceCapabilitiesKHR capabilities{};
    if (vkGetPhysicalDeviceSurfaceCapabilitiesKHR(b->physical_device, b->surface, &capabilities) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "surface capability query failed");
    int width{}, height{}; SDL_GetWindowSizeInPixels(b->window, &width, &height);
    if (width <= 0 || height <= 0) return SE_GPU_NOT_READY;
    uint32_t count{}; vkGetPhysicalDeviceSurfaceFormatsKHR(b->physical_device, b->surface, &count, nullptr);
    if (!count) return fail(b, SE_GPU_VULKAN_ERROR, "surface has no formats");
    std::vector<VkSurfaceFormatKHR> formats(count);
    vkGetPhysicalDeviceSurfaceFormatsKHR(b->physical_device, b->surface, &count, formats.data());
    VkSurfaceFormatKHR selected = formats.front();

    // Guest render targets are already encoded for display. Prefer a UNORM
    // swapchain everywhere so presentation does not apply an additional sRGB
    // transfer function. BGRA avoids a channel shuffle on the common path;
    // RGBA UNORM is the next-best native surface format.
    for (const auto& format : formats) {
        if (format.format == VK_FORMAT_B8G8R8A8_UNORM) {
            selected = format; break;
        }
    }
    if (selected.format != VK_FORMAT_B8G8R8A8_UNORM) {
        for (const auto& format : formats) {
            if (format.format == VK_FORMAT_R8G8B8A8_UNORM) {
                selected = format; break;
            }
        }
    }
    VkExtent2D extent = capabilities.currentExtent;
    if (extent.width == UINT32_MAX) {
        extent.width = std::clamp(static_cast<uint32_t>(width), capabilities.minImageExtent.width,
            capabilities.maxImageExtent.width);
        extent.height = std::clamp(static_cast<uint32_t>(height), capabilities.minImageExtent.height,
            capabilities.maxImageExtent.height);
    }
    uint32_t image_count = capabilities.minImageCount + 1;
    if (capabilities.maxImageCount) image_count = std::min(image_count, capabilities.maxImageCount);
    VkCompositeAlphaFlagBitsKHR alpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    if ((capabilities.supportedCompositeAlpha & alpha) == 0) {
        const VkCompositeAlphaFlagBitsKHR choices[]{VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR,
            VK_COMPOSITE_ALPHA_POST_MULTIPLIED_BIT_KHR, VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR};
        for (auto choice : choices) if ((capabilities.supportedCompositeAlpha & choice) != 0) { alpha = choice; break; }
    }
    VkSwapchainCreateInfoKHR info{}; info.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    info.surface = b->surface; info.minImageCount = image_count; info.imageFormat = selected.format;
    info.imageColorSpace = selected.colorSpace; info.imageExtent = extent; info.imageArrayLayers = 1;
    info.imageUsage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    info.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE; info.preTransform = capabilities.currentTransform;
    info.compositeAlpha = alpha; info.presentMode = VK_PRESENT_MODE_FIFO_KHR; info.clipped = VK_TRUE;
    info.oldSwapchain = b->swapchain;
    VkSwapchainKHR replacement{}; VkResult result = vkCreateSwapchainKHR(b->device, &info, nullptr, &replacement);
    if (result != VK_SUCCESS) return fail(b, SE_GPU_VULKAN_ERROR, "vkCreateSwapchainKHR failed");
    if (b->swapchain) vkDestroySwapchainKHR(b->device, b->swapchain, nullptr);
    b->swapchain = replacement; b->swapchain_format = selected.format; b->extent = extent;
    vkGetSwapchainImagesKHR(b->device, b->swapchain, &image_count, nullptr);
    b->swapchain_images.resize(image_count);
    vkGetSwapchainImagesKHR(b->device, b->swapchain, &image_count, b->swapchain_images.data());
    b->resized = false; return SE_GPU_OK;
}

se_gpu_result ensure_staging(se_gpu_backend* b, VkDeviceSize size) {
    if (b->staging_capacity >= size) return SE_GPU_OK;
    vkDeviceWaitIdle(b->device);
    if (b->staging_map) vkUnmapMemory(b->device, b->staging_memory);
    if (b->staging) vkDestroyBuffer(b->device, b->staging, nullptr);
    if (b->staging_memory) vkFreeMemory(b->device, b->staging_memory, nullptr);
    b->staging_map = nullptr; b->staging = {}; b->staging_memory = {}; b->staging_capacity = 0;
    VkBufferCreateInfo info{}; info.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO; info.size = size;
    info.usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT; info.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (vkCreateBuffer(b->device, &info, nullptr, &b->staging) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "staging buffer creation failed");
    VkMemoryRequirements requirements{}; vkGetBufferMemoryRequirements(b->device, b->staging, &requirements);
    uint32_t type = memory_type(b, requirements.memoryTypeBits,
        VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    if (type == UINT32_MAX) return fail(b, SE_GPU_VULKAN_ERROR, "no host-visible memory type");
    VkMemoryAllocateInfo allocation{}; allocation.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    allocation.allocationSize = requirements.size; allocation.memoryTypeIndex = type;
    if (vkAllocateMemory(b->device, &allocation, nullptr, &b->staging_memory) != VK_SUCCESS ||
        vkBindBufferMemory(b->device, b->staging, b->staging_memory, 0) != VK_SUCCESS ||
        vkMapMemory(b->device, b->staging_memory, 0, requirements.size, 0, &b->staging_map) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "staging memory allocation failed");
    b->staging_capacity = requirements.size; return SE_GPU_OK;
}

void refresh_gamepad(se_gpu_backend* b) {
    if (b->gamepad && SDL_GamepadConnected(b->gamepad)) return;
    if (b->gamepad) { SDL_CloseGamepad(b->gamepad); b->gamepad = nullptr; }
    int count{}; SDL_JoystickID* ids = SDL_GetGamepads(&count);
    if (ids && count > 0) b->gamepad = SDL_OpenGamepad(ids[0]);
    SDL_free(ids);
}
void set_key(se_gpu_input* state, uint32_t virtual_key, bool down) {
    if (down && virtual_key < 256) state->virtual_keys[virtual_key / 32] |= 1u << (virtual_key % 32);
}
uint8_t stick_value(int16_t value) {
    return static_cast<uint8_t>((static_cast<int32_t>(value) + 32768) * 255 / 65535);
}
uint8_t trigger_value(int16_t value) {
    return static_cast<uint8_t>(std::clamp(static_cast<int32_t>(value), 0, 32767) * 255 / 32767);
}

VkPrimitiveTopology primitive_topology(uint32_t type) {
    switch (type) {
    case 1: return VK_PRIMITIVE_TOPOLOGY_POINT_LIST;
    case 2: return VK_PRIMITIVE_TOPOLOGY_LINE_LIST;
    case 3: return VK_PRIMITIVE_TOPOLOGY_LINE_STRIP;
    case 5: return VK_PRIMITIVE_TOPOLOGY_TRIANGLE_FAN;
    case 6: case 0x11: return VK_PRIMITIVE_TOPOLOGY_TRIANGLE_STRIP;
    default: return VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    }
}
VkBlendFactor blend_factor(uint32_t value) {
    switch (value) {
    case 0: return VK_BLEND_FACTOR_ZERO; case 1: return VK_BLEND_FACTOR_ONE;
    case 2: return VK_BLEND_FACTOR_SRC_COLOR; case 3: return VK_BLEND_FACTOR_ONE_MINUS_SRC_COLOR;
    case 4: return VK_BLEND_FACTOR_SRC_ALPHA; case 5: return VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    case 6: return VK_BLEND_FACTOR_DST_ALPHA; case 7: return VK_BLEND_FACTOR_ONE_MINUS_DST_ALPHA;
    case 8: return VK_BLEND_FACTOR_DST_COLOR; case 9: return VK_BLEND_FACTOR_ONE_MINUS_DST_COLOR;
    case 10: return VK_BLEND_FACTOR_SRC_ALPHA_SATURATE; case 13: return VK_BLEND_FACTOR_CONSTANT_COLOR;
    case 14: return VK_BLEND_FACTOR_ONE_MINUS_CONSTANT_COLOR; case 15: return VK_BLEND_FACTOR_SRC1_COLOR;
    case 16: return VK_BLEND_FACTOR_ONE_MINUS_SRC1_COLOR; case 17: return VK_BLEND_FACTOR_SRC1_ALPHA;
    case 18: return VK_BLEND_FACTOR_ONE_MINUS_SRC1_ALPHA; case 19: return VK_BLEND_FACTOR_CONSTANT_ALPHA;
    case 20: return VK_BLEND_FACTOR_ONE_MINUS_CONSTANT_ALPHA; default: return VK_BLEND_FACTOR_ONE;
    }
}
VkBlendOp blend_op(uint32_t value) {
    switch (value) { case 1: return VK_BLEND_OP_SUBTRACT; case 2: return VK_BLEND_OP_MIN;
    case 3: return VK_BLEND_OP_MAX; case 4: return VK_BLEND_OP_REVERSE_SUBTRACT; default: return VK_BLEND_OP_ADD; }
}
VkFormat vertex_format(uint32_t data, uint32_t number, uint32_t components) {
    if (data == 1) return number == 1 ? VK_FORMAT_R8_SNORM : number == 4 ? VK_FORMAT_R8_UINT :
        number == 5 ? VK_FORMAT_R8_SINT : number == 9 ? VK_FORMAT_R8_SRGB : VK_FORMAT_R8_UNORM;
    if (data == 2) return number == 1 ? VK_FORMAT_R16_SNORM : number == 4 ? VK_FORMAT_R16_UINT :
        number == 5 ? VK_FORMAT_R16_SINT : number == 7 ? VK_FORMAT_R16_SFLOAT : VK_FORMAT_R16_UNORM;
    if (data == 3) return number == 1 ? VK_FORMAT_R8G8_SNORM : number == 4 ? VK_FORMAT_R8G8_UINT :
        number == 5 ? VK_FORMAT_R8G8_SINT : number == 9 ? VK_FORMAT_R8G8_SRGB : VK_FORMAT_R8G8_UNORM;
    if (data == 4) return number == 4 ? VK_FORMAT_R32_UINT : number == 5 ? VK_FORMAT_R32_SINT : VK_FORMAT_R32_SFLOAT;
    if (data == 5) return number == 1 ? VK_FORMAT_R16G16_SNORM : number == 4 ? VK_FORMAT_R16G16_UINT :
        number == 5 ? VK_FORMAT_R16G16_SINT : number == 7 ? VK_FORMAT_R16G16_SFLOAT : VK_FORMAT_R16G16_UNORM;
    if (data == 10) return number == 1 ? VK_FORMAT_R8G8B8A8_SNORM : number == 4 ? VK_FORMAT_R8G8B8A8_UINT :
        number == 5 ? VK_FORMAT_R8G8B8A8_SINT : number == 9 ? VK_FORMAT_R8G8B8A8_SRGB : VK_FORMAT_R8G8B8A8_UNORM;
    if (data == 11) return number == 4 ? VK_FORMAT_R32G32_UINT : number == 5 ? VK_FORMAT_R32G32_SINT : VK_FORMAT_R32G32_SFLOAT;
    if (data == 12) return number == 1 || number == 6 ? VK_FORMAT_R16G16B16A16_SNORM :
        number == 4 ? VK_FORMAT_R16G16B16A16_UINT : number == 5 ? VK_FORMAT_R16G16B16A16_SINT :
        number == 7 ? VK_FORMAT_R16G16B16A16_SFLOAT : VK_FORMAT_R16G16B16A16_UNORM;
    if (data == 13) return number == 4 ? VK_FORMAT_R32G32B32_UINT : number == 5 ? VK_FORMAT_R32G32B32_SINT : VK_FORMAT_R32G32B32_SFLOAT;
    if (data == 14) return number == 4 ? VK_FORMAT_R32G32B32A32_UINT : number == 5 ? VK_FORMAT_R32G32B32A32_SINT : VK_FORMAT_R32G32B32A32_SFLOAT;
    switch (components) { case 2: return VK_FORMAT_R32G32_SFLOAT; case 3: return VK_FORMAT_R32G32B32_SFLOAT;
    case 4: return VK_FORMAT_R32G32B32A32_SFLOAT; default: return VK_FORMAT_R32_SFLOAT; }
}

VkSamplerAddressMode sampler_address(uint32_t value) {
    switch (value) { case 0: return VK_SAMPLER_ADDRESS_MODE_REPEAT;
    case 1: return VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT;
    case 2: return VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    case 3: case 5: case 7: return VK_SAMPLER_ADDRESS_MODE_MIRROR_CLAMP_TO_EDGE;
    case 4: case 6: return VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_BORDER;
    default: return VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE; }
}
VkCompareOp compare_op(uint32_t value) {
    switch (value) { case 1: return VK_COMPARE_OP_LESS; case 2: return VK_COMPARE_OP_EQUAL;
    case 3: return VK_COMPARE_OP_LESS_OR_EQUAL; case 4: return VK_COMPARE_OP_GREATER;
    case 5: return VK_COMPARE_OP_NOT_EQUAL; case 6: return VK_COMPARE_OP_GREATER_OR_EQUAL;
    case 7: return VK_COMPARE_OP_ALWAYS; default: return VK_COMPARE_OP_NEVER; }
}
se_gpu_result create_sampler(se_gpu_backend* b, const se_gpu_sampler& source, VkSampler* output) {
    uint32_t compare = (source.words[0] >> 12) & 7u;
    uint32_t border = source.words[3] >> 30;
    uint32_t bias_bits = source.words[2] & 0x3fffu;
    int32_t signed_bias = static_cast<int32_t>((bias_bits ^ 0x2000u) - 0x2000u);
    VkSamplerCreateInfo info{}; info.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    info.addressModeU = sampler_address(source.words[0] & 7u);
    info.addressModeV = sampler_address((source.words[0] >> 3) & 7u);
    info.addressModeW = sampler_address((source.words[0] >> 6) & 7u);
    info.magFilter = ((source.words[2] >> 20) & 3u) == 1 || ((source.words[2] >> 20) & 3u) == 3 ?
        VK_FILTER_LINEAR : VK_FILTER_NEAREST;
    info.minFilter = ((source.words[2] >> 22) & 3u) == 1 || ((source.words[2] >> 22) & 3u) == 3 ?
        VK_FILTER_LINEAR : VK_FILTER_NEAREST;
    info.mipmapMode = ((source.words[2] >> 26) & 3u) == 2 ?
        VK_SAMPLER_MIPMAP_MODE_LINEAR : VK_SAMPLER_MIPMAP_MODE_NEAREST;
    info.mipLodBias = static_cast<float>(signed_bias) / 256.0f;
    info.minLod = static_cast<float>(source.words[1] & 0xfffu) / 256.0f;
    info.maxLod = std::max(info.minLod,
        static_cast<float>((source.words[1] >> 12) & 0xfffu) / 256.0f);
    info.compareEnable = compare ? VK_TRUE : VK_FALSE; info.compareOp = compare_op(compare);
    info.borderColor = border == 1 ? VK_BORDER_COLOR_FLOAT_TRANSPARENT_BLACK :
        border == 2 ? VK_BORDER_COLOR_FLOAT_OPAQUE_WHITE : VK_BORDER_COLOR_FLOAT_OPAQUE_BLACK;
    return vkCreateSampler(b->device, &info, nullptr, output) == VK_SUCCESS ? SE_GPU_OK :
        fail(b, SE_GPU_VULKAN_ERROR, "guest sampler creation failed");
}

VkComponentSwizzle component_swizzle(uint32_t selector) {
    switch (selector) { case 0: return VK_COMPONENT_SWIZZLE_ZERO; case 1: return VK_COMPONENT_SWIZZLE_ONE;
    case 4: return VK_COMPONENT_SWIZZLE_R; case 5: return VK_COMPONENT_SWIZZLE_G;
    case 6: return VK_COMPONENT_SWIZZLE_B; case 7: return VK_COMPONENT_SWIZZLE_A;
    default: return VK_COMPONENT_SWIZZLE_IDENTITY; }
}
se_gpu_result create_texture_view(se_gpu_backend* b, guest_image* image,
    const se_gpu_texture& texture, VkImageView* output) {
    uint32_t select = texture.dst_select ? texture.dst_select : 0xfacu;
    if (texture.is_storage || (select == 0xfacu && texture.mip_level == 0)) {
        *output = image->view; return SE_GPU_OK;
    }
    if (texture.mip_level != 0)
        return fail(b, SE_GPU_INVALID_ARGUMENT, "guest texture requests an unavailable mip level");
    VkImageViewCreateInfo info{}; info.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    info.image = image->image; info.viewType = VK_IMAGE_VIEW_TYPE_2D; info.format = image->format;
    info.components = {component_swizzle(select & 7u), component_swizzle((select >> 3) & 7u),
        component_swizzle((select >> 6) & 7u), component_swizzle((select >> 9) & 7u)};
    info.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    return vkCreateImageView(b->device, &info, nullptr, output) == VK_SUCCESS ? SE_GPU_OK :
        fail(b, SE_GPU_VULKAN_ERROR, "swizzled guest texture view creation failed");
}
}
extern "C" {
uint32_t SE_GPU_CALL se_gpu_abi_version(void) { return SE_GPU_ABI_VERSION; }
const char* SE_GPU_CALL se_gpu_last_error(const se_gpu_backend* b) {
    return b ? b->error.c_str() : global_error.c_str();
}
se_gpu_result SE_GPU_CALL se_gpu_create(const se_gpu_create_info* info, se_gpu_backend** out) {
    if (!info || !out || info->struct_size < sizeof(*info) || !info->width || !info->height)
        return SE_GPU_INVALID_ARGUMENT;
    *out = nullptr;
    if (info->abi_version != SE_GPU_ABI_VERSION) return SE_GPU_INCOMPATIBLE_ABI;
    auto* b = new (std::nothrow) se_gpu_backend{};
    if (!b) return SE_GPU_OUT_OF_MEMORY;
    b->log = info->log; b->log_user = info->log_user;
    // SharpEmu owns the process entry point (and is a WinExe on Windows), so
    // SDL never gets its usual SDL_main bootstrap. Mark the host entry point
    // ready before initializing video from the dedicated renderer thread.
    SDL_SetMainReady();
    if (!SDL_Init(SDL_INIT_VIDEO | SDL_INIT_GAMEPAD)) { cleanup(b); return fail(nullptr, SE_GPU_PLATFORM_ERROR, SDL_GetError()); }
    b->window = SDL_CreateWindow(info->title_utf8 ? info->title_utf8 : "SharpEmu",
        static_cast<int>(info->width), static_cast<int>(info->height), SDL_WINDOW_VULKAN | SDL_WINDOW_RESIZABLE);
    if (!b->window) { cleanup(b); return fail(nullptr, SE_GPU_PLATFORM_ERROR, SDL_GetError()); }
    if (!SDL_ShowWindow(b->window)) { cleanup(b); return fail(nullptr, SE_GPU_PLATFORM_ERROR, SDL_GetError()); }
    SDL_RaiseWindow(b->window);
    SDL_PumpEvents();
    uint32_t count{}; const char* const* extensions = SDL_Vulkan_GetInstanceExtensions(&count);
    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO, nullptr, "SharpEmu", 1,
        "SharpEmu native guest GPU", 1, VK_API_VERSION_1_2};
    VkInstanceCreateInfo create{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO, nullptr, 0, &app,
        0, nullptr, count, extensions};
    VkResult vr = vkCreateInstance(&create, nullptr, &b->instance);
    if (vr != VK_SUCCESS) { cleanup(b); return fail(nullptr, SE_GPU_VULKAN_ERROR, "vkCreateInstance failed"); }
    if (!SDL_Vulkan_CreateSurface(b->window, b->instance, nullptr, &b->surface)) {
        cleanup(b); return fail(nullptr, SE_GPU_VULKAN_ERROR, SDL_GetError());
    }
    uint32_t device_count{}; vkEnumeratePhysicalDevices(b->instance, &device_count, nullptr);
    if (!device_count) { cleanup(b); return fail(nullptr, SE_GPU_VULKAN_ERROR, "no Vulkan device"); }
    VkPhysicalDevice devices[16]{}; device_count = device_count > 16 ? 16 : device_count;
    vkEnumeratePhysicalDevices(b->instance, &device_count, devices);
    for (uint32_t d = 0; d < device_count && !b->physical_device; ++d) {
        uint32_t families{}; vkGetPhysicalDeviceQueueFamilyProperties(devices[d], &families, nullptr);
        std::vector<VkQueueFamilyProperties> properties(families);
        vkGetPhysicalDeviceQueueFamilyProperties(devices[d], &families, properties.data());
        for (uint32_t q = 0; q < families; ++q) { VkBool32 present{};
            vkGetPhysicalDeviceSurfaceSupportKHR(devices[d], q, b->surface, &present);
            if (present && (properties[q].queueFlags & VK_QUEUE_GRAPHICS_BIT) != 0) {
                b->physical_device = devices[d]; b->queue_family = q; break;
            }
        }
    }
    if (!b->physical_device) { cleanup(b); return fail(nullptr, SE_GPU_VULKAN_ERROR, "no presentation queue"); }
    float priority = 1; VkDeviceQueueCreateInfo queue{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
        nullptr, 0, b->queue_family, 1, &priority};
    const char* device_extensions[]{VK_KHR_SWAPCHAIN_EXTENSION_NAME};
    VkDeviceCreateInfo device{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, nullptr, 0, 1, &queue,
        0, nullptr, 1, device_extensions, nullptr};
    vr = vkCreateDevice(b->physical_device, &device, nullptr, &b->device);
    if (vr != VK_SUCCESS) { cleanup(b); return fail(nullptr, SE_GPU_VULKAN_ERROR, "vkCreateDevice failed"); }
    vkGetDeviceQueue(b->device, b->queue_family, 0, &b->queue);
    se_gpu_result result = create_commands(b);
    if (result == SE_GPU_OK) result = create_swapchain(b);
    if (result != SE_GPU_OK) { global_error = b->error; cleanup(b); return result; }
    *out = b; return SE_GPU_OK;
}
void SE_GPU_CALL se_gpu_destroy(se_gpu_backend* b) { cleanup(b); }
se_gpu_result SE_GPU_CALL se_gpu_poll(se_gpu_backend* b, uint32_t* close) {
    if (!b || !close) return SE_GPU_INVALID_ARGUMENT;
    *close = 0;
    SDL_Event e{};
    while (SDL_PollEvent(&e)) {
        if (e.type == SDL_EVENT_QUIT || e.type == SDL_EVENT_WINDOW_CLOSE_REQUESTED) *close = 1;
        if (e.type == SDL_EVENT_WINDOW_RESIZED || e.type == SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED) b->resized = true;
    }
    return SE_GPU_OK;
}
se_gpu_result SE_GPU_CALL se_gpu_input_snapshot(se_gpu_backend* b, se_gpu_input* state) {
    if (!b || !state || state->struct_size < sizeof(*state)) return SE_GPU_INVALID_ARGUMENT;
    uint32_t size = state->struct_size; std::memset(state, 0, sizeof(*state)); state->struct_size = size;
    state->keyboard_focused = (SDL_GetWindowFlags(b->window) & SDL_WINDOW_INPUT_FOCUS) != 0 ? 1u : 0u;
    int count{}; const bool* keys = SDL_GetKeyboardState(&count);
    auto down = [keys, count](SDL_Scancode key) { return static_cast<int>(key) < count && keys[key]; };
    set_key(state, 0x08, down(SDL_SCANCODE_BACKSPACE)); set_key(state, 0x09, down(SDL_SCANCODE_TAB));
    set_key(state, 0x0d, down(SDL_SCANCODE_RETURN)); set_key(state, 0x1b, down(SDL_SCANCODE_ESCAPE));
    set_key(state, 0x25, down(SDL_SCANCODE_LEFT)); set_key(state, 0x26, down(SDL_SCANCODE_UP));
    set_key(state, 0x27, down(SDL_SCANCODE_RIGHT)); set_key(state, 0x28, down(SDL_SCANCODE_DOWN));
    for (uint32_t index = 0; index < 26; ++index)
        set_key(state, 0x41u + index, down(static_cast<SDL_Scancode>(SDL_SCANCODE_A + index)));
    refresh_gamepad(b); if (!b->gamepad) return SE_GPU_OK;
    state->gamepad_connected = 1;
    auto button = [b](SDL_GamepadButton value) { return SDL_GetGamepadButton(b->gamepad, value); };
    if (button(SDL_GAMEPAD_BUTTON_DPAD_UP)) state->gamepad_buttons |= 1u << 0;
    if (button(SDL_GAMEPAD_BUTTON_DPAD_DOWN)) state->gamepad_buttons |= 1u << 1;
    if (button(SDL_GAMEPAD_BUTTON_DPAD_LEFT)) state->gamepad_buttons |= 1u << 2;
    if (button(SDL_GAMEPAD_BUTTON_DPAD_RIGHT)) state->gamepad_buttons |= 1u << 3;
    if (button(SDL_GAMEPAD_BUTTON_SOUTH)) state->gamepad_buttons |= 1u << 4;
    if (button(SDL_GAMEPAD_BUTTON_EAST)) state->gamepad_buttons |= 1u << 5;
    if (button(SDL_GAMEPAD_BUTTON_WEST)) state->gamepad_buttons |= 1u << 6;
    if (button(SDL_GAMEPAD_BUTTON_NORTH)) state->gamepad_buttons |= 1u << 7;
    if (button(SDL_GAMEPAD_BUTTON_LEFT_SHOULDER)) state->gamepad_buttons |= 1u << 8;
    if (button(SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER)) state->gamepad_buttons |= 1u << 9;
    if (button(SDL_GAMEPAD_BUTTON_LEFT_STICK)) state->gamepad_buttons |= 1u << 12;
    if (button(SDL_GAMEPAD_BUTTON_RIGHT_STICK)) state->gamepad_buttons |= 1u << 13;
    if (button(SDL_GAMEPAD_BUTTON_START)) state->gamepad_buttons |= 1u << 14;
    if (button(SDL_GAMEPAD_BUTTON_BACK)) state->gamepad_buttons |= 1u << 15;
    state->left_x = stick_value(SDL_GetGamepadAxis(b->gamepad, SDL_GAMEPAD_AXIS_LEFTX));
    state->left_y = stick_value(SDL_GetGamepadAxis(b->gamepad, SDL_GAMEPAD_AXIS_LEFTY));
    state->right_x = stick_value(SDL_GetGamepadAxis(b->gamepad, SDL_GAMEPAD_AXIS_RIGHTX));
    state->right_y = stick_value(SDL_GetGamepadAxis(b->gamepad, SDL_GAMEPAD_AXIS_RIGHTY));
    state->left_trigger = trigger_value(SDL_GetGamepadAxis(b->gamepad, SDL_GAMEPAD_AXIS_LEFT_TRIGGER));
    state->right_trigger = trigger_value(SDL_GetGamepadAxis(b->gamepad, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER));
    if (state->left_trigger > 64) state->gamepad_buttons |= 1u << 10;
    if (state->right_trigger > 64) state->gamepad_buttons |= 1u << 11;
    const char* name = SDL_GetGamepadName(b->gamepad);
    if (name) std::strncpy(state->gamepad_name_utf8, name, sizeof(state->gamepad_name_utf8) - 1);
    return SE_GPU_OK;
}
se_gpu_result SE_GPU_CALL se_gpu_present_bgra(
    se_gpu_backend* b, const void* pixels, size_t size, uint32_t width, uint32_t height, uint32_t pitch) {
    if (!b || !pixels || !width || !height || pitch < width * 4u || size < static_cast<size_t>(pitch) * height)
        return SE_GPU_INVALID_ARGUMENT;
    if (vkWaitForFences(b->device, 1, &b->frame_fence, VK_TRUE, UINT64_MAX) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "frame fence wait failed");
    if (b->resized) { vkDeviceWaitIdle(b->device); se_gpu_result recreated = create_swapchain(b);
        if (recreated != SE_GPU_OK) return recreated; }
    uint32_t image_index{};
    VkResult result = vkAcquireNextImageKHR(b->device, b->swapchain, UINT64_MAX,
        b->image_available, VK_NULL_HANDLE, &image_index);
    if (result == VK_ERROR_OUT_OF_DATE_KHR) { b->resized = true; return SE_GPU_NOT_READY; }
    if (result != VK_SUCCESS && result != VK_SUBOPTIMAL_KHR)
        return fail(b, SE_GPU_VULKAN_ERROR, "swapchain image acquisition failed");
    uint32_t copy_width = std::min(width, b->extent.width), copy_height = std::min(height, b->extent.height);
    VkDeviceSize packed_size = static_cast<VkDeviceSize>(copy_width) * copy_height * 4u;
    se_gpu_result staged = ensure_staging(b, packed_size); if (staged != SE_GPU_OK) return staged;
    const auto* source = static_cast<const uint8_t*>(pixels); auto* destination = static_cast<uint8_t*>(b->staging_map);
    bool bgra = b->swapchain_format == VK_FORMAT_B8G8R8A8_UNORM || b->swapchain_format == VK_FORMAT_B8G8R8A8_SRGB;
    for (uint32_t y = 0; y < copy_height; ++y) {
        const uint8_t* row = source + static_cast<size_t>(y) * pitch;
        uint8_t* output = destination + static_cast<size_t>(y) * copy_width * 4u;
        if (bgra) std::memcpy(output, row, static_cast<size_t>(copy_width) * 4u);
        else for (uint32_t x = 0; x < copy_width; ++x) {
            output[x * 4] = row[x * 4 + 2]; output[x * 4 + 1] = row[x * 4 + 1];
            output[x * 4 + 2] = row[x * 4]; output[x * 4 + 3] = row[x * 4 + 3];
        }
    }
    vkResetFences(b->device, 1, &b->frame_fence); vkResetCommandBuffer(b->command_buffer, 0);
    VkCommandBufferBeginInfo begin{}; begin.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    if (vkBeginCommandBuffer(b->command_buffer, &begin) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "command buffer begin failed");
    VkImageMemoryBarrier transfer{}; transfer.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    transfer.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT; transfer.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    transfer.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL; transfer.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    transfer.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED; transfer.image = b->swapchain_images[image_index];
    transfer.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    vkCmdPipelineBarrier(b->command_buffer, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
        0, 0, nullptr, 0, nullptr, 1, &transfer);
    VkBufferImageCopy copy{}; copy.bufferRowLength = copy_width; copy.bufferImageHeight = copy_height;
    copy.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1}; copy.imageExtent = {copy_width, copy_height, 1};
    vkCmdCopyBufferToImage(b->command_buffer, b->staging, b->swapchain_images[image_index],
        VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &copy);
    VkImageMemoryBarrier present = transfer; present.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    present.dstAccessMask = 0; present.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    present.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    vkCmdPipelineBarrier(b->command_buffer, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
        0, 0, nullptr, 0, nullptr, 1, &present);
    if (vkEndCommandBuffer(b->command_buffer) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "command buffer end failed");
    VkPipelineStageFlags wait = VK_PIPELINE_STAGE_TRANSFER_BIT; VkSubmitInfo submit{};
    submit.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO; submit.waitSemaphoreCount = 1;
    submit.pWaitSemaphores = &b->image_available; submit.pWaitDstStageMask = &wait;
    submit.commandBufferCount = 1; submit.pCommandBuffers = &b->command_buffer;
    submit.signalSemaphoreCount = 1; submit.pSignalSemaphores = &b->render_finished;
    if (vkQueueSubmit(b->queue, 1, &submit, b->frame_fence) != VK_SUCCESS)
        return fail(b, SE_GPU_VULKAN_ERROR, "queue submit failed");
    VkPresentInfoKHR info{}; info.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    info.waitSemaphoreCount = 1; info.pWaitSemaphores = &b->render_finished;
    info.swapchainCount = 1; info.pSwapchains = &b->swapchain; info.pImageIndices = &image_index;
    result = vkQueuePresentKHR(b->queue, &info);
    if (result == VK_ERROR_OUT_OF_DATE_KHR || result == VK_SUBOPTIMAL_KHR) { b->resized = true; return SE_GPU_OK; }
    return result == VK_SUCCESS ? SE_GPU_OK : fail(b, SE_GPU_VULKAN_ERROR, "queue present failed");
}
se_gpu_result SE_GPU_CALL se_gpu_submit_draw(se_gpu_backend* b, const se_gpu_draw* work) {
    if (!b || !work || work->struct_size < sizeof(*work) || !work->pixel_spirv.data ||
        !work->vertex_spirv.data || !work->pixel_spirv.size || !work->vertex_spirv.size ||
        (work->pixel_spirv.size % 4) != 0 || (work->vertex_spirv.size % 4) != 0 ||
        !work->width || !work->height || !work->vertex_count || !work->instance_count ||
        (work->texture_count && !work->textures) || (work->memory_buffer_count && !work->memory_buffers) ||
        (work->vertex_buffer_count && !work->vertex_buffers) || (work->target_count && !work->targets))
        return SE_GPU_INVALID_ARGUMENT;
    std::vector<host_buffer> global_buffers(work->memory_buffer_count);
    std::vector<host_buffer> vertex_buffers(work->vertex_buffer_count);
    host_buffer index_buffer{};
    std::vector<guest_image> transient_images; transient_images.reserve(work->texture_count + work->target_count + 1);
    std::vector<guest_image*> textures(work->texture_count);
    std::vector<VkImageView> texture_views(work->texture_count);
    uint32_t target_count = work->target_count ? work->target_count : 1;
    std::vector<guest_image*> targets(target_count);
    std::vector<VkSampler> samplers(work->texture_count);
    VkDescriptorSetLayout descriptor_layout{}; VkPipelineLayout pipeline_layout{};
    VkDescriptorPool descriptor_pool{}; VkDescriptorSet descriptor_set{}; VkRenderPass render_pass{};
    VkFramebuffer framebuffer{}; VkShaderModule vertex_shader{}, pixel_shader{}; VkPipeline pipeline{};
    auto release = [&] {
        if (pipeline) vkDestroyPipeline(b->device, pipeline, nullptr);
        if (pixel_shader) vkDestroyShaderModule(b->device, pixel_shader, nullptr);
        if (vertex_shader) vkDestroyShaderModule(b->device, vertex_shader, nullptr);
        if (framebuffer) vkDestroyFramebuffer(b->device, framebuffer, nullptr);
        if (render_pass) vkDestroyRenderPass(b->device, render_pass, nullptr);
        if (descriptor_pool) vkDestroyDescriptorPool(b->device, descriptor_pool, nullptr);
        if (pipeline_layout) vkDestroyPipelineLayout(b->device, pipeline_layout, nullptr);
        if (descriptor_layout) vkDestroyDescriptorSetLayout(b->device, descriptor_layout, nullptr);
        for (VkSampler sampler : samplers) if (sampler) vkDestroySampler(b->device, sampler, nullptr);
        for (uint32_t index = 0; index < work->texture_count; ++index)
            if (texture_views[index] && texture_views[index] != textures[index]->view)
                vkDestroyImageView(b->device, texture_views[index], nullptr);
        destroy_buffer(b, &index_buffer);
        for (auto& buffer : vertex_buffers) destroy_buffer(b, &buffer);
        for (auto& buffer : global_buffers) destroy_buffer(b, &buffer);
        for (auto& image : transient_images) destroy_image(b, &image);
    };
    se_gpu_result operation = [&]() -> se_gpu_result {
        for (uint32_t index = 0; index < work->memory_buffer_count; ++index) {
            const auto& source = work->memory_buffers[index];
            se_gpu_result result = create_buffer(b, source.data.data, source.data.size,
                VK_BUFFER_USAGE_STORAGE_BUFFER_BIT, &global_buffers[index]);
            if (result != SE_GPU_OK) return result;
        }
        for (uint32_t index = 0; index < work->vertex_buffer_count; ++index) {
            const auto& source = work->vertex_buffers[index];
            se_gpu_result result = create_buffer(b, source.data.data, source.data.size,
                VK_BUFFER_USAGE_VERTEX_BUFFER_BIT, &vertex_buffers[index]);
            if (result != SE_GPU_OK) return result;
        }
        if (work->index_buffer) {
            se_gpu_result result = create_buffer(b, work->index_buffer->data.data,
                work->index_buffer->data.size, VK_BUFFER_USAGE_INDEX_BUFFER_BIT, &index_buffer);
            if (result != SE_GPU_OK) return result;
        }
        for (uint32_t index = 0; index < work->texture_count; ++index) {
            const auto& source = work->textures[index]; guest_image* image{};
            if (source.address) {
                uint32_t row_length = source.tile_mode == 0 ? std::max(source.pitch, source.width) : source.width;
                se_gpu_result result = cached_image(b, source.address, source.width, source.height,
                    source.format, source.number_type, row_length, &source.rgba_pixels, &image);
                if (result != SE_GPU_OK) return result;
            } else {
                transient_images.emplace_back(); image = &transient_images.back();
                se_gpu_result result = create_image(b, source.width, source.height,
                    source.format, source.number_type, image);
                uint32_t row_length = source.tile_mode == 0 ? std::max(source.pitch, source.width) : source.width;
                if (result == SE_GPU_OK) result = upload_image(b, image, source.rgba_pixels, row_length);
                if (result != SE_GPU_OK) return result;
            }
            textures[index] = image;
            se_gpu_result view_result = create_texture_view(b, image, source, &texture_views[index]);
            if (view_result != SE_GPU_OK) return view_result;
        }
        for (uint32_t index = 0; index < target_count; ++index) {
            guest_image* image{};
            if (work->target_count && work->targets[index].address) {
                const auto& source = work->targets[index];
                se_gpu_result result = cached_image(b, source.address, source.width, source.height,
                    source.format, source.number_type, source.width, nullptr, &image);
                if (result != SE_GPU_OK) return result;
            } else {
                transient_images.emplace_back(); image = &transient_images.back();
                uint32_t format = work->target_count ? work->targets[index].format : 10;
                uint32_t number = work->target_count ? work->targets[index].number_type : 0;
                se_gpu_result result = create_image(b, work->width, work->height, format, number, image);
                if (result != SE_GPU_OK) return result;
            }
            targets[index] = image;
        }
        for (uint32_t index = 0; index < work->texture_count; ++index)
            if (!work->textures[index].is_storage) {
                se_gpu_result result = create_sampler(b, work->textures[index].sampler, &samplers[index]);
                if (result != SE_GPU_OK) return result;
            }
        std::vector<VkDescriptorSetLayoutBinding> bindings;
        VkShaderStageFlags resource_stages = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT;
        if (work->memory_buffer_count) {
            VkDescriptorSetLayoutBinding binding{}; binding.binding = 0;
            binding.descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
            binding.descriptorCount = work->memory_buffer_count; binding.stageFlags = resource_stages;
            bindings.push_back(binding);
        }
        for (uint32_t index = 0; index < work->texture_count; ++index) {
            VkDescriptorSetLayoutBinding binding{}; binding.binding = index + 1;
            binding.descriptorType = work->textures[index].is_storage ? VK_DESCRIPTOR_TYPE_STORAGE_IMAGE :
                VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
            binding.descriptorCount = 1; binding.stageFlags = resource_stages; bindings.push_back(binding);
        }
        if (!bindings.empty()) {
            VkDescriptorSetLayoutCreateInfo info{}; info.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
            info.bindingCount = static_cast<uint32_t>(bindings.size()); info.pBindings = bindings.data();
            if (vkCreateDescriptorSetLayout(b->device, &info, nullptr, &descriptor_layout) != VK_SUCCESS)
                return fail(b, SE_GPU_VULKAN_ERROR, "graphics descriptor layout creation failed");
        }
        VkPipelineLayoutCreateInfo pipeline_layout_info{};
        pipeline_layout_info.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
        if (descriptor_layout) { pipeline_layout_info.setLayoutCount = 1; pipeline_layout_info.pSetLayouts = &descriptor_layout; }
        if (vkCreatePipelineLayout(b->device, &pipeline_layout_info, nullptr, &pipeline_layout) != VK_SUCCESS)
            return fail(b, SE_GPU_VULKAN_ERROR, "graphics pipeline layout creation failed");
        if (!bindings.empty()) {
            std::vector<VkDescriptorPoolSize> sizes;
            if (work->memory_buffer_count) sizes.push_back({VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, work->memory_buffer_count});
            uint32_t sampled{}, storage{};
            for (uint32_t index = 0; index < work->texture_count; ++index)
                work->textures[index].is_storage ? ++storage : ++sampled;
            if (sampled) sizes.push_back({VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, sampled});
            if (storage) sizes.push_back({VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, storage});
            VkDescriptorPoolCreateInfo pool{}; pool.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
            pool.maxSets = 1; pool.poolSizeCount = static_cast<uint32_t>(sizes.size()); pool.pPoolSizes = sizes.data();
            if (vkCreateDescriptorPool(b->device, &pool, nullptr, &descriptor_pool) != VK_SUCCESS)
                return fail(b, SE_GPU_VULKAN_ERROR, "graphics descriptor pool creation failed");
            VkDescriptorSetAllocateInfo allocate{}; allocate.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
            allocate.descriptorPool = descriptor_pool; allocate.descriptorSetCount = 1; allocate.pSetLayouts = &descriptor_layout;
            if (vkAllocateDescriptorSets(b->device, &allocate, &descriptor_set) != VK_SUCCESS)
                return fail(b, SE_GPU_VULKAN_ERROR, "graphics descriptor allocation failed");
            std::vector<VkDescriptorBufferInfo> buffer_info(work->memory_buffer_count);
            for (uint32_t index = 0; index < work->memory_buffer_count; ++index)
                buffer_info[index] = {global_buffers[index].buffer, 0, global_buffers[index].size};
            std::vector<VkDescriptorImageInfo> image_info(work->texture_count);
            std::vector<VkWriteDescriptorSet> writes;
            if (work->memory_buffer_count) {
                VkWriteDescriptorSet write{}; write.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
                write.dstSet = descriptor_set; write.dstBinding = 0; write.descriptorCount = work->memory_buffer_count;
                write.descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER; write.pBufferInfo = buffer_info.data();
                writes.push_back(write);
            }
            for (uint32_t index = 0; index < work->texture_count; ++index) {
                bool storage_image = work->textures[index].is_storage != 0;
                image_info[index] = {storage_image ? VK_NULL_HANDLE : samplers[index], texture_views[index], VK_IMAGE_LAYOUT_GENERAL};
                VkWriteDescriptorSet write{}; write.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
                write.dstSet = descriptor_set; write.dstBinding = index + 1; write.descriptorCount = 1;
                write.descriptorType = storage_image ? VK_DESCRIPTOR_TYPE_STORAGE_IMAGE :
                    VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; write.pImageInfo = &image_info[index]; writes.push_back(write);
            }
            vkUpdateDescriptorSets(b->device, static_cast<uint32_t>(writes.size()), writes.data(), 0, nullptr);
        }
        std::vector<VkAttachmentDescription> attachments(target_count);
        std::vector<VkAttachmentReference> references(target_count);
        for (uint32_t index = 0; index < target_count; ++index) {
            attachments[index].format = targets[index]->format; attachments[index].samples = VK_SAMPLE_COUNT_1_BIT;
            attachments[index].loadOp = VK_ATTACHMENT_LOAD_OP_LOAD; attachments[index].storeOp = VK_ATTACHMENT_STORE_OP_STORE;
            attachments[index].stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
            attachments[index].stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
            attachments[index].initialLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
            attachments[index].finalLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
            references[index] = {index, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL};
        }
        VkSubpassDescription subpass{}; subpass.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
        subpass.colorAttachmentCount = target_count; subpass.pColorAttachments = references.data();
        VkRenderPassCreateInfo pass{}; pass.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO;
        pass.attachmentCount = target_count; pass.pAttachments = attachments.data(); pass.subpassCount = 1; pass.pSubpasses = &subpass;
        if (vkCreateRenderPass(b->device, &pass, nullptr, &render_pass) != VK_SUCCESS)
            return fail(b, SE_GPU_VULKAN_ERROR, "render pass creation failed");
        std::vector<VkImageView> views(target_count);
        for (uint32_t index = 0; index < target_count; ++index) views[index] = targets[index]->view;
        VkFramebufferCreateInfo frame{}; frame.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
        frame.renderPass = render_pass; frame.attachmentCount = target_count; frame.pAttachments = views.data();
        frame.width = work->width; frame.height = work->height; frame.layers = 1;
        if (vkCreateFramebuffer(b->device, &frame, nullptr, &framebuffer) != VK_SUCCESS)
            return fail(b, SE_GPU_VULKAN_ERROR, "framebuffer creation failed");
        VkShaderModuleCreateInfo module{}; module.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
        module.codeSize = work->vertex_spirv.size; module.pCode = static_cast<const uint32_t*>(work->vertex_spirv.data);
        if (vkCreateShaderModule(b->device, &module, nullptr, &vertex_shader) != VK_SUCCESS)
            return fail(b, SE_GPU_VULKAN_ERROR, "vertex shader module creation failed");
        module.codeSize = work->pixel_spirv.size; module.pCode = static_cast<const uint32_t*>(work->pixel_spirv.data);
        if (vkCreateShaderModule(b->device, &module, nullptr, &pixel_shader) != VK_SUCCESS)
            return fail(b, SE_GPU_VULKAN_ERROR, "pixel shader module creation failed");
        VkPipelineShaderStageCreateInfo stages[2]{};
        stages[0].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT; stages[0].module = vertex_shader; stages[0].pName = "main";
        stages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT; stages[1].module = pixel_shader; stages[1].pName = "main";
        std::vector<VkVertexInputBindingDescription> vertex_bindings(work->vertex_buffer_count);
        std::vector<VkVertexInputAttributeDescription> attributes(work->vertex_buffer_count);
        for (uint32_t index = 0; index < work->vertex_buffer_count; ++index) {
            const auto& source = work->vertex_buffers[index];
            vertex_bindings[index] = {index, source.stride ? source.stride : std::max(source.component_count, 1u) * 4u,
                VK_VERTEX_INPUT_RATE_VERTEX};
            attributes[index] = {source.location, index,
                vertex_format(source.data_format, source.number_format, source.component_count), 0};
        }
        VkPipelineVertexInputStateCreateInfo vertex_input{};
        vertex_input.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;
        vertex_input.vertexBindingDescriptionCount = work->vertex_buffer_count;
        vertex_input.pVertexBindingDescriptions = vertex_bindings.data();
        vertex_input.vertexAttributeDescriptionCount = work->vertex_buffer_count;
        vertex_input.pVertexAttributeDescriptions = attributes.data();
        VkPipelineInputAssemblyStateCreateInfo assembly{};
        assembly.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO;
        assembly.topology = primitive_topology(work->primitive_type);
        VkPipelineViewportStateCreateInfo viewport_state{};
        viewport_state.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO;
        viewport_state.viewportCount = 1; viewport_state.scissorCount = 1;
        VkPipelineRasterizationStateCreateInfo raster{};
        raster.sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO;
        raster.polygonMode = VK_POLYGON_MODE_FILL; raster.cullMode = VK_CULL_MODE_NONE;
        raster.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE; raster.lineWidth = 1.0f;
        VkPipelineMultisampleStateCreateInfo multisample{};
        multisample.sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO;
        multisample.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
        std::vector<VkPipelineColorBlendAttachmentState> color_blends(target_count);
        for (uint32_t index = 0; index < target_count; ++index) {
            const se_gpu_blend* source = work->blend_count ? &work->blends[std::min(index, work->blend_count - 1)] : nullptr;
            auto& blend = color_blends[index]; blend.colorWriteMask = source ? source->write_mask : 0xf;
            if (source) { blend.blendEnable = source->enable; blend.srcColorBlendFactor = blend_factor(source->color_src);
                blend.dstColorBlendFactor = blend_factor(source->color_dst); blend.colorBlendOp = blend_op(source->color_func);
                blend.srcAlphaBlendFactor = blend_factor(source->separate_alpha ? source->alpha_src : source->color_src);
                blend.dstAlphaBlendFactor = blend_factor(source->separate_alpha ? source->alpha_dst : source->color_dst);
                blend.alphaBlendOp = blend_op(source->separate_alpha ? source->alpha_func : source->color_func); }
        }
        VkPipelineColorBlendStateCreateInfo blend_state{};
        blend_state.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO;
        blend_state.attachmentCount = target_count; blend_state.pAttachments = color_blends.data();
        VkDynamicState dynamic_values[]{VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR};
        VkPipelineDynamicStateCreateInfo dynamic{}; dynamic.sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO;
        dynamic.dynamicStateCount = 2; dynamic.pDynamicStates = dynamic_values;
        VkGraphicsPipelineCreateInfo pipeline_info{}; pipeline_info.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO;
        pipeline_info.stageCount = 2; pipeline_info.pStages = stages; pipeline_info.pVertexInputState = &vertex_input;
        pipeline_info.pInputAssemblyState = &assembly; pipeline_info.pViewportState = &viewport_state;
        pipeline_info.pRasterizationState = &raster; pipeline_info.pMultisampleState = &multisample;
        pipeline_info.pColorBlendState = &blend_state; pipeline_info.pDynamicState = &dynamic;
        pipeline_info.layout = pipeline_layout; pipeline_info.renderPass = render_pass;
        if (vkCreateGraphicsPipelines(b->device, b->pipeline_cache, 1, &pipeline_info, nullptr, &pipeline) != VK_SUCCESS)
            return fail(b, SE_GPU_VULKAN_ERROR, "graphics pipeline creation failed");
        se_gpu_result begun = begin_immediate(b); if (begun != SE_GPU_OK) return begun;
        for (guest_image* target : targets) image_barrier(b, target, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
            VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT | VK_ACCESS_TRANSFER_WRITE_BIT,
            VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
            VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT);
        VkRenderPassBeginInfo render{}; render.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
        render.renderPass = render_pass; render.framebuffer = framebuffer;
        render.renderArea.extent = {work->width, work->height};
        vkCmdBeginRenderPass(b->command_buffer, &render, VK_SUBPASS_CONTENTS_INLINE);
        vkCmdBindPipeline(b->command_buffer, VK_PIPELINE_BIND_POINT_GRAPHICS, pipeline);
        if (descriptor_set) vkCmdBindDescriptorSets(b->command_buffer, VK_PIPELINE_BIND_POINT_GRAPHICS,
            pipeline_layout, 0, 1, &descriptor_set, 0, nullptr);
        if (!vertex_buffers.empty()) {
            std::vector<VkBuffer> handles(vertex_buffers.size()); std::vector<VkDeviceSize> offsets(vertex_buffers.size());
            for (size_t index = 0; index < vertex_buffers.size(); ++index) {
                handles[index] = vertex_buffers[index].buffer;
                offsets[index] = work->vertex_buffers[index].offset_bytes < vertex_buffers[index].size ?
                    work->vertex_buffers[index].offset_bytes : 0;
            }
            vkCmdBindVertexBuffers(b->command_buffer, 0, static_cast<uint32_t>(handles.size()), handles.data(), offsets.data());
        }
        VkViewport viewport{0, 0, static_cast<float>(work->width), static_cast<float>(work->height), 0, 1};
        if (work->viewport) viewport = {work->viewport->x, work->viewport->y, work->viewport->width,
            work->viewport->height, work->viewport->min_depth, work->viewport->max_depth};
        vkCmdSetViewport(b->command_buffer, 0, 1, &viewport);
        VkRect2D scissor{{0, 0}, {work->width, work->height}};
        if (work->scissor) { int32_t x = std::clamp(work->scissor->x, 0, static_cast<int32_t>(work->width));
            int32_t y = std::clamp(work->scissor->y, 0, static_cast<int32_t>(work->height));
            scissor.offset = {x, y}; scissor.extent.width = std::min(work->scissor->width, work->width - static_cast<uint32_t>(x));
            scissor.extent.height = std::min(work->scissor->height, work->height - static_cast<uint32_t>(y)); }
        vkCmdSetScissor(b->command_buffer, 0, 1, &scissor);
        if (work->index_buffer) {
            vkCmdBindIndexBuffer(b->command_buffer, index_buffer.buffer, 0,
                work->index_buffer->is_32_bit ? VK_INDEX_TYPE_UINT32 : VK_INDEX_TYPE_UINT16);
            uint32_t count = static_cast<uint32_t>(work->index_buffer->data.size /
                (work->index_buffer->is_32_bit ? sizeof(uint32_t) : sizeof(uint16_t)));
            vkCmdDrawIndexed(b->command_buffer, count, work->instance_count, 0, 0, 0);
        } else {
            uint32_t count = work->primitive_type == 0x11 ? 4 : work->vertex_count;
            vkCmdDraw(b->command_buffer, count, work->instance_count, 0, 0);
        }
        vkCmdEndRenderPass(b->command_buffer);
        for (guest_image* target : targets) image_barrier(b, target, VK_IMAGE_LAYOUT_GENERAL,
            VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT |
                VK_ACCESS_TRANSFER_READ_BIT, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
            VK_PIPELINE_STAGE_ALL_COMMANDS_BIT);
        return end_immediate(b);
    }();
    if (operation == SE_GPU_OK && work->target_count == 0) operation = present_image(b, targets[0]);
    release(); return operation;
}
se_gpu_result SE_GPU_CALL se_gpu_submit_compute(se_gpu_backend* b, const se_gpu_compute* work) {
    if (!b || !work || work->struct_size < sizeof(*work) || !work->spirv.data ||
        work->spirv.size == 0 || (work->spirv.size % 4) != 0 ||
        !work->groups_x || !work->groups_y || !work->groups_z ||
        (work->texture_count && !work->textures) || (work->memory_buffer_count && !work->memory_buffers))
        return SE_GPU_INVALID_ARGUMENT;
    std::vector<host_buffer> buffers(work->memory_buffer_count);
    std::vector<guest_image> transient_images;
    std::vector<guest_image*> images(work->texture_count);
    std::vector<VkImageView> texture_views(work->texture_count);
    std::vector<VkSampler> samplers(work->texture_count);
    VkDescriptorSetLayout descriptor_layout{}; VkPipelineLayout pipeline_layout{};
    VkDescriptorPool descriptor_pool{}; VkDescriptorSet descriptor_set{};
    VkShaderModule shader{}; VkPipeline pipeline{};
    auto release = [&] {
        if (pipeline) vkDestroyPipeline(b->device, pipeline, nullptr);
        if (shader) vkDestroyShaderModule(b->device, shader, nullptr);
        if (descriptor_pool) vkDestroyDescriptorPool(b->device, descriptor_pool, nullptr);
        if (pipeline_layout) vkDestroyPipelineLayout(b->device, pipeline_layout, nullptr);
        if (descriptor_layout) vkDestroyDescriptorSetLayout(b->device, descriptor_layout, nullptr);
        for (VkSampler sampler : samplers) if (sampler) vkDestroySampler(b->device, sampler, nullptr);
        for (uint32_t index = 0; index < work->texture_count; ++index)
            if (texture_views[index] && texture_views[index] != images[index]->view)
                vkDestroyImageView(b->device, texture_views[index], nullptr);
        for (auto& image : transient_images) destroy_image(b, &image);
        for (auto& buffer : buffers) destroy_buffer(b, &buffer);
    };
    se_gpu_result operation = [&]() -> se_gpu_result {
        for (uint32_t index = 0; index < work->memory_buffer_count; ++index) {
            const auto& source = work->memory_buffers[index];
            se_gpu_result result = create_buffer(b, source.data.data, source.data.size,
                VK_BUFFER_USAGE_STORAGE_BUFFER_BIT, &buffers[index]);
            if (result != SE_GPU_OK) return result;
        }
        transient_images.reserve(work->texture_count);
        for (uint32_t index = 0; index < work->texture_count; ++index) {
            const auto& source = work->textures[index]; guest_image* image{};
            if (source.address) {
                uint32_t row_length = source.tile_mode == 0 ? std::max(source.pitch, source.width) : source.width;
                se_gpu_result result = cached_image(b, source.address, source.width, source.height,
                    source.format, source.number_type, row_length, &source.rgba_pixels, &image);
                if (result != SE_GPU_OK) return result;
            } else {
                transient_images.emplace_back(); image = &transient_images.back();
                se_gpu_result result = create_image(b, source.width, source.height,
                    source.format, source.number_type, image);
                uint32_t row_length = source.tile_mode == 0 ? std::max(source.pitch, source.width) : source.width;
                if (result == SE_GPU_OK) result = upload_image(b, image, source.rgba_pixels, row_length);
                if (result != SE_GPU_OK) return result;
            }
            images[index] = image;
            se_gpu_result view_result = create_texture_view(b, image, source, &texture_views[index]);
            if (view_result != SE_GPU_OK) return view_result;
        }
        for (uint32_t index = 0; index < work->texture_count; ++index)
            if (!work->textures[index].is_storage) {
                se_gpu_result result = create_sampler(b, work->textures[index].sampler, &samplers[index]);
                if (result != SE_GPU_OK) return result;
            }
        std::vector<VkDescriptorSetLayoutBinding> bindings;
        if (work->memory_buffer_count) {
            VkDescriptorSetLayoutBinding binding{}; binding.binding = 0;
            binding.descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
            binding.descriptorCount = work->memory_buffer_count; binding.stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
            bindings.push_back(binding);
        }
        for (uint32_t index = 0; index < work->texture_count; ++index) {
            VkDescriptorSetLayoutBinding binding{}; binding.binding = index + 1;
            binding.descriptorType = work->textures[index].is_storage ? VK_DESCRIPTOR_TYPE_STORAGE_IMAGE :
                VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
            binding.descriptorCount = 1; binding.stageFlags = VK_SHADER_STAGE_COMPUTE_BIT; bindings.push_back(binding);
        }
        if (!bindings.empty()) {
            VkDescriptorSetLayoutCreateInfo info{}; info.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
            info.bindingCount = static_cast<uint32_t>(bindings.size()); info.pBindings = bindings.data();
            if (vkCreateDescriptorSetLayout(b->device, &info, nullptr, &descriptor_layout) != VK_SUCCESS)
                return fail(b, SE_GPU_VULKAN_ERROR, "compute descriptor layout creation failed");
        }
        VkPipelineLayoutCreateInfo pipeline_layout_info{};
        pipeline_layout_info.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
        if (descriptor_layout) { pipeline_layout_info.setLayoutCount = 1; pipeline_layout_info.pSetLayouts = &descriptor_layout; }
        if (vkCreatePipelineLayout(b->device, &pipeline_layout_info, nullptr, &pipeline_layout) != VK_SUCCESS)
            return fail(b, SE_GPU_VULKAN_ERROR, "compute pipeline layout creation failed");
        if (!bindings.empty()) {
            std::vector<VkDescriptorPoolSize> sizes;
            if (work->memory_buffer_count) sizes.push_back({VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, work->memory_buffer_count});
            uint32_t sampled{}, storage{};
            for (uint32_t index = 0; index < work->texture_count; ++index)
                work->textures[index].is_storage ? ++storage : ++sampled;
            if (sampled) sizes.push_back({VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, sampled});
            if (storage) sizes.push_back({VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, storage});
            VkDescriptorPoolCreateInfo pool{}; pool.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
            pool.maxSets = 1; pool.poolSizeCount = static_cast<uint32_t>(sizes.size()); pool.pPoolSizes = sizes.data();
            if (vkCreateDescriptorPool(b->device, &pool, nullptr, &descriptor_pool) != VK_SUCCESS)
                return fail(b, SE_GPU_VULKAN_ERROR, "compute descriptor pool creation failed");
            VkDescriptorSetAllocateInfo allocate{}; allocate.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
            allocate.descriptorPool = descriptor_pool; allocate.descriptorSetCount = 1;
            allocate.pSetLayouts = &descriptor_layout;
            if (vkAllocateDescriptorSets(b->device, &allocate, &descriptor_set) != VK_SUCCESS)
                return fail(b, SE_GPU_VULKAN_ERROR, "compute descriptor allocation failed");
            std::vector<VkDescriptorBufferInfo> buffer_info(work->memory_buffer_count);
            for (uint32_t index = 0; index < work->memory_buffer_count; ++index)
                buffer_info[index] = {buffers[index].buffer, 0, buffers[index].size};
            std::vector<VkDescriptorImageInfo> image_info(work->texture_count);
            std::vector<VkWriteDescriptorSet> writes;
            if (work->memory_buffer_count) {
                VkWriteDescriptorSet write{}; write.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
                write.dstSet = descriptor_set; write.dstBinding = 0;
                write.descriptorCount = work->memory_buffer_count; write.descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
                write.pBufferInfo = buffer_info.data(); writes.push_back(write);
            }
            for (uint32_t index = 0; index < work->texture_count; ++index) {
                bool storage_image = work->textures[index].is_storage != 0;
                image_info[index] = {storage_image ? VK_NULL_HANDLE : samplers[index], texture_views[index],
                    VK_IMAGE_LAYOUT_GENERAL};
                VkWriteDescriptorSet write{}; write.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
                write.dstSet = descriptor_set; write.dstBinding = index + 1; write.descriptorCount = 1;
                write.descriptorType = storage_image ? VK_DESCRIPTOR_TYPE_STORAGE_IMAGE :
                    VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; write.pImageInfo = &image_info[index];
                writes.push_back(write);
            }
            vkUpdateDescriptorSets(b->device, static_cast<uint32_t>(writes.size()), writes.data(), 0, nullptr);
        }
        VkShaderModuleCreateInfo shader_info{}; shader_info.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
        shader_info.codeSize = work->spirv.size; shader_info.pCode = static_cast<const uint32_t*>(work->spirv.data);
        if (vkCreateShaderModule(b->device, &shader_info, nullptr, &shader) != VK_SUCCESS)
            return fail(b, SE_GPU_VULKAN_ERROR, "compute shader module creation failed");
        VkComputePipelineCreateInfo pipeline_info{}; pipeline_info.sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO;
        pipeline_info.stage.sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        pipeline_info.stage.stage = VK_SHADER_STAGE_COMPUTE_BIT; pipeline_info.stage.module = shader;
        pipeline_info.stage.pName = "main"; pipeline_info.layout = pipeline_layout;
        if (vkCreateComputePipelines(b->device, b->pipeline_cache, 1, &pipeline_info, nullptr, &pipeline) != VK_SUCCESS)
            return fail(b, SE_GPU_VULKAN_ERROR, "compute pipeline creation failed");
        se_gpu_result begun = begin_immediate(b); if (begun != SE_GPU_OK) return begun;
        vkCmdBindPipeline(b->command_buffer, VK_PIPELINE_BIND_POINT_COMPUTE, pipeline);
        if (descriptor_set) vkCmdBindDescriptorSets(b->command_buffer, VK_PIPELINE_BIND_POINT_COMPUTE,
            pipeline_layout, 0, 1, &descriptor_set, 0, nullptr);
        vkCmdDispatch(b->command_buffer, work->groups_x, work->groups_y, work->groups_z);
        VkMemoryBarrier barrier{}; barrier.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER;
        barrier.srcAccessMask = VK_ACCESS_SHADER_WRITE_BIT;
        barrier.dstAccessMask = VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_TRANSFER_READ_BIT;
        vkCmdPipelineBarrier(b->command_buffer, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
            VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 1, &barrier, 0, nullptr, 0, nullptr);
        return end_immediate(b);
    }();
    release(); return operation;
}
se_gpu_result SE_GPU_CALL se_gpu_register_display_buffer(se_gpu_backend* b, uint64_t address, uint32_t format) {
    if (!b || !address) return SE_GPU_INVALID_ARGUMENT;
    b->display_formats[address] = format; return SE_GPU_OK;
}
se_gpu_result SE_GPU_CALL se_gpu_present_guest_image(
    se_gpu_backend* b, uint64_t address, uint32_t width, uint32_t height, uint32_t pitch) {
    if (!b || !address || !width || !height || pitch < width) return SE_GPU_INVALID_ARGUMENT;
    auto found = b->guest_images.find(address); if (found == b->guest_images.end()) return SE_GPU_NOT_FOUND;
    return present_image(b, &found->second);
}
se_gpu_result SE_GPU_CALL se_gpu_has_guest_image(
    se_gpu_backend* b, uint64_t address, uint32_t format, uint32_t number_type) {
    if (!b || !address) return SE_GPU_INVALID_ARGUMENT;
    auto found = b->guest_images.find(address); if (found == b->guest_images.end()) return SE_GPU_NOT_FOUND;
    VkFormat expected = guest_format(format, number_type);
    return expected == VK_FORMAT_UNDEFINED || found->second.format == expected ? SE_GPU_OK : SE_GPU_NOT_FOUND;
}
se_gpu_result SE_GPU_CALL se_gpu_blit_guest_image(
    se_gpu_backend* b, uint64_t source_address, uint32_t source_width, uint32_t source_height,
    uint32_t source_format, uint64_t destination_address, uint32_t destination_width,
    uint32_t destination_height, uint32_t destination_format) {
    if (!b || !source_address || !destination_address || !source_width || !source_height ||
        !destination_width || !destination_height) return SE_GPU_INVALID_ARGUMENT;
    auto source = b->guest_images.find(source_address);
    if (source == b->guest_images.end()) return SE_GPU_NOT_FOUND;
    if (guest_format(source_format, 0) != VK_FORMAT_UNDEFINED &&
        source->second.format != guest_format(source_format, 0)) return SE_GPU_NOT_FOUND;
    guest_image* destination{};
    se_gpu_result created = cached_image(b, destination_address, destination_width, destination_height,
        destination_format, 0, destination_width, nullptr, &destination);
    if (created != SE_GPU_OK) return created;
    source = b->guest_images.find(source_address);
    return source == b->guest_images.end() ? SE_GPU_NOT_FOUND : blit_image(b, &source->second, destination);
}
se_gpu_result SE_GPU_CALL se_gpu_render_target_output_kind(
    uint32_t format, uint32_t number_type, uint32_t* output_kind) {
    if (!output_kind) return SE_GPU_INVALID_ARGUMENT;
    bool supported = false; uint32_t kind = 0;
    if ((format == 4 || format == 5 || format == 10 || format == 12) && number_type == 4) {
        supported = true; kind = 1;
    } else if ((format == 4 || format == 5 || format == 10 || format == 12) && number_type == 5) {
        supported = true; kind = 2;
    } else {
        switch (format) {
        case 1: case 3: case 4: case 5: case 7: case 9: case 10: case 12: case 13: case 14:
        case 22: case 29: case 36: case 56: case 62: case 64: case 71: case 75:
        case 0x30004: case 0x30005:
            supported = true; kind = 0; break;
        case 49: case 0x10004: case 0x10005: case 0x1000a: case 0x1000c:
            supported = true; kind = 1; break;
        case 0x20004: case 0x20005: case 0x2000a: case 0x2000c:
            supported = true; kind = 2; break;
        default: break;
        }
    }
    if (!supported) return SE_GPU_NOT_FOUND;
    *output_kind = kind; return SE_GPU_OK;
}
}
