#pragma once

#include "OtaControl.h"

namespace controller {

class FirmwareOta final : public OtaControl {
public:
    FirmwareOta();
    ~FirmwareOta() override;
    FirmwareOta(const FirmwareOta&) = delete;
    FirmwareOta& operator=(const FirmwareOta&) = delete;

    OtaResult info(uint32_t& maxSize, char runningImageSha256[65]) const override;
    OtaResult begin(uint32_t size, const char* sha256, uint32_t now) override;
    OtaResult finish(uint32_t& size, char verifiedSha256[65]) override;
    OtaResult activate() override;
    OtaResult abort() override;
    OtaAcknowledgment processPacket(const uint8_t* packet, size_t length, uint32_t now) override;
    void update(uint32_t now) override;
    void endSession() override;
    bool activationReplyPending() const override;
    void activationReplyDrained(uint32_t now) override;
    bool restartDue(uint32_t now) const override;
    OtaState state() const override;

private:
    struct Impl;
    Impl* impl_;
};

}  // namespace controller
