#include <cassert>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>
#include <utility>
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
    assert(response["data"]["firmware_version"] == "1.4.7");
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
    const float expectedFirst = 30.0f + 70.0f * std::exp(-1000.0f / 3000.0f);
    const float expectedSecond = 18.6f +
        (30.0f + 70.0f * std::exp(-250.0f / 3000.0f) - 18.6f) *
        std::exp(-750.0f / 3000.0f);
    assert(std::fabs(response["data"]["fans"][1]["applied_duty"].as<float>() - expectedFirst) < 0.01f);
    assert(std::fabs(response["data"]["fans"][2]["applied_duty"].as<float>() - expectedSecond) < 0.01f);
    assert(std::fabs(response["data"]["fans"][3]["applied_duty"].as<float>() - expectedSecond) < 0.01f);
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

void setConstantProfile(Config& config, float duty) {
    config.profiles[0] = {2, {{20.0f, duty}, {40.0f, duty}}};
}

void testAutomaticDutySmoothing() {
    fake::now = 0;
    fake::adc[33] = fake::adc[32] = 1650;
    Config config = defaults();
    config.fans[0] = {false, 25};
    ThermalController thermal;
    thermal.begin();
    auto tick = [&](uint32_t now) {
        fake::now = now;
        thermal.update(now, config);
        return thermal.status(config).fans[0];
    };
    assert(tick(1000).appliedDuty == 25);
    config.fans[0].automatic = true;
    setConstantProfile(config, 75);
    float expected = 75 - 50 * std::exp(-0.25f);
    auto fan = tick(1250);
    assert(fan.requestedDuty == 75 && !fan.boosting);
    assert(std::fabs(fan.appliedDuty - expected) < 0.001f);
    assert(fake::pwm[hardware::PwmPins[0]] == uint32_t(std::lround(expected * 1023 / 100)));
    expected = 75 - 50 * std::exp(-1.0f);
    assert(std::fabs(tick(2000).appliedDuty - expected) < 0.001f);
    setConstantProfile(config, 25);
    const float beforeFall = expected;
    expected = 25 + (beforeFall - 25) * std::exp(-0.25f / 3);
    assert(std::fabs(tick(2250).appliedDuty - expected) < 0.001f);
    expected = 25 + (beforeFall - 25) * std::exp(-1.0f);
    assert(std::fabs(tick(5000).appliedDuty - expected) < 0.001f);

    config.fans[0] = {false, 60};
    assert(tick(5250).appliedDuty == 60);
    config.fans[0].automatic = true;
    expected = 25 + 35 * std::exp(-0.25f / 3);
    assert(std::fabs(tick(5500).appliedDuty - expected) < 0.001f);
    for (const uint32_t adc : {0U, 3300U, 100U}) {
        fake::adc[33] = adc;
        assert(tick(fake::now + 250).appliedDuty == 100);
        assert(thermal.status(config).sensors[0].safety);
        assert(thermal.status(config).fans[1].appliedDuty == 100);
        assert(fake::pwm[hardware::PwmPins[0]] == 1023);
        fake::adc[33] = 1650;
        expected = 25 + 75 * std::exp(-0.25f / 3);
        assert(std::fabs(tick(fake::now + 250).appliedDuty - expected) < 0.001f);
    }
    assert(std::fabs(tick(fake::now + 60000).appliedDuty - 25) < 0.001f);

    fake::now = std::numeric_limits<uint32_t>::max() - 500;
    ThermalController wrapped;
    config.fans[0] = {false, 20};
    wrapped.begin();
    fake::now += 250;
    wrapped.update(fake::now, config);
    config.fans[0].automatic = true;
    setConstantProfile(config, 80);
    fake::now += 1000;
    wrapped.update(fake::now, config);
    expected = 80 - 60 * std::exp(-1.0f);
    assert(std::fabs(wrapped.status(config).fans[0].appliedDuty - expected) < 0.001f);
}

