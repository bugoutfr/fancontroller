#include "CommandProcessor.h"
#include "ConfigJson.h"
#include "FirmwareVersion.h"

namespace controller {

String CommandProcessor::encode(const JsonDocument& document) {
    String output;
    serializeJson(document, output);
    output += '\n';
    return output;
}

String CommandProcessor::errorResponse(const char* code, const char* message) {
    Serial.printf("BLE error: %s: %s\n", code, message);
    JsonDocument response;
    response["ok"] = false;
    response["error"] = code;
    response["message"] = message;
    return encode(response);
}

bool CommandProcessor::readIndex(JsonVariantConst value, size_t maximum, size_t& index) {
    if (!value.is<unsigned int>()) {
        return false;
    }
    const unsigned int number = value.as<unsigned int>();
    if (number < 1 || number > maximum) {
        return false;
    }
    index = number - 1;
    return true;
}

void CommandProcessor::writeStatus(JsonObject data, const int8_t* rssi) const {
    data["firmware_version"] = FirmwareVersion;
    if (rssi != nullptr) {
        data["ble_rssi_dbm"] = *rssi;
    } else {
        data["ble_rssi_dbm"] = nullptr;
    }
    data["uptime_ms"] = millis();
    data["dirty"] = storage_.dirty();
    data["storage"] = storage_.state();
    data["storage_available"] = storage_.available();
    thermal_.writeStatus(data, storage_.config());
}

String CommandProcessor::process(const String& command, const int8_t* rssi) {
    JsonDocument request;
    const auto error = deserializeJson(request, command);
    if (error || !request.is<JsonObject>() || !request["cmd"].is<const char*>()) {
        return errorResponse("invalid_json", "Expected a JSON object with a string cmd");
    }
    JsonDocument response;
    response["ok"] = true;
    const char* cmd = request["cmd"];
    response["cmd"] = cmd;
    if (strcmp(cmd, "get_config") == 0) {
        writeConfig(response["data"].to<JsonObject>(), storage_.config());
        response["storage"] = storage_.state();
    } else if (strcmp(cmd, "get_status") == 0) {
        writeStatus(response["data"].to<JsonObject>(), rssi);
    } else if (strcmp(cmd, "set_profile") == 0) {
        size_t sensor;
        Profile profile{};
        if (!readIndex(request["sensor"], SensorCount, sensor) ||
            !readProfile(request["points"], profile)) {
            return errorResponse("invalid_profile", "sensor: 1..2; points: 2..8, increasing temperatures (-20..120), nondecreasing duty (0..100)");
        }
        Config candidate = storage_.config();
        candidate.profiles[sensor] = profile;
        storage_.replace(candidate);
    } else if (strcmp(cmd, "set_fan") == 0) {
        size_t fan;
        if (!readIndex(request["fan"], FanCount, fan) || !request["mode"].is<const char*>()) {
            return errorResponse("invalid_fan", "Expected fan: 1..4 and mode: auto or fixed");
        }
        Config candidate = storage_.config();
        const char* mode = request["mode"];
        if (strcmp(mode, "auto") == 0) {
            candidate.fans[fan].automatic = true;
        } else if (strcmp(mode, "fixed") == 0 && request["duty"].is<float>() &&
                   validDuty(request["duty"].as<float>())) {
            candidate.fans[fan] = {false, request["duty"].as<float>()};
        } else {
            return errorResponse("invalid_mode", "Expected mode: auto, or mode: fixed with duty: 0..100");
        }
        storage_.replace(candidate);
    } else if (strcmp(cmd, "save") == 0) {
        if (!storage_.save()) {
            return errorResponse("storage_error", "Configuration could not be saved and verified");
        }
    } else if (strcmp(cmd, "defaults") == 0) {
        storage_.replace(defaults());
    } else {
        return errorResponse("unknown_command", "Use get_config, get_status, set_profile, set_fan, save or defaults");
    }
    response["dirty"] = storage_.dirty();
    return encode(response);
}

}  // namespace controller
