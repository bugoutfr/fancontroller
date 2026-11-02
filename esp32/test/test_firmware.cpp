#include <cassert>
#include <cmath>
#include <cstdio>
#include <limits>
#include "CommandProcessor.h"
#include "HardwareConfig.h"

using namespace controller;

namespace {

JsonDocument execute(CommandProcessor& processor, const char* command, bool ok = true) {
    const String response = processor.process(command);
    assert(!response.isEmpty());
    assert(response.c_str()[response.length() - 1] == '\n');
    JsonDocument document;
    assert(!deserializeJson(document, response));
    assert(document["ok"].is<bool>());
    assert(document["ok"].as<bool>() == ok);
    if (!ok) {
        assert(document["error"].is<const char*>());
        assert(document["message"].is<const char*>());
    }
    return document;
}

JsonDocument status(CommandProcessor& processor) {
    return execute(processor, R"({"cmd":"get_status"})");
}

void advance(ThermalController& thermal, const ConfigurationStore& storage, uint32_t now) {
    fake::now = now;
    thermal.update(now, storage.config());
}

void testCommandsRegulationAndStorage() {
    fake::nvs.clear();
    fake::adc[33] = 1650;
    fake::adc[32] = 1650;
    ConfigurationStore storage;
    ThermalController thermal;
    CommandProcessor processor(storage, thermal);
    thermal.begin();
    storage.begin();

    auto response = execute(processor, R"({"cmd":"get_config"})");
    assert(response["data"]["version"] == 1);
    assert(response["data"]["profiles"].size() == 2);
    assert(response["data"]["fans"].size() == 4);
    assert(response["dirty"] == false);
    assert(response["storage"] == "defaults");
    advance(thermal, storage, 250);
    response = status(processor);
    assert(response["data"]["firmware_version"] == "1.1.2");
    assert(response["data"]["sensors"].size() == 2);
    assert(response["data"]["fans"].size() == 4);
    assert(std::fabs(response["data"]["sensors"][0]["temperature"].as<float>() - 24.3f) < 0.01f);
    assert(std::fabs(response["data"]["sensors"][1]["temperature"].as<float>() - 24.3f) < 0.01f);
    assert(response["data"]["sensors"][0]["millivolts"] == 1650);
    assert(response["data"]["fans"][0]["applied_duty"] == 100);
    assert(response["data"]["fans"][0]["boost"] == true);

    execute(processor, R"({"cmd":"set_profile","sensor":2,"points":[{"temperature":20,"duty":10},{"temperature":30,"duty":30}]})");
    execute(processor, R"({"cmd":"set_fan","fan":1,"mode":"fixed","duty":0})");
    advance(thermal, storage, 1000);
    response = status(processor);
    assert(response["data"]["fans"][0]["applied_duty"] == 0);
    assert(std::fabs(response["data"]["fans"][1]["applied_duty"].as<float>() - 30) < 0.01f);
    assert(std::fabs(response["data"]["fans"][2]["applied_duty"].as<float>() - 18.6f) < 0.01f);
    assert(std::fabs(response["data"]["fans"][3]["applied_duty"].as<float>() - 18.6f) < 0.01f);
    assert(response["data"]["fans"][3]["sensor"] == 2);

    execute(processor, R"({"cmd":"set_fan","fan":1,"mode":"fixed","duty":45})");
    advance(thermal, storage, 1250);
    response = status(processor);
    assert(response["data"]["fans"][0]["target_duty"] == 45);
    assert(response["data"]["fans"][0]["applied_duty"] == 100);
    advance(thermal, storage, 2250);
    response = status(processor);
    assert(response["data"]["fans"][0]["applied_duty"] == 45);

    for (size_t fan = 0; fan < FanCount; ++fan) {
        const auto pin = hardware::TachPins[fan];
        for (size_t pulse = 0; pulse < 40 * (fan + 1); ++pulse) {
            fake::interrupts[pin](fake::interruptArguments[pin]);
        }
    }
    advance(thermal, storage, 3250);
    response = status(processor);
    for (size_t fan = 0; fan < FanCount; ++fan) {
        assert(response["data"]["fans"][fan]["rpm"].as<float>() == 1200.0f * (fan + 1));
    }

    fake::adc[33] = 0;
    advance(thermal, storage, 3500);
    response = status(processor);
    assert(response["data"]["sensors"][0]["temperature"].isNull());
    assert(response["data"]["sensors"][0]["fault"] == "short_circuit");
    assert(response["data"]["sensors"][0]["safety"] == true);
    assert(response["data"]["fans"][0]["applied_duty"] == 100);
    assert(response["data"]["fans"][1]["applied_duty"] == 100);
    assert(response["data"]["fans"][0]["mode"] == "fixed");
    assert(response["data"]["fans"][2]["applied_duty"].as<float>() < 100);
    fake::adc[33] = 1650;
    advance(thermal, storage, 3750);
    assert(status(processor)["data"]["fans"][0]["applied_duty"] == 45);

    const char* invalid[] = {
        "{}",
        R"({"cmd":"unknown"})",
        R"({"cmd":"set_fan","fan":0,"mode":"auto"})",
        R"({"cmd":"set_fan","fan":5,"mode":"fixed","duty":40})",
        R"({"cmd":"set_fan","fan":1.5,"mode":"auto"})",
        R"({"cmd":"set_fan","fan":1,"mode":"fixed","duty":101})",
        R"({"cmd":"set_fan","fan":1,"mode":"fixed","duty":"50"})",
        R"({"cmd":"set_profile","sensor":1,"points":[{"temperature":40,"duty":70},{"temperature":30,"duty":90}]})"
    };
    for (const char* command : invalid) {
        execute(processor, command, false);
        assert(storage.config().fans[0].duty == 45);
        assert(storage.config().profiles[0].count == 5);
    }
    response = execute(processor, R"({"cmd":"save"})");
    assert(response["dirty"] == false);
    assert(!storage.dirty());
    ConfigurationStore rebooted;
    rebooted.begin();
    assert(std::strcmp(rebooted.state(), "loaded") == 0);
    assert(!rebooted.config().fans[0].automatic);
    assert(rebooted.config().fans[0].duty == 45);
    assert(rebooted.config().profiles[1].count == 2);
    execute(processor, R"({"cmd":"defaults"})");
    assert(storage.dirty());
    assert(storage.config().fans[0].automatic);
    assert(rebooted.config().fans[0].duty == 45);

    fake::storageWriteOk = false;
    execute(processor, R"({"cmd":"save"})", false);
    assert(storage.dirty());
    assert(std::strcmp(storage.state(), "write_error") == 0);
    fake::storageWriteOk = true;
    fake::pwmWriteOk = false;
    advance(thermal, storage, 4000);
    response = status(processor);
    assert(response["data"]["fans"][0]["pwm_ok"] == false);
    assert(response["data"]["fans"][0]["applied_duty"] == 100);
    assert(fake::levels[15] == HIGH);
    fake::pwmWriteOk = true;

    fake::nvs["config"] = "{}";
    ConfigurationStore invalidStore;
    invalidStore.begin();
    assert(std::strcmp(invalidStore.state(), "invalid") == 0);
    assert(validConfig(invalidStore.config()));
    fake::storageAvailable = false;
    ConfigurationStore unavailable;
    unavailable.begin();
    assert(!unavailable.available());
    assert(!unavailable.save());
    fake::storageAvailable = true;
}

void testTimerWraparound() {
    fake::now = std::numeric_limits<uint32_t>::max() - 500;
    fake::adc[33] = 1650;
    fake::adc[32] = 1650;
    ThermalController thermal;
    ConfigurationStore storage;
    CommandProcessor processor(storage, thermal);
    thermal.begin();
    advance(thermal, storage, 600);
    auto response = status(processor);
    assert(response["data"]["fans"][0]["boost"] == false);
    assert(response["data"]["fans"][0]["applied_duty"].as<float>() < 100);
}

}  // namespace

int main() {
    testCommandsRegulationAndStorage();
    testTimerWraparound();
    std::puts("PASS: class integration, BLE JSON v1 commands/status, PWM/NTC/TACH simulation, boosts, NVS reboot/errors, timer wraparound");
}