uint32_t millivoltsForTemperature(float celsius) {
    const float kelvin = celsius - hardware::TemperatureOffsetC + 273.15f;
    const float resistance = NtcOhms * std::exp(Beta * (1.0f / kelvin - 1.0f / 298.15f));
    return uint32_t(std::round(SupplyMv * resistance / (DividerOhms + resistance)));
}

void testAutomaticStartHysteresis() {
    fake::now = 0;
    fake::adc[33] = fake::adc[32] = millivoltsForTemperature(30);
    Config config = defaults();
    config.profiles[0] = {3, {{20, 0}, {30, 0}, {35, 65}}};
    ThermalController thermal;
    thermal.begin();
    auto tick = [&](float temperature, uint32_t elapsed = 250) {
        fake::adc[33] = millivoltsForTemperature(temperature);
        fake::now += elapsed;
        thermal.update(fake::now, config);
        return thermal.status(config).fans[0];
    };
    assert(tick(29.9f).appliedDuty == 0);
    assert(fake::pwm[hardware::PwmPins[0]] == 0);
    for (int i = 0; i < 8; ++i) {
        auto fan = tick(i % 2 ? 30.1f : 29.9f);
        assert(fan.requestedDuty == 0 && fan.appliedDuty == 0 && !fan.boosting);
    }
    auto fan = tick(30.4f);
    assert(fan.appliedDuty == 0);
    fan = tick(30.6f);
    const uint32_t started = fake::now;
    assert(fan.requestedDuty > 0 && fan.appliedDuty == 100 && fan.boosting);
    assert(fake::pwm[hardware::PwmPins[0]] == 1023);
    fan = tick(30.1f);
    assert(fan.requestedDuty > 0 && fan.boosting);
    fan = tick(30.1f, 750);
    assert(fake::now - started == hardware::StartupBoostMs);
    assert(fan.requestedDuty > 0 && fan.appliedDuty > 0 && fan.appliedDuty < 100 && !fan.boosting);
    fan = tick(29.9f);
    assert(fan.requestedDuty == 0 && fan.appliedDuty == 0 && !fan.boosting);
    assert(tick(30.1f).appliedDuty == 0);

    fan = tick(81);
    assert(thermal.status(config).sensors[0].safety && fan.appliedDuty == 100);
    assert(tick(30.1f).appliedDuty == 0);
    config.fans[0] = {false, 5};
    fan = tick(29.9f);
    assert(fan.requestedDuty == 5 && fan.appliedDuty == 100 && fan.boosting);
    assert(tick(29.9f, 1000).appliedDuty == 5);
    config.fans[0].automatic = true;
    assert(tick(30.1f).appliedDuty == 0);

    config.profiles[0] = {2, {{35, 0}, {40, 65}}};
    assert(tick(35.4f).appliedDuty == 0);
    assert(tick(35.6f).boosting);
    assert(tick(34.9f).appliedDuty == 0);
    const float exactTemperature = temperatureFromMv(1650, hardware::TemperatureOffsetC).celsius;
    const float zeroEnd = exactTemperature - hardware::AutomaticStartHysteresisC;
    config.profiles[0] = {4, {{zeroEnd - 10, 0}, {zeroEnd - 5, 0},
        {zeroEnd, 0}, {zeroEnd + 5, 65}}};
    fake::adc[33] = 1650;
    fake::now += 250;
    thermal.update(fake::now, config);
    fan = thermal.status(config).fans[0];
    assert(fan.requestedDuty > 0 && fan.boosting);
    assert(tick(zeroEnd - 0.1f).appliedDuty == 0);
    setConstantProfile(config, 0);
    assert(tick(60).appliedDuty == 0);
    setConstantProfile(config, 20);
    assert(tick(19).boosting);
    fan = tick(19, 1000);
    assert(fan.requestedDuty == 20 && fan.appliedDuty > 0 && !fan.boosting);
}

