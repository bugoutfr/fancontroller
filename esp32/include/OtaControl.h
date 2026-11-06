#pragma once

#include <cstddef>
#include <cstdint>

#include "OtaProtocol.h"

namespace controller {

enum class OtaState : uint8_t {
    Idle,
    Receiving,
    Staged
};

struct OtaResult {
    bool ok;
    const char* code;
    const char* message;
};

struct OtaAcknowledgment {
    OtaPacketStatus status;
    uint32_t acceptedOffset;
};

class OtaControl {
public:
    virtual ~OtaControl() = default;
    virtual OtaResult info(uint32_t& maxSize, char runningImageSha256[65]) const = 0;
    virtual OtaResult begin(uint32_t size, const char* sha256, uint32_t now) = 0;
    virtual OtaResult finish(uint32_t& size, char verifiedSha256[65]) = 0;
    virtual OtaResult activate() = 0;
    virtual OtaResult abort() = 0;
    virtual OtaAcknowledgment processPacket(const uint8_t* packet, size_t length, uint32_t now) = 0;
    virtual void update(uint32_t now) = 0;
    virtual void endSession() = 0;
    virtual bool activationReplyPending() const = 0;
    virtual void activationReplyDrained(uint32_t now) = 0;
    virtual bool restartDue(uint32_t now) const = 0;
    virtual OtaState state() const = 0;
};

}  // namespace controller
