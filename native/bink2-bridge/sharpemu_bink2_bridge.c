/*
 * Copyright (C) 2026 SharpEmu Emulator Project
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/error.h>
#include <libswscale/swscale.h>

#if defined(_WIN32)
#define SHARPEMU_EXPORT __declspec(dllexport)
#else
#define SHARPEMU_EXPORT __attribute__((visibility("default")))
#endif

typedef struct sharpemu_bink2_info {
    uint32_t width;
    uint32_t height;
    uint32_t frames_per_second_numerator;
    uint32_t frames_per_second_denominator;
} sharpemu_bink2_info;

typedef struct sharpemu_bink2_movie {
    AVFormatContext *format;
    AVCodecContext *codec;
    struct SwsContext *converter;
    AVFrame *frame;
    AVPacket *packet;
    int video_stream;
    uint32_t output_width;
    uint32_t output_height;
    int draining;
} sharpemu_bink2_movie;

static void sharpemu_bink2_log_error(const char *operation, int error) {
    char message[AV_ERROR_MAX_STRING_SIZE];
    if (av_strerror(error, message, sizeof(message)) < 0) {
        snprintf(message, sizeof(message), "FFmpeg error %d", error);
    }
    fprintf(stderr, "[BINK2][ERROR] %s: %s\n", operation, message);
}

static void sharpemu_bink2_destroy(sharpemu_bink2_movie *movie) {
    if (!movie) {
        return;
    }

    sws_freeContext(movie->converter);
    av_packet_free(&movie->packet);
    av_frame_free(&movie->frame);
    avcodec_free_context(&movie->codec);
    avformat_close_input(&movie->format);
    free(movie);
}

static AVRational sharpemu_bink2_frame_rate(AVFormatContext *format, AVStream *stream) {
    AVRational rate = av_guess_frame_rate(format, stream, NULL);
    if (rate.num <= 0 || rate.den <= 0) {
        rate = stream->avg_frame_rate;
    }
    if (rate.num <= 0 || rate.den <= 0) {
        rate = stream->r_frame_rate;
    }
    if (rate.num <= 0 || rate.den <= 0) {
        rate = (AVRational){30, 1};
    }
    return rate;
}

static int sharpemu_bink2_open_internal(
    const char *path,
    uint32_t maximum_width,
    uint32_t maximum_height,
    void **movie_out,
    sharpemu_bink2_info *info) {
    sharpemu_bink2_movie *movie;
    const AVCodec *decoder = NULL;
    AVStream *stream;
    AVRational frame_rate;
    int result;

    if (!path || !movie_out || !info) {
        return 0;
    }

    *movie_out = NULL;
    movie = (sharpemu_bink2_movie *)calloc(1, sizeof(*movie));
    if (!movie) {
        return 0;
    }

    result = avformat_open_input(&movie->format, path, NULL, NULL);
    if (result < 0) {
        sharpemu_bink2_log_error("open", result);
        sharpemu_bink2_destroy(movie);
        return 0;
    }

    result = avformat_find_stream_info(movie->format, NULL);
    if (result < 0) {
        sharpemu_bink2_log_error("stream info", result);
        sharpemu_bink2_destroy(movie);
        return 0;
    }

    result = av_find_best_stream(
        movie->format, AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
    if (result < 0 || !decoder) {
        sharpemu_bink2_log_error("video stream", result);
        sharpemu_bink2_destroy(movie);
        return 0;
    }

    movie->video_stream = result;
    stream = movie->format->streams[movie->video_stream];
    movie->codec = avcodec_alloc_context3(decoder);
    if (!movie->codec) {
        sharpemu_bink2_destroy(movie);
        return 0;
    }

    result = avcodec_parameters_to_context(movie->codec, stream->codecpar);
    if (result < 0) {
        sharpemu_bink2_log_error("codec parameters", result);
        sharpemu_bink2_destroy(movie);
        return 0;
    }

    movie->codec->thread_count = 0;
    movie->codec->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
    result = avcodec_open2(movie->codec, decoder, NULL);
    if (result < 0) {
        sharpemu_bink2_log_error("codec open", result);
        sharpemu_bink2_destroy(movie);
        return 0;
    }

    movie->frame = av_frame_alloc();
    movie->packet = av_packet_alloc();
    if (!movie->frame || !movie->packet ||
        movie->codec->width <= 0 || movie->codec->height <= 0) {
        sharpemu_bink2_destroy(movie);
        return 0;
    }

    frame_rate = sharpemu_bink2_frame_rate(movie->format, stream);
    movie->output_width = (uint32_t)movie->codec->width;
    movie->output_height = (uint32_t)movie->codec->height;
    if (maximum_width > 0 && maximum_height > 0 &&
        (movie->output_width > maximum_width ||
         movie->output_height > maximum_height)) {
        if ((uint64_t)movie->output_width * maximum_height >
            (uint64_t)movie->output_height * maximum_width) {
            movie->output_height = (uint32_t)((uint64_t)movie->output_height *
                                               maximum_width /
                                               movie->output_width);
            movie->output_width = maximum_width;
        } else {
            movie->output_width = (uint32_t)((uint64_t)movie->output_width *
                                              maximum_height /
                                              movie->output_height);
            movie->output_height = maximum_height;
        }
        if (movie->output_width == 0) {
            movie->output_width = 1;
        }
        if (movie->output_height == 0) {
            movie->output_height = 1;
        }
    }

    info->width = movie->output_width;
    info->height = movie->output_height;
    info->frames_per_second_numerator = (uint32_t)frame_rate.num;
    info->frames_per_second_denominator = (uint32_t)frame_rate.den;
    *movie_out = movie;
    return 1;
}

SHARPEMU_EXPORT int sharpemu_bink2_open_utf8(
    const char *path,
    void **movie_out,
    sharpemu_bink2_info *info) {
    return sharpemu_bink2_open_internal(path, 0, 0, movie_out, info);
}

SHARPEMU_EXPORT int sharpemu_bink2_open_scaled_utf8(
    const char *path,
    uint32_t maximum_width,
    uint32_t maximum_height,
    void **movie_out,
    sharpemu_bink2_info *info) {
    return sharpemu_bink2_open_internal(
        path, maximum_width, maximum_height, movie_out, info);
}

static int sharpemu_bink2_receive_frame(sharpemu_bink2_movie *movie) {
    int result;

    for (;;) {
        result = avcodec_receive_frame(movie->codec, movie->frame);
        if (result >= 0) {
            return 1;
        }
        if (result == AVERROR_EOF) {
            return 0;
        }
        if (result != AVERROR(EAGAIN)) {
            sharpemu_bink2_log_error("decode", result);
            return 0;
        }
        if (movie->draining) {
            return 0;
        }

        for (;;) {
            result = av_read_frame(movie->format, movie->packet);
            if (result < 0) {
                movie->draining = 1;
                result = avcodec_send_packet(movie->codec, NULL);
                if (result < 0 && result != AVERROR_EOF) {
                    sharpemu_bink2_log_error("decoder drain", result);
                    return 0;
                }
                break;
            }

            if (movie->packet->stream_index != movie->video_stream) {
                av_packet_unref(movie->packet);
                continue;
            }

            result = avcodec_send_packet(movie->codec, movie->packet);
            av_packet_unref(movie->packet);
            if (result < 0 && result != AVERROR(EAGAIN)) {
                sharpemu_bink2_log_error("packet submit", result);
                return 0;
            }
            break;
        }
    }
}

SHARPEMU_EXPORT int sharpemu_bink2_decode_next_bgra(
    void *handle,
    uint8_t *destination,
    uint32_t stride,
    uint32_t destination_bytes) {
    sharpemu_bink2_movie *movie = (sharpemu_bink2_movie *)handle;
    uint8_t *destination_planes[4] = {destination, NULL, NULL, NULL};
    int destination_strides[4] = {(int)stride, 0, 0, 0};
    uint64_t required_bytes;
    int converted_rows;

    if (!movie || !destination || stride < movie->output_width * 4) {
        return 0;
    }

    required_bytes = (uint64_t)stride * movie->output_height;
    if (required_bytes > destination_bytes || !sharpemu_bink2_receive_frame(movie)) {
        return 0;
    }

    movie->converter = sws_getCachedContext(
        movie->converter,
        movie->frame->width,
        movie->frame->height,
        (enum AVPixelFormat)movie->frame->format,
        (int)movie->output_width,
        (int)movie->output_height,
        AV_PIX_FMT_BGRA,
        SWS_FAST_BILINEAR,
        NULL,
        NULL,
        NULL);
    if (!movie->converter) {
        av_frame_unref(movie->frame);
        return 0;
    }

    converted_rows = sws_scale(
        movie->converter,
        (const uint8_t *const *)movie->frame->data,
        movie->frame->linesize,
        0,
        movie->frame->height,
        destination_planes,
        destination_strides);
    av_frame_unref(movie->frame);
    return converted_rows == (int)movie->output_height;
}

SHARPEMU_EXPORT void sharpemu_bink2_close(void *movie) {
    sharpemu_bink2_destroy((sharpemu_bink2_movie *)movie);
}