struct TachLog {
    unsigned fan = 0;
    unsigned long windowUs = 0;
    unsigned long pulses = 0;
    unsigned long intervals = 0;
    unsigned long minUs = 0;
    unsigned long maxUs = 0;
    unsigned long long spanUs = 0;
    char countRpm[24]{};
    char periodRpm[24]{};
    double displayRpm = 0;
    double duty = 0;
};

TachLog tachLog(ThermalController& thermal, size_t fan) {
    ConfigurationStore storage;
    BleHealth health;
    TestOta ota;
    CommandProcessor processor(storage, thermal, health, ota);
    char command[96];
    std::snprintf(command, sizeof(command), "{\"cmd\":\"get_tach_diagnostics\",\"after\":%llu}",
                  static_cast<unsigned long long>(thermal.tachSequence() - 1));
    auto response = execute(processor, command);
    assert(response["data"]["dropped"] == false);
    assert(response["data"]["window"]["sequence"].as<uint64_t>() == thermal.tachSequence());
    assert(response["data"]["window"]["fans"].size() == FanCount);
    const JsonObject item = response["data"]["window"]["fans"][fan];
    TachLog result;
    result.fan = item["fan"];
    result.windowUs = item["window_us"];
    result.pulses = item["pulses"];
    result.intervals = item["intervals"];
    result.minUs = item["min_us"];
    result.maxUs = item["max_us"];
    result.spanUs = item["span_us"];
    result.displayRpm = item["rpm_display"];
    result.duty = item["pwm_pct"];
    std::snprintf(result.countRpm, sizeof(result.countRpm), "%.2f", item["rpm_count"].as<double>());
    if (item["rpm_period"].isNull()) {
        std::snprintf(result.periodRpm, sizeof(result.periodRpm), "NA");
    } else {
        std::snprintf(result.periodRpm, sizeof(result.periodRpm), "%.2f", item["rpm_period"].as<double>());
    }
    assert(result.fan == fan + 1);
    return result;
}

void tachEdge(size_t fan, uint32_t atUs) {
    fake::microseconds = atUs;
    const uint8_t pin = hardware::TachPins[fan];
    fake::interrupts[pin](fake::interruptArguments[pin]);
}

