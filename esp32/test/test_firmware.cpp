#include <cassert>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>
#include "CommandProcessor.h"
#include "HardwareConfig.h"

using namespace controller;

namespace {

class TestOta final : public OtaControl {
public:
    OtaResult info(uint32_t& maxSize, char runningImageSha256[65]) const override {
        maxSize = 0x140000;
        std::strcpy(runningImageSha256, Digest);
        return {true, nullptr, nullptr};
    }
    OtaResult begin(uint32_t size, const char* sha256, uint32_t) override {
        uint8_t digest[32];
        if (state_ != OtaState::Idle || size == 0 || !parseSha256Hex(sha256, digest)) {
            return {false, "invalid_ota_request", "Invalid OTA begin request"};
        }
        state_ = OtaState::Receiving;
        return {true, nullptr, nullptr};
    }
    OtaResult finish(uint32_t& size, char verifiedSha256[65]) override {
        if (state_ != OtaState::Receiving) {
            return {false, "ota_not_receiving", "No OTA transfer"};
        }
        state_ = OtaState::Staged;
        size = 123;
        std::strcpy(verifiedSha256, Digest);
        return {true, nullptr, nullptr};
    }
    OtaResult activate() override {
        if (state_ != OtaState::Staged) return {false, "ota_not_staged", "No staged image"};
        activationPending_ = true;
        return {true, nullptr, nullptr};
    }
    OtaResult abort() override {
        if (state_ == OtaState::Idle) return {false, "ota_not_active", "No active OTA"};
        state_ = OtaState::Idle;
        activationPending_ = false;
        return {true, nullptr, nullptr};
    }
    OtaAcknowledgment processPacket(const uint8_t*, size_t, uint32_t) override {
        return {OtaPacketStatus::Ok, 1};
    }
    void update(uint32_t) override {}
    void endSession() override { state_ = OtaState::Idle; activationPending_ = false; }
    bool activationReplyPending() const override { return activationPending_; }
    void activationReplyDrained(uint32_t) override { activationPending_ = false; }
    bool restartDue(uint32_t) const override { return false; }
    OtaState state() const override { return state_; }

    static constexpr char Digest[] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

private:
    OtaState state_ = OtaState::Idle;
    bool activationPending_ = false;
};

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
    BleHealth bleHealth;
    TestOta ota;
    CommandProcessor processor(storage, thermal, bleHealth, ota);
    thermal.begin();
    storage.begin();

    bleHealth.state = BleHealthState::Ready;
    auto response = execute(processor, R"({"cmd":"get_config"})");
    assert(response["data"]["version"] == 1);
    assert(response["data"]["profiles"].size() == 2);
    assert(response["data"]["fans"].size() == 4);
    assert(response["dirty"] == false);
    assert(response["storage"] == "defaults");
    advance(thermal, storage, 250);
    response = status(processor);
    assert(response["data"]["ble_available"] == true);
    assert(response["data"]["ble_status"] == "ready");
    assert(response["data"]["firmware_version"] == "1.4.0");
    assert(response["data"]["ble_rssi_dbm"].isNull());
    for (const int8_t rssi : {int8_t(-127), int8_t(-62), int8_t(0), int8_t(20)}) {
        const String wire = processor.process(R"({"cmd":"get_status"})", &rssi);
        JsonDocument measured;
        assert(!deserializeJson(measured, wire));
        assert(measured["ok"] == true);
        assert(measured["data"]["ble_rssi_dbm"].is<int>());
        assert(measured["data"]["ble_rssi_dbm"].as<int>() == rssi);
    }
    assert(status(processor)["data"]["ble_rssi_dbm"].isNull());
    assert(response["data"]["sensors"].size() == 2);
    assert(response["data"]["fans"].size() == 4);
    assert(std::fabs(response["data"]["sensors"][0]["temperature"].as<float>() - 24.3f) < 0.01f);
    assert(std::fabs(response["data"]["sensors"][1]["temperature"].as<float>() - 24.3f) < 0.01f);
    assert(response["data"]["sensors"][0]["millivolts"] == 1650);
    assert(response["data"]["fans"][0]["applied_duty"] == 100);
    assert(response["data"]["fans"][0]["boost"] == true);
    const ThermalStatus thermalStatus = thermal.status(storage.config());
    assert(thermalStatus.sensors[0].temperature.fault == SensorFault::None);
    assert(thermalStatus.fans[0].appliedDuty == 100);

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
    BleHealth bleHealth;
    TestOta ota;
    bleHealth.state = BleHealthState::AdvertisingStartFailed;
    CommandProcessor processor(storage, thermal, bleHealth, ota);
    thermal.begin();
    advance(thermal, storage, 600);
    auto response = status(processor);
    assert(response["data"]["ble_available"] == false);
    assert(response["data"]["ble_status"] == "advertising_start_failed");
    assert(response["data"]["fans"][0]["boost"] == false);
    assert(response["data"]["fans"][0]["applied_duty"].as<float>() < 100);
}

