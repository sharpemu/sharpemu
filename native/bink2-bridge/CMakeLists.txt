# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

cmake_minimum_required(VERSION 3.21)

if(NOT DEFINED SHARPEMU_TARGET_RID)
    message(FATAL_ERROR "SHARPEMU_TARGET_RID is required")
endif()

if(SHARPEMU_TARGET_RID STREQUAL "osx-x64")
    set(CMAKE_OSX_ARCHITECTURES x86_64 CACHE STRING "" FORCE)
elseif(SHARPEMU_TARGET_RID STREQUAL "osx-arm64")
    set(CMAKE_OSX_ARCHITECTURES arm64 CACHE STRING "" FORCE)
endif()

project(sharpemu_bink2_bridge LANGUAGES C)

set(SHARPEMU_FFMPEG_TAG "6a6861e")
set(SHARPEMU_FFMPEG_COMMIT "6a6861e357a263edee51d2f4894941f50aed59f5")
# Keyed by tag so bumping SHARPEMU_FFMPEG_TAG always fetches fresh instead of
# silently reusing a previous tag's cached download.
set(SHARPEMU_FFMPEG_ROOT "${CMAKE_BINARY_DIR}/ffmpeg-core-${SHARPEMU_FFMPEG_TAG}")

if(SHARPEMU_TARGET_RID STREQUAL "win-x64")
    set(SHARPEMU_FFMPEG_PACKAGE "ffmpeg-windows-x64.zip")
elseif(SHARPEMU_TARGET_RID STREQUAL "linux-x64")
    set(SHARPEMU_FFMPEG_PACKAGE "ffmpeg-linux-x64.zip")
elseif(SHARPEMU_TARGET_RID STREQUAL "osx-x64")
    set(SHARPEMU_FFMPEG_PACKAGE "ffmpeg-macos-x64.zip")
elseif(SHARPEMU_TARGET_RID STREQUAL "osx-arm64")
    set(SHARPEMU_FFMPEG_PACKAGE "ffmpeg-macos-arm64.zip")
else()
    message(FATAL_ERROR "Unsupported Bink2 bridge RID: ${SHARPEMU_TARGET_RID}")
endif()

set(SHARPEMU_FFMPEG_ARCHIVE "${SHARPEMU_FFMPEG_ROOT}/${SHARPEMU_FFMPEG_PACKAGE}")
set(SHARPEMU_FFMPEG_LIB_DIR "${SHARPEMU_FFMPEG_ROOT}/lib")
set(SHARPEMU_FFMPEG_SOURCE_ARCHIVE "${SHARPEMU_FFMPEG_ROOT}/source.tar.gz")
set(SHARPEMU_FFMPEG_SOURCE_DIR
    "${SHARPEMU_FFMPEG_ROOT}/source/ffmpeg-core-${SHARPEMU_FFMPEG_COMMIT}")

if(NOT EXISTS "${SHARPEMU_FFMPEG_ARCHIVE}")
    file(MAKE_DIRECTORY "${SHARPEMU_FFMPEG_ROOT}")
    file(DOWNLOAD
        "https://github.com/sharpemu/ffmpeg-core/releases/download/${SHARPEMU_FFMPEG_TAG}/${SHARPEMU_FFMPEG_PACKAGE}"
        "${SHARPEMU_FFMPEG_ARCHIVE}"
        SHOW_PROGRESS
        STATUS SHARPEMU_DOWNLOAD_STATUS)
    list(GET SHARPEMU_DOWNLOAD_STATUS 0 SHARPEMU_DOWNLOAD_CODE)
    if(NOT SHARPEMU_DOWNLOAD_CODE EQUAL 0)
        message(FATAL_ERROR "Failed to download ${SHARPEMU_FFMPEG_PACKAGE}: ${SHARPEMU_DOWNLOAD_STATUS}")
    endif()
endif()