void testTachDiagnostics() {
    fake::now = 0;
    fake::useMicroseconds = true;
    fake::microseconds = 0;
    fake::adc[33] = fake::adc[32] = 1650;
    ThermalController thermal;
    ConfigurationStore storage;
    Serial.output.clear();
    thermal.begin();
    assert(Serial.output.find("TACH,") == std::string::npos);
    Serial.output.clear();

    for (uint32_t time = 25000; time <= 1000000; time += 25000) {
        tachEdge(0, time);
        if (time != 600000) {
            tachEdge(1, time);
        }
        if (time == 400000) {
            tachEdge(1, time + 1);
        }
    }
    tachEdge(2, 0);
    tachEdge(2, 1000000);
    fake::microseconds = 1000500;
    advance(thermal, storage, 1000);
    auto log = tachLog(thermal, 0);
    assert(log.windowUs == 1000500 && log.pulses == 40 && log.intervals == 39);
    assert(log.minUs == 25000 && log.maxUs == 25000 && log.spanUs == 975000);
    assert(std::strcmp(log.countRpm, "1199.40") == 0);
    assert(std::strcmp(log.periodRpm, "1200.00") == 0);
    assert(log.displayRpm == 1200);
    assert(log.duty == thermal.status(storage.config()).fans[0].appliedDuty);
    log = tachLog(thermal, 1);
    assert(log.pulses == 40 && log.intervals == 39);
    assert(log.minUs == 1 && log.maxUs == 50000 && log.spanUs == 975000);
    log = tachLog(thermal, 2);
    assert(log.pulses == 2 && log.intervals == 1 && log.minUs == 1000000);
    assert(log.maxUs == 1000000 && log.spanUs == 1000000);
    assert(std::strcmp(log.periodRpm, "30.00") == 0);
    log = tachLog(thermal, 3);
    assert(log.pulses == 0 && log.intervals == 0);
    assert(log.minUs == 0 && log.maxUs == 0 && log.spanUs == 0);
    assert(std::strcmp(log.countRpm, "0.00") == 0);
    assert(std::strcmp(log.periodRpm, "NA") == 0);

    Serial.output.clear();
    for (uint32_t time = 1025000; time <= 2000000; time += 25000) {
        tachEdge(0, time);
    }
    tachEdge(3, 1500000);
    fake::microseconds = 2000000;
    advance(thermal, storage, 2000);
    log = tachLog(thermal, 0);
    assert(log.windowUs == 999500 && log.pulses == 40 && log.intervals == 40);
    assert(log.minUs == 25000 && log.maxUs == 25000 && log.spanUs == 1000000);
    assert(std::strcmp(log.countRpm, "1200.60") == 0);
    assert(std::strcmp(log.periodRpm, "1200.00") == 0);
    log = tachLog(thermal, 3);
    assert(log.pulses == 1 && log.intervals == 0);
    assert(std::strcmp(log.periodRpm, "NA") == 0);

    Serial.output.clear();
    fake::microseconds = 3000000;
    advance(thermal, storage, 3000);
    log = tachLog(thermal, 0);
    assert(log.pulses == 0 && log.intervals == 0 && log.spanUs == 0);
    assert(log.displayRpm == 0 && std::strcmp(log.periodRpm, "NA") == 0);

    Serial.output.clear();
    tachEdge(0, 3100000);
    tachEdge(0, 3125000);
    fake::microseconds = 4000000;
    advance(thermal, storage, 4000);
    log = tachLog(thermal, 0);
    assert(log.pulses == 2 && log.intervals == 1);
    assert(log.minUs == 25000 && log.maxUs == 25000 && log.spanUs == 25000);
    assert(log.displayRpm == 1200 && std::strcmp(log.periodRpm, "1200.00") == 0);
    fake::useMicroseconds = false;
}

void testTachMicrosWraparound() {
    fake::now = 0;
    fake::useMicroseconds = true;
    const uint32_t start = UINT32_MAX - 499999;
    fake::microseconds = start;
    ThermalController thermal;
    ConfigurationStore storage;
    thermal.begin();
    Serial.output.clear();
    for (uint32_t offset = 250000; offset <= 1000000; offset += 250000) {
        tachEdge(0, start + offset);
    }
    fake::microseconds = start + 1000000;
    advance(thermal, storage, 1000);
    const auto log = tachLog(thermal, 0);
    assert(log.windowUs == 1000000 && log.pulses == 4 && log.intervals == 3);
    assert(log.minUs == 250000 && log.maxUs == 250000 && log.spanUs == 750000);
    assert(std::strcmp(log.countRpm, "120.00") == 0);
    assert(std::strcmp(log.periodRpm, "120.00") == 0);
    fake::useMicroseconds = false;
}

