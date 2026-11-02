#pragma once

#include <Arduino.h>
#include <ArduinoJson.h>
#include "ConfigurationStore.h"
#include "ThermalController.h"

namespace controller {

class CommandProcessor {
public:
    CommandProcessor(ConfigurationStore& storage, const ThermalController& thermal)
        : storage_(storage), thermal_(thermal) {}

    String process(const String& command);
    static String errorResponse(const char* code, const char* message);

private:
    static String encode(const JsonDocument& document);
    static bool readIndex(JsonVariantConst value, size_t maximum, size_t& index);
    void writeStatus(JsonObject data) const;

    ConfigurationStore& storage_;
    const ThermalController& thermal_;
};

}  // namespace controller
