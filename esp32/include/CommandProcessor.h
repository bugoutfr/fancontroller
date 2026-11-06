#pragma once

#include <Arduino.h>
#include <ArduinoJson.h>
#include "BleHealth.h"
#include "ConfigurationStore.h"
#include "OtaControl.h"
#include "ThermalController.h"

namespace controller {

class CommandProcessor {
public:
    CommandProcessor(
        ConfigurationStore& storage,
        const ThermalController& thermal,
        const BleHealth& bleHealth,
        OtaControl& ota)
        : storage_(storage), thermal_(thermal), bleHealth_(bleHealth), ota_(ota) {}

    String process(const String& command, const int8_t* rssi = nullptr, bool otaSubscribed = false);
    static String errorResponse(const char* code, const char* message);
    OtaAcknowledgment processOtaPacket(const uint8_t* packet, size_t length, uint32_t now);
    void updateOta(uint32_t now);
    void endOtaSession();
    bool otaActivationReplyPending() const;
    void otaActivationReplyDrained(uint32_t now);
    bool otaRestartDue(uint32_t now) const;

private:
    static String encode(const JsonDocument& document);
    static bool readIndex(JsonVariantConst value, size_t maximum, size_t& index);
    void writeStatus(JsonObject data, const int8_t* rssi) const;

    ConfigurationStore& storage_;
    const ThermalController& thermal_;
    const BleHealth& bleHealth_;
    OtaControl& ota_;
};

}  // namespace controller
