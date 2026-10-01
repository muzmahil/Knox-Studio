// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

#include "AudioFile.h"

#define DR_WAV_IMPLEMENTATION
#define DR_FLAC_IMPLEMENTATION
#define DR_MP3_IMPLEMENTATION
#include "dr_wav.h"
#include "dr_flac.h"
#include "dr_mp3.h"

#include <algorithm>
#include <cctype>
#include <fstream>
#include <filesystem>
#include <vector>

namespace nota {

namespace {

std::string extLower(const std::string& path) {
    auto dot = path.find_last_of('.');
    if (dot == std::string::npos) return {};
    std::string ext = path.substr(dot + 1);
    std::transform(ext.begin(), ext.end(), ext.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return ext;
}

std::vector<uint8_t> readBinaryFile(const std::string& path) {
    try {
#if defined(_WIN32)
        std::filesystem::path p = std::filesystem::u8path(path);
#else
        std::filesystem::path p(path);
#endif
        std::ifstream file(p, std::ios::binary | std::ios::ate);
        if (!file.is_open()) return {};
        auto size = file.tellg();
        if (size <= 0) return {};
        std::vector<uint8_t> buffer(static_cast<size_t>(size));
        file.seekg(0, std::ios::beg);
        if (file.read(reinterpret_cast<char*>(buffer.data()), size)) {
            return buffer;
        }
    } catch (...) {
    }
    return {};
}

std::shared_ptr<SampleBuffer> decodeWav(const std::vector<uint8_t>& bytes) {
    if (bytes.empty()) return nullptr;
    unsigned int channels = 0, sampleRate = 0;
    drwav_uint64 frameCount = 0;
    float* data = drwav_open_memory_and_read_pcm_frames_f32(
        bytes.data(), bytes.size(), &channels, &sampleRate, &frameCount, nullptr);
    if (!data) return nullptr;
    auto buf = std::make_shared<SampleBuffer>();
    buf->channels = static_cast<int32_t>(channels);
    buf->frames = static_cast<int64_t>(frameCount);
    buf->sourceSampleRate = sampleRate;
    buf->samples.assign(data, data + frameCount * channels);
    drwav_free(data, nullptr);
    return buf;
}

std::shared_ptr<SampleBuffer> decodeFlac(const std::vector<uint8_t>& bytes) {
    if (bytes.empty()) return nullptr;
    unsigned int channels = 0, sampleRate = 0;
    drflac_uint64 frameCount = 0;
    float* data = drflac_open_memory_and_read_pcm_frames_f32(
        bytes.data(), bytes.size(), &channels, &sampleRate, &frameCount, nullptr);
    if (!data) return nullptr;
    auto buf = std::make_shared<SampleBuffer>();
    buf->channels = static_cast<int32_t>(channels);
    buf->frames = static_cast<int64_t>(frameCount);
    buf->sourceSampleRate = sampleRate;
    buf->samples.assign(data, data + frameCount * channels);
    drflac_free(data, nullptr);
    return buf;
}

std::shared_ptr<SampleBuffer> decodeMp3(const std::vector<uint8_t>& bytes) {
    if (bytes.empty()) return nullptr;
    drmp3_config cfg{};
    drmp3_uint64 frameCount = 0;
    float* data = drmp3_open_memory_and_read_pcm_frames_f32(
        bytes.data(), bytes.size(), &cfg, &frameCount, nullptr);
    if (!data) return nullptr;
    auto buf = std::make_shared<SampleBuffer>();
    buf->channels = static_cast<int32_t>(cfg.channels);
    buf->frames = static_cast<int64_t>(frameCount);
    buf->sourceSampleRate = cfg.sampleRate;
    buf->samples.assign(data, data + frameCount * cfg.channels);
    drmp3_free(data, nullptr);
    return buf;
}

} // namespace

std::shared_ptr<SampleBuffer> decodeAudioFile(const std::string& path) {
    const std::string ext = extLower(path);
    auto bytes = readBinaryFile(path);
    if (bytes.empty()) return nullptr;

    if (ext == "wav" || ext == "wave")  return decodeWav(bytes);
    if (ext == "flac") return decodeFlac(bytes);
    if (ext == "mp3")  return decodeMp3(bytes);
    return nullptr;
}

} // namespace nota
