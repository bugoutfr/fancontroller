#pragma once

#include <cstddef>
#include <cstdint>

namespace controller {

constexpr size_t OtaPacketHeaderSize = 4;
constexpr size_t OtaPacketMaxSize = 512;
constexpr size_t OtaPacketMaxPayload = OtaPacketMaxSize - OtaPacketHeaderSize;

enum class OtaPacketStatus : uint8_t {
    Ok = 0,
    NotReceiving = 1,
    InvalidLength = 2,
    WrongOffset = 3,
    ImageSizeExceeded = 4,
    FlashWriteFailed = 5
};

inline bool parseSha256Hex(const char* text, uint8_t digest[32]) {
    if (text == nullptr) {
        return false;
    }
    for (size_t i = 0; i < 32; ++i) {
        const char high = text[i * 2];
        const char low = text[i * 2 + 1];
        const auto nibble = [](char value) -> int {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        };
        const int hi = nibble(high);
        const int lo = nibble(low);
        if (hi < 0 || lo < 0) {
            return false;
        }
        digest[i] = static_cast<uint8_t>((hi << 4) | lo);
    }
    return text[64] == '\0';
}

inline void formatSha256Hex(const uint8_t digest[32], char text[65]) {
    constexpr char Hex[] = "0123456789abcdef";
    for (size_t i = 0; i < 32; ++i) {
        text[i * 2] = Hex[digest[i] >> 4];
        text[i * 2 + 1] = Hex[digest[i] & 0x0f];
    }
    text[64] = '\0';
}

inline OtaPacketStatus validateOtaPacket(
    const uint8_t* packet,
    size_t packetLength,
    uint32_t acceptedOffset,
    uint32_t expectedSize,
    uint32_t& nextOffset) {
    if (packet == nullptr || packetLength <= OtaPacketHeaderSize ||
        packetLength > OtaPacketMaxSize) {
        return OtaPacketStatus::InvalidLength;
    }
    const uint32_t offset = static_cast<uint32_t>(packet[0]) |
        (static_cast<uint32_t>(packet[1]) << 8) |
        (static_cast<uint32_t>(packet[2]) << 16) |
        (static_cast<uint32_t>(packet[3]) << 24);
    if (offset != acceptedOffset) {
        return OtaPacketStatus::WrongOffset;
    }
    const size_t payloadLength = packetLength - OtaPacketHeaderSize;
    if (payloadLength > OtaPacketMaxPayload ||
        acceptedOffset > expectedSize ||
        payloadLength > expectedSize - acceptedOffset) {
        return OtaPacketStatus::ImageSizeExceeded;
    }
    nextOffset = acceptedOffset + static_cast<uint32_t>(payloadLength);
    return OtaPacketStatus::Ok;
}

}  // namespace controller