void testTachBleHistory() {
    fake::now = 0;
    ThermalController thermal;
    ConfigurationStore storage;
    BleHealth health;
    TestOta ota;
    CommandProcessor processor(storage, thermal, health, ota);
    thermal.begin();
    assert(status(processor)["data"]["tach_diagnostics_supported"] == true);
    auto reply = execute(processor, R"({"cmd":"get_tach_diagnostics"})");
    assert(reply["data"]["latest_sequence"] == 0);
    assert(reply["data"]["window"].isNull());
    reply = execute(processor, R"({"cmd":"get_tach_diagnostics","after":0})");
    assert(reply["data"]["window"].isNull() && reply["data"]["dropped"] == false);
    for (uint32_t time = 1000; time <= 140000; time += 1000) {
        advance(thermal, storage, time);
    }
    reply = execute(processor, R"({"cmd":"get_tach_diagnostics","after":1})");
    assert(reply["data"]["dropped"] == true && reply["data"]["window"].isNull());
    for (unsigned after = 12; after < 140; ++after) {
        char command[96];
        std::snprintf(command, sizeof(command), "{\"cmd\":\"get_tach_diagnostics\",\"after\":%u}", after);
        reply = execute(processor, command);
        assert(reply["data"]["latest_sequence"] == 140);
        assert(reply["data"]["dropped"] == false);
        assert(reply["data"]["window"]["sequence"] == after + 1);
        assert(reply["data"]["window"]["uptime_ms"] == (after + 1) * 1000);
        assert(reply["data"]["window"]["fans"][0]["rpm_count"] == 0);
        assert(reply["data"]["window"]["fans"][0]["rpm_period"].isNull());
    }
    reply = execute(processor, R"({"cmd":"get_tach_diagnostics","after":140})");
    assert(reply["data"]["dropped"] == false && reply["data"]["window"].isNull());
    reply = execute(processor, R"({"cmd":"get_tach_diagnostics","after":141})");
    assert(reply["data"]["dropped"] == true && reply["data"]["window"].isNull());
    for (const char* command : {
        R"({"cmd":"get_tach_diagnostics","after":-1})",
        R"({"cmd":"get_tach_diagnostics","after":1.5})",
        R"({"cmd":"get_tach_diagnostics","after":null})",
        R"({"cmd":"get_tach_diagnostics","after":"1"})"}) {
        assert(execute(processor, command, false)["error"] == "invalid_tach_cursor");
    }
}

void testTachAnomalyTrace() {
    fake::now = 0;
    fake::useMicroseconds = true;
    fake::microseconds = 0;
    ThermalController thermal;
    ConfigurationStore storage;
    BleHealth health;
    TestOta ota;
    CommandProcessor processor(storage, thermal, health, ota);
    thermal.begin();
    tachEdge(0, 850000);
    for (uint32_t time = 875000; time <= 975000; time += 25000) {
        tachEdge(0, time);
    }
    tachEdge(0, 987500);
    fake::microseconds = 1000000;
    advance(thermal, storage, 1000);
    auto reply = execute(processor, R"({"cmd":"get_tach_diagnostics","after":0})");
    assert(reply["data"]["window"]["fans"][0]["trace_us"].isNull());
    assert(reply["data"]["window"]["fans"][0]["pulses"] == 7);
    for (uint32_t time = 1000000; time <= 1075000; time += 25000) {
        tachEdge(0, time);
    }
    // A later anomaly must not overwrite the first completed trace before snapshot.
    tachEdge(0, 1087500);
    tachEdge(0, 1100000);
    fake::microseconds = 2000000;
    advance(thermal, storage, 2000);
    reply = execute(processor, R"({"cmd":"get_tach_diagnostics","after":1})");
    JsonObject fan = reply["data"]["window"]["fans"][0];
    assert(fan["pulses"] == 6 && fan["intervals"] == 6);
    assert(fan["trace_trigger_us"] == 987500);
    assert(fan["trace_reference_us"] == 25000);
    const uint32_t expected[] = {25000, 25000, 25000, 25000, 12500, 12500, 25000, 25000, 25000};
    assert(fan["trace_us"].size() == ThermalController::TachTraceSize);
    for (size_t i = 0; i < ThermalController::TachTraceSize; ++i) {
        assert(fan["trace_us"][i] == expected[i]);
    }
    assert(fan["trace_us"][4].as<uint32_t>() + fan["trace_us"][5].as<uint32_t>() ==
           fan["trace_reference_us"].as<uint32_t>());
    assert(fan["merged_pairs"] == 1);
    assert(fan["corrected_intervals"] == 4);
    assert(fan["corrected_span_us"] == 100000);
    assert(fan["rpm_raw_display"] == 180);
    assert(fan["rpm_corrected"] == 1200);
    assert(fan["rpm_display"] == fan["rpm_corrected"]);
    const String repeated = processor.process(R"({"cmd":"get_tach_diagnostics","after":1})");
    assert(!deserializeJson(reply, repeated));
    assert(reply["data"]["window"]["fans"][0]["trace_trigger_us"] == 987500);

    fake::microseconds = 3000000;
    advance(thermal, storage, 3000);
    for (uint32_t time = 3100000; time <= 3300000; time += 25000) {
        tachEdge(0, time);
    }
    fake::microseconds = 4000000;
    advance(thermal, storage, 4000);
    reply = execute(processor, R"({"cmd":"get_tach_diagnostics","after":3})");
    assert(reply["data"]["window"]["fans"][0]["trace_us"].isNull());
    fake::useMicroseconds = false;
}

