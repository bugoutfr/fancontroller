#include "CommandProcessor.h"
#include "ConfigJson.h"
#include "FirmwareVersion.h"
#include "HardwareConfig.h"

namespace controller {
namespace {

const char* faultName(SensorFault fault) {
    switch (fault) {
        case SensorFault::None: return "none";
        case SensorFault::ShortCircuit: return "short_circuit";
        case SensorFault::OpenCircuit: return "open_circuit";
        case SensorFault::OutOfRange: return "out_of_range";
    }
    return "out_of_range";
}

const char* bleHealthName(BleHealthState state) {
    switch (state) {
        case BleHealthState::Starting: return "starting";
        case BleHealthState::Ready: return "ready";
        case BleHealthState::QueueAllocationFailed: return "queue_allocation_failed";
        case BleHealthState::StackInitializationFailed: return "stack_initialization_failed";
        case BleHealthState::PairingStorageUnavailable: return "pairing_storage_unavailable";
        case BleHealthState::ResourceAllocationFailed: return "resource_allocation_failed";
        case BleHealthState::ServerStartFailed: return "server_start_failed";
        case BleHealthState::AdvertisingConfigurationFailed: return "advertising_configuration_failed";
        case BleHealthState::AdvertisingStartFailed: return "advertising_start_failed";
        case BleHealthState::AdvertisingRestartFailed: return "advertising_restart_failed";
    }
    return "unknown";
}

}  // namespace

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

OtaAcknowledgment CommandProcessor::processOtaPacket(
    const uint8_t* packet,
    size_t length,
    uint32_t now) {
    return ota_.processPacket(packet, length, now);
}

void CommandProcessor::updateOta(uint32_t now) {
    ota_.update(now);
}

void CommandProcessor::endOtaSession() {
    ota_.endSession();
}

bool CommandProcessor::otaActivationReplyPending() const {
    return ota_.activationReplyPending();
}

void CommandProcessor::otaActivationReplyDrained(uint32_t now) {
    ota_.activationReplyDrained(now);
}

bool CommandProcessor::otaRestartDue(uint32_t now) const {
    return ota_.restartDue(now);
}

void CommandProcessor::writeStatus(JsonObject data, const int8_t* rssi) const {
    data["firmware_version"] = FirmwareVersion;
    data["tach_diagnostics_supported"] = true;
    if (rssi != nullptr) {
        data["ble_rssi_dbm"] = *rssi;
    } else {
        data["ble_rssi_dbm"] = nullptr;
    }
    data["uptime_ms"] = millis();
    data["dirty"] = storage_.dirty();
    data["storage"] = storage_.state();
    data["storage_available"] = storage_.available();
    const BleHealthState bleState = bleHealth_.state.load();
    data["ble_available"] = bleState == BleHealthState::Ready;
    data["ble_status"] = bleHealthName(bleState);
    const ThermalStatus status = thermal_.status(storage_.config());
    JsonArray sensors = data["sensors"].to<JsonArray>();
    for (size_t sensor = 0; sensor < SensorCount; ++sensor) {
        const auto& state = status.sensors[sensor];
        JsonObject item = sensors.add<JsonObject>();
        item["sensor"] = sensor + 1;
        item["millivolts"] = state.millivolts;
        if (state.temperature.fault == SensorFault::None) {
            item["temperature"] = state.temperature.celsius;
        } else {
            item["temperature"] = nullptr;
        }
        item["fault"] = faultName(state.temperature.fault);
        item["safety"] = state.safety;
    }
    JsonArray fans = data["fans"].to<JsonArray>();
    for (size_t fan = 0; fan < FanCount; ++fan) {
        const auto& state = status.fans[fan];
        JsonObject item = fans.add<JsonObject>();
        item["fan"] = fan + 1;
        item["sensor"] = fan / 2 + 1;
        item["mode"] = state.automatic ? "auto" : "fixed";
        item["fixed_duty"] = state.fixedDuty;
        item["target_duty"] = state.requestedDuty;
        item["applied_duty"] = state.appliedDuty;
        item["rpm"] = state.rpm;
        item["pwm_ok"] = state.pwmReady;
        item["boost"] = state.boosting;
    }
}

String CommandProcessor::process(const String& command, const int8_t* rssi, bool otaSubscribed) {
    JsonDocument request;
    const auto error = deserializeJson(request, command);
    if (error || !request.is<JsonObject>() || !request["cmd"].is<const char*>()) {
        return errorResponse("invalid_json", "Expected a JSON object with a string cmd");
    }
    JsonDocument response;
    response["ok"] = true;
    const char* cmd = request["cmd"];
    response["cmd"] = cmd;
    if (strcmp(cmd, "ota_info") == 0 || strcmp(cmd, "ota_begin") == 0 ||
        strcmp(cmd, "ota_finish") == 0 || strcmp(cmd, "ota_activate") == 0 ||
        strcmp(cmd, "ota_abort") == 0) {
        if (!otaSubscribed) {
            return errorResponse("ota_subscription_required", "Subscribe to the OTA characteristic notifications before OTA commands");
        }
        JsonObject data = response["data"].to<JsonObject>();
        if (strcmp(cmd, "ota_info") == 0) {
            uint32_t maxSize = 0;
            char runningSha256[65];
            const OtaResult result = ota_.info(maxSize, runningSha256);
            if (!result.ok) {
                return errorResponse(result.code, result.message);
            }
            data["protocol"] = 1;
            data["max_size"] = maxSize;
            data["running_image_sha256"] = runningSha256;
        } else if (strcmp(cmd, "ota_begin") == 0) {
            if (!request["size"].is<uint32_t>() || request["size"].as<uint32_t>() == 0 ||
                !request["sha256"].is<const char*>()) {
                return errorResponse("invalid_ota_request", "Expected a positive size and a 64-character SHA-256 hex digest");
            }
            const JsonString sha256 = request["sha256"].as<JsonString>();
            uint8_t digest[32];
            if (sha256.size() != 64 || !parseSha256Hex(sha256.c_str(), digest)) {
                return errorResponse("invalid_ota_request", "Expected a positive size and a 64-character SHA-256 hex digest");
            }
            const OtaResult result = ota_.begin(
                request["size"].as<uint32_t>(), sha256.c_str(), millis());
            if (!result.ok) {
                return errorResponse(result.code, result.message);
            }
            data["offset"] = 0;
        } else if (strcmp(cmd, "ota_finish") == 0) {
            uint32_t size = 0;
            char sha256[65];
            const OtaResult result = ota_.finish(size, sha256);
            if (!result.ok) {
                return errorResponse(result.code, result.message);
            }
            data["size"] = size;
            data["sha256"] = sha256;
        } else if (strcmp(cmd, "ota_activate") == 0) {
            const OtaResult result = ota_.activate();
            if (!result.ok) {
                return errorResponse(result.code, result.message);
            }
            data["scheduled"] = true;
        } else {
            const OtaResult result = ota_.abort();
            if (!result.ok) {
                return errorResponse(result.code, result.message);
            }
            data["aborted"] = true;
        }
    } else if (ota_.state() != OtaState::Idle &&
               (strcmp(cmd, "set_profile") == 0 || strcmp(cmd, "set_fan") == 0 ||
                strcmp(cmd, "save") == 0 || strcmp(cmd, "defaults") == 0)) {
        return errorResponse("ota_busy", "Configuration changes are disabled during an OTA transfer or staged image");
    } else if (strcmp(cmd, "get_config") == 0) {
        writeConfig(response["data"].to<JsonObject>(), storage_.config());
        response["storage"] = storage_.state();
    } else if (strcmp(cmd, "get_status") == 0) {
        writeStatus(response["data"].to<JsonObject>(), rssi);
    } else if (strcmp(cmd, "get_tach_diagnostics") == 0) {
        if (!request["after"].isUnbound() && !request["after"].is<uint64_t>()) {
            return errorResponse("invalid_tach_cursor", "after must be an unsigned sequence number");
        }
        JsonObject data = response["data"].to<JsonObject>();
        data["latest_sequence"] = thermal_.tachSequence();
        bool dropped = false;
        const auto* snapshot = !request["after"].isUnbound()
            ? thermal_.nextTachSnapshot(request["after"].as<uint64_t>(), dropped) : nullptr;
        data["dropped"] = dropped;
        if (snapshot == nullptr) {
            data["window"] = nullptr;
        } else {
            JsonObject window = data["window"].to<JsonObject>();
            window["sequence"] = snapshot->sequence;
            window["uptime_ms"] = snapshot->uptimeMs;
            JsonArray fans = window["fans"].to<JsonArray>();
            for (size_t fan = 0; fan < FanCount; ++fan) {
                const auto& reading = snapshot->fans[fan];
                const auto& edges = reading.edges;
                const uint8_t ppr = hardware::PulsesPerRevolution[fan];
                JsonObject item = fans.add<JsonObject>();
                item["fan"] = fan + 1;
                item["window_us"] = reading.windowUs;
                item["pulses"] = edges.pulses;
                item["intervals"] = edges.intervals;
                item["min_us"] = edges.intervals == 0 ? 0 : edges.minUs;
                item["max_us"] = edges.maxUs;
                item["span_us"] = edges.spanUs;
                item["rpm_count"] = nullptr;
                item["rpm_period"] = nullptr;
                if (reading.windowUs != 0 && ppr != 0) {
                    item["rpm_count"] = edges.pulses * 60000000.0 / reading.windowUs / ppr;
                }
                if (edges.intervals != 0 && edges.spanUs != 0 && ppr != 0) {
                    item["rpm_period"] = edges.intervals * 60000000.0 / edges.spanUs / ppr;
                }
                item["rpm_display"] = reading.displayRpm;
                item["pwm_pct"] = reading.duty;
                item["rpm_raw_display"] = reading.rawDisplayRpm;
                item["corrected_intervals"] = reading.corrected.intervals;
                item["corrected_span_us"] = reading.corrected.spanUs;
                item["merged_pairs"] = reading.corrected.mergedPairs;
                item["rpm_corrected"] = nullptr;
                if (reading.corrected.spanUs != 0 && ppr != 0) {
                    item["rpm_corrected"] = reading.corrected.intervals * 60000000.0 /
                                            reading.corrected.spanUs / ppr;
                }
                if (reading.trace.count == ThermalController::TachTraceSize) {
                    item["trace_trigger_us"] = reading.trace.triggerUs;
                    item["trace_reference_us"] = reading.trace.referenceUs;
                    JsonArray intervals = item["trace_us"].to<JsonArray>();
                    for (const uint32_t interval : reading.trace.intervalsUs) {
                        intervals.add(interval);
                    }
                }
            }
        }
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
        return errorResponse("unknown_command", "Use get_config, get_status, get_tach_diagnostics, set_profile, set_fan, save or defaults");
    }
    response["dirty"] = storage_.dirty();
    return encode(response);
}

}  // namespace controller
