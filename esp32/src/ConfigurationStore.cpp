#include "ConfigurationStore.h"
#include "ConfigJson.h"

namespace controller {

void ConfigurationStore::begin() {
    ready_ = preferences_.begin("fan-controller", false);
    if (!ready_) {
        state_ = "unavailable";
        Serial.println("ERROR: NVS unavailable; using defaults, save disabled");
        return;
    }
    if (!preferences_.isKey("config")) {
        Serial.println("NVS: first boot, using default profiles");
        return;
    }
    char stored[MaxStoredBytes + 1]{};
    if (preferences_.getString("config", stored, sizeof(stored)) == 0) {
        state_ = "invalid";
        Serial.println("ERROR: unreadable or oversized NVS configuration; using defaults (not overwriting NVS)");
        return;
    }
    JsonDocument document;
    const auto error = deserializeJson(document, stored);
    Config candidate{};
    if (error || !readConfig(document.as<JsonObjectConst>(), candidate)) {
        state_ = "invalid";
        Serial.println("ERROR: invalid NVS configuration; using defaults (not overwriting NVS)");
        return;
    }
    config_ = candidate;
    state_ = "loaded";
    Serial.println("NVS: configuration loaded");
}

bool ConfigurationStore::save() {
    if (!ready_) {
        Serial.println("ERROR: NVS unavailable; cannot save configuration");
        return false;
    }
    JsonDocument document;
    writeConfig(document.to<JsonObject>(), config_);
    String serialized;
    serializeJson(document, serialized);
    if (document.overflowed() || serialized.isEmpty() ||
        preferences_.putString("config", serialized) != serialized.length()) {
        state_ = "write_error";
        Serial.println("ERROR: NVS write failed");
        return false;
    }
    if (preferences_.getString("config") != serialized) {
        state_ = "verify_error";
        Serial.println("ERROR: NVS verification failed");
        return false;
    }
    dirty_ = false;
    state_ = "saved";
    return true;
}

void ConfigurationStore::replace(const Config& config) {
    config_ = config;
    dirty_ = true;
}

}  // namespace controller