void testTachTraceWraparoundAndThreshold() {
    fake::now = 0;
    fake::useMicroseconds = true;
    const uint32_t start = UINT32_MAX - 109999;
    fake::microseconds = start;
    ThermalController thermal;
    ConfigurationStore storage;
    BleHealth health;
    TestOta ota;
    CommandProcessor processor(storage, thermal, health, ota);
    thermal.begin();
    for (uint32_t offset = 0; offset <= 100000; offset += 25000) {
        tachEdge(0, start + offset);
        tachEdge(1, start + offset);
    }
    for (uint32_t offset : {112500U, 125000U, 150000U, 175000U, 200000U}) {
        tachEdge(0, start + offset);
    }
    // Exactly 75% does not trigger; no zero/absent timestamp sentinel is used.
    tachEdge(1, start + 118750);
    for (uint32_t offset : {143750U, 168750U, 193750U, 218750U}) {
        tachEdge(1, start + offset);
    }
    fake::microseconds = start + 1000000;
    advance(thermal, storage, 1000);
    auto reply = execute(processor, R"({"cmd":"get_tach_diagnostics","after":0})");
    auto fan = reply["data"]["window"]["fans"][0];
    assert(fan["trace_trigger_us"].as<uint32_t>() == start + 112500);
    assert(fan["trace_us"][4] == 12500 && fan["trace_us"][5] == 12500);
    assert(reply["data"]["window"]["fans"][1]["trace_us"].isNull());
    assert(fan["pulses"] == 10);
    fake::useMicroseconds = false;
}