if(NOT EXISTS "${SHARPEMU_FFMPEG_LIB_DIR}")
    file(MAKE_DIRECTORY "${SHARPEMU_FFMPEG_LIB_DIR}")
    file(ARCHIVE_EXTRACT
        INPUT "${SHARPEMU_FFMPEG_ARCHIVE}"
        DESTINATION "${SHARPEMU_FFMPEG_LIB_DIR}")
endif()

if(NOT EXISTS "${SHARPEMU_FFMPEG_SOURCE_DIR}/include/libavcodec/avcodec.h")
    file(MAKE_DIRECTORY "${SHARPEMU_FFMPEG_ROOT}/source")
    file(DOWNLOAD
        "https://github.com/sharpemu/ffmpeg-core/archive/${SHARPEMU_FFMPEG_COMMIT}.tar.gz"
        "${SHARPEMU_FFMPEG_SOURCE_ARCHIVE}"
        SHOW_PROGRESS
        STATUS SHARPEMU_SOURCE_STATUS)
    list(GET SHARPEMU_SOURCE_STATUS 0 SHARPEMU_SOURCE_CODE)
    if(NOT SHARPEMU_SOURCE_CODE EQUAL 0)
        message(FATAL_ERROR "Failed to download FFmpeg headers: ${SHARPEMU_SOURCE_STATUS}")
    endif()
    file(ARCHIVE_EXTRACT
        INPUT "${SHARPEMU_FFMPEG_SOURCE_ARCHIVE}"
        DESTINATION "${SHARPEMU_FFMPEG_ROOT}/source")
endif()

add_library(sharpemu_bink2_bridge SHARED sharpemu_bink2_bridge.c)
target_compile_features(sharpemu_bink2_bridge PRIVATE c_std_11)
target_include_directories(sharpemu_bink2_bridge PRIVATE
    "${SHARPEMU_FFMPEG_SOURCE_DIR}/include")

function(sharpemu_link_ffmpeg_library target library_name)
    # find_library caches absolute paths; discard paths from an older FFmpeg
    # tag when a build directory is reused after a package update.
    unset(SHARPEMU_${library_name}_LIBRARY CACHE)
    find_library(SHARPEMU_${library_name}_LIBRARY
        NAMES "${library_name}" "lib${library_name}"
        PATHS "${SHARPEMU_FFMPEG_LIB_DIR}"
        NO_DEFAULT_PATH
        REQUIRED)
    target_link_libraries(${target} PRIVATE "${SHARPEMU_${library_name}_LIBRARY}")
endfunction()

sharpemu_link_ffmpeg_library(sharpemu_bink2_bridge avformat)
sharpemu_link_ffmpeg_library(sharpemu_bink2_bridge avcodec)
sharpemu_link_ffmpeg_library(sharpemu_bink2_bridge swscale)
sharpemu_link_ffmpeg_library(sharpemu_bink2_bridge avutil)

if(WIN32)
    set_property(TARGET sharpemu_bink2_bridge PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    target_link_libraries(sharpemu_bink2_bridge PRIVATE
        bcrypt ole32 oleaut32 psapi shlwapi strmiids user32 uuid ws2_32)
elseif(APPLE)
    target_link_libraries(sharpemu_bink2_bridge PRIVATE
        "-framework AppKit"
        "-framework AudioToolbox"
        "-framework CoreAudio"
        "-framework CoreFoundation"
        "-framework CoreMedia"
        "-framework CoreServices"
        "-framework CoreVideo"
        "-framework Security"
        "-framework VideoToolbox")
else()
    target_link_libraries(sharpemu_bink2_bridge PRIVATE dl m pthread)
endif()

set_target_properties(sharpemu_bink2_bridge PROPERTIES
    C_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out")

foreach(configuration Debug Release RelWithDebInfo MinSizeRel)
    string(TOUPPER "${configuration}" configuration_upper)
    set_target_properties(sharpemu_bink2_bridge PROPERTIES
        LIBRARY_OUTPUT_DIRECTORY_${configuration_upper} "${CMAKE_BINARY_DIR}/out"
        RUNTIME_OUTPUT_DIRECTORY_${configuration_upper} "${CMAKE_BINARY_DIR}/out")
endforeach()