void testOtaCommandsAndPacketValidation() {
    ConfigurationStore storage;
    ThermalController thermal;
    BleHealth bleHealth;
    TestOta ota;
    CommandProcessor processor(storage, thermal, bleHealth, ota);

    auto response = execute(processor, R"({"cmd":"ota_info"})", false);
    assert(response["error"] == "ota_subscription_required");

    const String infoWire = processor.process(R"({"cmd":"ota_info"})", nullptr, true);
    JsonDocument info;
    assert(!deserializeJson(info, infoWire));
    assert(info["ok"] == true && info["cmd"] == "ota_info");
    assert(info["data"]["protocol"] == 1);
    assert(info["data"]["max_size"] == 0x140000);
    assert(info["data"]["running_image_sha256"] == TestOta::Digest);

    const String invalidBegin = processor.process(
        R"({"cmd":"ota_begin","size":123,"sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\u0000"})",
        nullptr,
        true);
    JsonDocument invalid;
    assert(!deserializeJson(invalid, invalidBegin));
    assert(invalid["ok"] == false && invalid["error"] == "invalid_ota_request");
    const String beginWire = processor.process(
        R"({"cmd":"ota_begin","size":123,"sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"})",
        nullptr,
        true);
    JsonDocument begun;
    assert(!deserializeJson(begun, beginWire));
    assert(begun["ok"] == true && begun["cmd"] == "ota_begin");
    assert(begun["data"]["offset"] == 0);
    assert(ota.state() == OtaState::Receiving);
    response = execute(processor, R"({"cmd":"set_fan","fan":1,"mode":"fixed","duty":50})", false);
    assert(response["error"] == "ota_busy");

    const String finishWire = processor.process(R"({"cmd":"ota_finish"})", nullptr, true);
    JsonDocument finished;
    assert(!deserializeJson(finished, finishWire));
    assert(finished["ok"] == true && finished["data"]["size"] == 123);
    assert(finished["data"]["sha256"] == TestOta::Digest);
    response = execute(processor, R"({"cmd":"defaults"})", false);
    assert(response["error"] == "ota_busy");

    const String activateWire = processor.process(R"({"cmd":"ota_activate"})", nullptr, true);
    JsonDocument activated;
    assert(!deserializeJson(activated, activateWire));
    assert(activated["ok"] == true && activated["data"]["scheduled"] == true);
    assert(processor.otaActivationReplyPending());
    processor.otaActivationReplyDrained(10);
    assert(!processor.otaActivationReplyPending());
    response = execute(processor, R"({"cmd":"ota_abort"})", false);
    assert(response["error"] == "ota_subscription_required");

    uint8_t digest[32]{};
    assert(parseSha256Hex(TestOta::Digest, digest));
    char formatted[65];
    formatSha256Hex(digest, formatted);
    assert(std::strcmp(formatted, TestOta::Digest) == 0);
    assert(!parseSha256Hex("0123456789abcdef", digest));

    const uint8_t validPacket[] = {0, 0, 0, 0, 0x41, 0x42};
    uint32_t nextOffset = 0;
    assert(validateOtaPacket(validPacket, sizeof(validPacket), 0, 2, nextOffset) == OtaPacketStatus::Ok);
    assert(nextOffset == 2);
    assert(validateOtaPacket(validPacket, sizeof(validPacket), 1, 2, nextOffset) == OtaPacketStatus::WrongOffset);
    assert(validateOtaPacket(validPacket, sizeof(validPacket), 0, 1, nextOffset) == OtaPacketStatus::ImageSizeExceeded);
    assert(validateOtaPacket(validPacket, OtaPacketHeaderSize, 0, 2, nextOffset) == OtaPacketStatus::InvalidLength);
}

}  // namespace

int main() {
    testCommandsRegulationAndStorage();
    testTimerWraparound();
    testOtaCommandsAndPacketValidation();
    std::puts("PASS: class integration, BLE JSON v1, OTA state/packet validation, PWM/NTC/TACH, NVS, timer wraparound");
}