void testCorrectedPeriods() {
    using Filter = ThermalController::PeriodFilter;
    Filter clean;
    for (unsigned i = 0; i < 40; ++i) {
        clean.add(25000);
    }
    assert(clean.window.intervals == 40 && clean.window.spanUs == 1000000);
    assert(clean.window.mergedPairs == 0);

    Filter split;
    for (unsigned i = 0; i < 6; ++i) {
        split.add(25000);
    }
    split.window = {};
    split.add(12500);
    assert(split.window.intervals == 0);
    split.add(12500);
    assert(split.window.intervals == 0);
    split.window = {};
    split.add(25000);
    assert(split.window.mergedPairs == 1 && split.window.intervals == 2);
    assert(split.window.spanUs == 50000);

    for (unsigned pairs = 2; pairs <= 4; ++pairs) {
        Filter consecutive;
        for (unsigned i = 0; i < 6; ++i) {
            consecutive.add(27300);
        }
        consecutive.window = {};
        for (unsigned i = 0; i < pairs; ++i) {
            consecutive.add(14000);
            consecutive.add(13300);
        }
        assert(consecutive.window.intervals == 0);
        consecutive.window = {};
        consecutive.add(27300);
        assert(consecutive.window.mergedPairs == pairs);
        assert(consecutive.window.intervals == pairs + 1);
        assert(consecutive.window.spanUs == uint64_t(pairs + 1) * 27300);
    }

    Filter captured;
    for (unsigned i = 0; i < 6; ++i) {
        captured.add(27300);
    }
    captured.window = {};
    for (uint32_t interval : {14026U, 13407U, 13631U, 13528U, 27441U}) {
        captured.add(interval);
    }
    assert(captured.window.mergedPairs == 2 && captured.window.intervals == 3);
    assert(captured.window.spanUs == 82033);

    Filter overLimit;
    for (unsigned i = 0; i < 6; ++i) {
        overLimit.add(25000);
    }
    overLimit.window = {};
    for (unsigned i = 0; i < 10; ++i) {
        overLimit.add(12500);
    }
    overLimit.add(25000);
    assert(overLimit.window.mergedPairs == 0 && overLimit.window.intervals == 11);
    assert(overLimit.window.spanUs == 150000 && overLimit.pendingCount == 0);

    Filter invalidRun;
    for (unsigned i = 0; i < 6; ++i) {
        invalidRun.add(25000);
    }
    invalidRun.window = {};
    for (uint32_t interval : {12500U, 12500U, 8000U, 8000U, 25000U}) {
        invalidRun.add(interval);
    }
    assert(invalidRun.window.mergedPairs == 0 && invalidRun.window.intervals == 5);
    assert(invalidRun.window.spanUs == 66000);

    Filter acceleration;
    for (unsigned i = 0; i < 6; ++i) {
        acceleration.add(25000);
    }
    acceleration.window = {};
    for (unsigned i = 0; i < 20; ++i) {
        acceleration.add(12500);
    }
    assert(acceleration.window.mergedPairs == 0);
    assert(acceleration.window.intervals == 20 && acceleration.window.spanUs == 250000);

    Filter unrelated;
    for (unsigned i = 0; i < 6; ++i) {
        unrelated.add(25000);
    }
    unrelated.window = {};
    unrelated.add(8000);
    unrelated.add(8000);
    unrelated.add(25000);
    assert(unrelated.window.mergedPairs == 0 && unrelated.window.intervals == 3);
    assert(unrelated.window.spanUs == 41000);

    Filter uneven;
    for (unsigned i = 0; i < 40; ++i) {
        uneven.add(i % 2 == 0 ? 15000 : 25000);
    }
    assert(uneven.window.mergedPairs == 0 && uneven.window.intervals == 40);
    assert(uneven.window.spanUs == 800000);

    Filter threshold;
    for (unsigned i = 0; i < 6; ++i) {
        threshold.add(24000);
    }
    threshold.window = {};
    threshold.add(18000);
    threshold.add(6000);
    threshold.add(24000);
    assert(threshold.window.mergedPairs == 0);
    assert(threshold.window.intervals == 3 && threshold.window.spanUs == 48000);

    // Recorded pairs from the 2026-11-08 capture, including unequal rotor periods.
    for (const auto& pair : {std::pair<uint32_t, uint32_t>{13502, 12908},
                             {13105, 13018}, {19639, 18854}, {20045, 19399}}) {
        Filter recorded;
        const uint32_t baseline = pair.first < 15000 ? 26300 : 39000;
        for (unsigned i = 0; i < 6; ++i) {
            recorded.add(baseline);
        }
        recorded.window = {};
        recorded.add(pair.first);
        recorded.add(pair.second);
        recorded.add(baseline);
        assert(recorded.window.mergedPairs == 1 && recorded.window.intervals == 2);
        assert(recorded.window.spanUs == uint64_t(pair.first) + pair.second + baseline);
    }
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
    testAutomaticDutySmoothing();
    testAutomaticStartHysteresis();
    testTachDiagnostics();
    testTachMicrosWraparound();
    testTachBleHistory();
    testTachAnomalyTrace();
    testTachTraceWraparoundAndThreshold();
    testCorrectedPeriods();
    testOtaCommandsAndPacketValidation();
    std::puts("PASS: class integration, BLE JSON v1, OTA state/packet validation, automatic PWM smoothing/start hysteresis, PWM/NTC/TACH diagnostics, NVS, timer wraparound");
}
