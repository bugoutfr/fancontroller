#pragma once

#include <ArduinoJson.h>
#include <cstring>
#include "Controller.h"

namespace controller {

inline void writeProfile(JsonArray array, const Profile& profile) {
    for (size_t i = 0; i < profile.count; ++i) {
        JsonObject point = array.add<JsonObject>();
        point["temperature"] = profile.points[i].temperature;
        point["duty"] = profile.points[i].duty;
    }
}

inline bool readProfile(JsonVariantConst value, Profile& output) {
    if (!value.is<JsonArrayConst>()) {
        return false;
    }
    const JsonArrayConst array = value.as<JsonArrayConst>();
    if (array.size() < 2 || array.size() > MaxPoints) {
        return false;
    }
    Profile candidate{};
    candidate.count = array.size();
    size_t i = 0;
    for (JsonVariantConst item : array) {
        if (!item["temperature"].is<float>() || !item["duty"].is<float>()) {
            return false;
        }
        candidate.points[i++] = {item["temperature"].as<float>(), item["duty"].as<float>()};
    }
    if (!validProfile(candidate)) {
        return false;
    }
    output = candidate;
    return true;
}

inline void writeConfig(JsonObject object, const Config& config) {
    object["version"] = 1;
    JsonArray profiles = object["profiles"].to<JsonArray>();
    for (const auto& profile : config.profiles) {
        writeProfile(profiles.add<JsonArray>(), profile);
    }
    JsonArray fans = object["fans"].to<JsonArray>();
    for (const auto& fan : config.fans) {
        JsonObject item = fans.add<JsonObject>();
        item["mode"] = fan.automatic ? "auto" : "fixed";
        item["duty"] = fan.duty;
    }
}

inline bool readConfig(JsonObjectConst object, Config& output) {
    if (!object["version"].is<int>() || object["version"].as<int>() != 1 ||
        !object["profiles"].is<JsonArrayConst>() || !object["fans"].is<JsonArrayConst>()) {
        return false;
    }
    const JsonArrayConst profiles = object["profiles"].as<JsonArrayConst>();
    const JsonArrayConst fans = object["fans"].as<JsonArrayConst>();
    if (profiles.size() != SensorCount || fans.size() != FanCount) {
        return false;
    }
    Config candidate{};
    for (size_t i = 0; i < SensorCount; ++i) {
        if (!readProfile(profiles[i], candidate.profiles[i])) {
            return false;
        }
    }
    for (size_t i = 0; i < FanCount; ++i) {
        if (!fans[i]["mode"].is<const char*>() || !fans[i]["duty"].is<float>()) {
            return false;
        }
        const char* mode = fans[i]["mode"];
        if (strcmp(mode, "auto") != 0 && strcmp(mode, "fixed") != 0) {
            return false;
        }
        candidate.fans[i] = {strcmp(mode, "auto") == 0, fans[i]["duty"].as<float>()};
    }
    if (!validConfig(candidate)) {
        return false;
    }
    output = candidate;
    return true;
}

}  // namespace controller
