#include <cassert>
#include <cstdio>
#include <limits>
#include <string>
#include "ConfigJson.h"

using namespace controller;

namespace {

bool near(float actual, float expected, float tolerance = 0.01f) {
    return std::fabs(actual - expected) <= tolerance;
}

float millivoltsAt(float celsius) {
    const float resistance = NtcOhms * std::exp(Beta * (1.0f / (celsius + 273.15f) - 1.0f / 298.15f));
    return SupplyMv * resistance / (DividerOhms + resistance);
}

void testNtc() {
    assert(near(temperatureFromMv(1650).celsius, 25));
    assert(temperatureFromMv(1650).fault == SensorFault::None);
    for (float temperature : {0.0f, 20.0f, 40.0f, 60.0f, 80.0f, 100.0f}) {
        const auto actual = temperatureFromMv(millivoltsAt(temperature));
        assert(actual.fault == SensorFault::None);
        assert(near(actual.celsius, temperature));
    }
    assert(temperatureFromMv(0).fault == SensorFault::ShortCircuit);
    assert(temperatureFromMv(50).fault == SensorFault::ShortCircuit);
    assert(temperatureFromMv(3050).fault == SensorFault::OpenCircuit);
    assert(temperatureFromMv(3300).fault == SensorFault::OpenCircuit);
    assert(temperatureFromMv(60).fault == SensorFault::OutOfRange);
    assert(temperatureFromMv(std::numeric_limits<float>::quiet_NaN()).fault == SensorFault::OutOfRange);
    assert(temperatureFromMv(std::numeric_limits<float>::infinity()).fault == SensorFault::OutOfRange);
}

void testProfiles() {
    Config config = defaults();
    assert(validConfig(config));
    const Profile profile = config.profiles[0];
    assert(near(interpolate(profile, -10), 30));
    assert(near(interpolate(profile, 25), 30));
    assert(near(interpolate(profile, 30), 35));
    assert(near(interpolate(profile, 40), 50));
    assert(near(interpolate(profile, 65), 100));
    assert(near(interpolate(profile, 90), 100));
    Profile invalid = profile;
    invalid.count = 1;
    assert(!validProfile(invalid));
    invalid.count = MaxPoints + 1;
    assert(!validProfile(invalid));
    invalid = profile;
    invalid.points[1].temperature = invalid.points[0].temperature;
    assert(!validProfile(invalid));
    invalid.points[1].temperature = 10;
    assert(!validProfile(invalid));
    invalid = profile;
    invalid.points[1].duty = 29;
    assert(!validProfile(invalid));
    invalid = profile;
    invalid.points[0].temperature = -21;
    assert(!validProfile(invalid));
    invalid = profile;
    invalid.points[4].temperature = 121;
    assert(!validProfile(invalid));
    invalid = profile;
    invalid.points[2].duty = std::numeric_limits<float>::quiet_NaN();
    assert(!validProfile(invalid));
    assert(!validDuty(-1));
    assert(!validDuty(101));
    assert(validDuty(0));
    assert(validDuty(100));
}

void testCalibration() {
    assert(near(temperatureFromMv(millivoltsAt(23.6f), -0.7f).celsius, 22.9f));
    assert(near(temperatureFromMv(1650, -0.7f).celsius, 24.3f));
    assert(temperatureFromMv(0, -0.7f).fault == SensorFault::ShortCircuit);
    assert(temperatureFromMv(3300, -0.7f).fault == SensorFault::OpenCircuit);
    assert(temperatureFromMv(60, -10.0f).fault == SensorFault::OutOfRange);
    assert(temperatureFromMv(1650, std::numeric_limits<float>::quiet_NaN()).fault == SensorFault::OutOfRange);
    assert(temperatureFromMv(1650, -100.0f).fault == SensorFault::OutOfRange);
    assert(safetyActive(temperatureFromMv(millivoltsAt(80.8f), -0.7f)));
    assert(!safetyActive(temperatureFromMv(millivoltsAt(80.0f), -0.7f)));
}

void testFanMappingAndSafety() {
    Config config = defaults();
    config.profiles[1] = {2, {{20, 10}, {60, 90}}};
    Temperature normal{40, SensorFault::None};
    assert(near(targetDuty(config, 0, normal), 50));
    assert(near(targetDuty(config, 1, normal), 50));
    normal.celsius = 30;
    assert(near(targetDuty(config, 2, normal), 30));
    assert(near(targetDuty(config, 3, normal), 30));
    for (size_t fan = 0; fan < FanCount; ++fan) {
        config.fans[fan] = {false, 17.5f};
        assert(near(targetDuty(config, fan, normal), 17.5f));
        assert(near(targetDuty(config, fan, {80, SensorFault::None}), 100));
        assert(near(targetDuty(config, fan, {95, SensorFault::None}), 100));
        for (auto fault : {SensorFault::ShortCircuit, SensorFault::OpenCircuit, SensorFault::OutOfRange}) {
            assert(near(targetDuty(config, fan, {0, fault}), 100));
        }
        config.fans[fan].automatic = true;
        assert(!near(targetDuty(config, fan, normal), 17.5f));
    }
    assert(!safetyActive({79.99f, SensorFault::None}));
    assert(safetyActive({80, SensorFault::None}));
    assert(near(rpmFromPulses(40, 1000, 2), 1200));
    assert(near(rpmFromPulses(60, 1500, 2), 1200));
    assert(near(rpmFromPulses(0, 1000, 2), 0));
    assert(near(rpmFromPulses(40, 0, 2), 0));
}

void testConfigurationRoundTrip() {
    Config original = defaults();
    original.fans[0] = {false, 42.5f};
    original.fans[3] = {false, 0};
    original.profiles[1] = {2, {{10, 0}, {70, 100}}};
    JsonDocument document;
    writeConfig(document.to<JsonObject>(), original);
    std::string serialized;
    serializeJson(document, serialized);
    JsonDocument restored;
    assert(!deserializeJson(restored, serialized));
    Config actual{};
    assert(readConfig(restored.as<JsonObjectConst>(), actual));
    assert(!actual.fans[0].automatic);
    assert(near(actual.fans[0].duty, 42.5f));
    assert(actual.fans[1].automatic);
    assert(!actual.fans[3].automatic);
    assert(near(actual.fans[3].duty, 0));
    assert(actual.profiles[1].count == 2);
    assert(near(actual.profiles[1].points[1].temperature, 70));
    assert(near(interpolate(actual.profiles[1], 40), 50));

    for (auto& profile : original.profiles) {
        profile.count = MaxPoints;
        for (size_t i = 0; i < MaxPoints; ++i) {
            profile.points[i] = {-19.1234f + float(i) * 17.1234f, float(i) * 13.1234f};
        }
    }
    document.clear();
    writeConfig(document.to<JsonObject>(), original);
    assert(measureJson(document) <= 1024);
    assert(readConfig(document.as<JsonObjectConst>(), actual));
    assert(actual.profiles[0].count == MaxPoints);
}

void testInvalidJsonConfiguration() {
    const char* invalid[] = {
        "{}",
        R"({"version":2,"profiles":[],"fans":[]})",
        R"({"version":1,"profiles":[],"fans":[]})",
        R"({"version":1,"profiles":[[],[]],"fans":[]})"
    };
    Config untouched = defaults();
    for (const char* json : invalid) {
        JsonDocument document;
        assert(!deserializeJson(document, json));
        assert(!readConfig(document.as<JsonObjectConst>(), untouched));
        assert(validConfig(untouched));
    }
    JsonDocument document;
    writeConfig(document.to<JsonObject>(), defaults());
    document["fans"][2]["duty"] = 101;
    assert(!readConfig(document.as<JsonObjectConst>(), untouched));
    document["fans"][2]["duty"] = "50";
    assert(!readConfig(document.as<JsonObjectConst>(), untouched));
    document["fans"][2]["duty"] = 50;
    document["fans"][2]["mode"] = "typo";
    assert(!readConfig(document.as<JsonObjectConst>(), untouched));
    document["fans"][2]["mode"] = "auto";
    document["profiles"][0][1]["temperature"] = 25;
    assert(!readConfig(document.as<JsonObjectConst>(), untouched));
    document["profiles"][0][1]["temperature"] = 35;
    document["profiles"][0][0]["duty"] = nullptr;
    assert(!readConfig(document.as<JsonObjectConst>(), untouched));

    Profile output = defaults().profiles[0];
    for (const char* json : {
             "null", "[]", "[{}]", R"([{"temperature":20,"duty":40},{"temperature":30,"duty":20}])",
             R"([{"temperature":"20","duty":10},{"temperature":30,"duty":20}])"}) {
        JsonDocument points;
        assert(!deserializeJson(points, json));
        assert(!readProfile(points.as<JsonVariantConst>(), output));
        assert(output.count == 5);
        assert(near(output.points[0].duty, 30));
    }
}

}  // namespace

int main() {
    testNtc();
    testCalibration();
    testProfiles();
    testFanMappingAndSafety();
    testConfigurationRoundTrip();
    testInvalidJsonConfiguration();
    std::puts("PASS: NTC conversion/faults, interpolation/validation, fan mapping/overrides/safety, RPM, JSON round-trip/rejection");
    return 0;
}
