#include "ThermalController.h"
#include <esp_arduino_version.h>
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

}  // namespace

void IRAM_ATTR ThermalController::tachInterrupt(void* argument) {
    auto& counter = *static_cast<TachCounter*>(argument);
    portENTER_CRITICAL_ISR(&counter.mux);
    counter.pulses = counter.pulses + 1;
    portEXIT_CRITICAL_ISR(&counter.mux);
}

void ThermalController::holdFullSpeed(size_t fan) {
#if ESP_ARDUINO_VERSION_MAJOR >= 3
    ledcDetach(hardware::PwmPins[fan]);
#else
    ledcDetachPin(hardware::PwmPins[fan]);
#endif
    pinMode(hardware::PwmPins[fan], OUTPUT);
    digitalWrite(hardware::PwmPins[fan], hardware::PwmInverted ? LOW : HIGH);
    fans_[fan].pwmReady = false;
    fans_[fan].appliedDuty = 100.0f;
}

void ThermalController::writePwm(size_t fan, float duty) {
    const uint32_t maximum = (1U << hardware::PwmBits) - 1;
    const float electricalDuty = hardware::PwmInverted ? 100.0f - duty : duty;
    const uint32_t value = lroundf(electricalDuty * maximum / 100.0f);
#if ESP_ARDUINO_VERSION_MAJOR >= 3
    if (!ledcWrite(hardware::PwmPins[fan], value)) {
        if (fans_[fan].pwmReady) {
            Serial.printf("ERROR: PWM write failed on fan %u\n", unsigned(fan + 1));
        }
        holdFullSpeed(fan);
        return;
    }
#else
    ledcWrite(fan, value);
#endif
    fans_[fan].appliedDuty = duty;
}

void ThermalController::begin() {
    for (size_t fan = 0; fan < FanCount; ++fan) {
        auto& state = fans_[fan];
        pinMode(hardware::PwmPins[fan], OUTPUT);
        digitalWrite(hardware::PwmPins[fan], hardware::PwmInverted ? LOW : HIGH);
#if ESP_ARDUINO_VERSION_MAJOR >= 3
        state.pwmReady = ledcAttachChannel(hardware::PwmPins[fan], hardware::PwmFrequency,
                                         hardware::PwmBits, fan);
#else
        state.pwmReady = ledcSetup(fan, hardware::PwmFrequency, hardware::PwmBits) > 0;
        ledcAttachPin(hardware::PwmPins[fan], fan);
#endif
        if (!state.pwmReady) {
            holdFullSpeed(fan);
            Serial.printf("ERROR: PWM initialization failed on fan %u; output held at full speed\n",
                          unsigned(fan + 1));
        } else {
            writePwm(fan, 100.0f);
        }
        state.boosting = true;
        state.boostStarted = millis();
        pinMode(hardware::TachPins[fan], INPUT);
        attachInterruptArg(hardware::TachPins[fan], tachInterrupt, &tach_[fan], FALLING);
    }
    analogReadResolution(12);
    for (const uint8_t pin : hardware::SensorPins) {
        pinMode(pin, INPUT);
        analogSetPinAttenuation(pin, ADC_11db);
    }
    lastControl_ = millis();
    lastRpm_ = lastControl_;
}

void ThermalController::readSensors() {
    for (size_t sensor = 0; sensor < SensorCount; ++sensor) {
        auto& state = sensors_[sensor];
        uint32_t sum = 0;
        for (size_t sample = 0; sample < 16; ++sample) {
            sum += analogReadMilliVolts(hardware::SensorPins[sensor]);
        }
        state.millivolts = sum / 16.0f;
        const Temperature reading = temperatureFromMv(
            state.millivolts, hardware::TemperatureOffsetC);
        if (reading.fault != state.temperature.fault) {
            Serial.printf("Sensor %u: %s\n", unsigned(sensor + 1), faultName(reading.fault));
        }
        state.temperature = reading;
    }
}

void ThermalController::controlFans(uint32_t now, const Config& config) {
    for (size_t fan = 0; fan < FanCount; ++fan) {
        auto& state = fans_[fan];
        const float target = targetDuty(config, fan, sensors_[fan / 2].temperature);
        if (target > 0 && state.requestedDuty == 0) {
            state.boosting = true;
            state.boostStarted = now;
        }
        if (target == 0 || now - state.boostStarted >= hardware::StartupBoostMs) {
            state.boosting = false;
        }
        state.requestedDuty = target;
        if (state.pwmReady) {
            writePwm(fan, state.boosting ? 100.0f : target);
        }
    }
}

void ThermalController::updateRpm(uint32_t now) {
    const uint32_t elapsed = now - lastRpm_;
    if (elapsed < hardware::RpmIntervalMs) {
        return;
    }
    for (size_t fan = 0; fan < FanCount; ++fan) {
        auto& counter = tach_[fan];
        portENTER_CRITICAL(&counter.mux);
        const uint32_t pulses = counter.pulses;
        counter.pulses = 0;
        portEXIT_CRITICAL(&counter.mux);
        fans_[fan].rpm = rpmFromPulses(pulses, elapsed, hardware::PulsesPerRevolution[fan]);
    }
    lastRpm_ = now;
}

void ThermalController::update(uint32_t now, const Config& config) {
    if (now - lastControl_ >= hardware::ControlIntervalMs) {
        readSensors();
        controlFans(now, config);
        lastControl_ = now;
    }
    updateRpm(now);
}

void ThermalController::writeStatus(JsonObject data, const Config& config) const {
    JsonArray sensors = data["sensors"].to<JsonArray>();
    for (size_t sensor = 0; sensor < SensorCount; ++sensor) {
        const auto& state = sensors_[sensor];
        JsonObject item = sensors.add<JsonObject>();
        item["sensor"] = sensor + 1;
        item["millivolts"] = state.millivolts;
        if (state.temperature.fault == SensorFault::None) {
            item["temperature"] = state.temperature.celsius;
        } else {
            item["temperature"] = nullptr;
        }
        item["fault"] = faultName(state.temperature.fault);
        item["safety"] = safetyActive(state.temperature);
    }
    JsonArray fans = data["fans"].to<JsonArray>();
    for (size_t fan = 0; fan < FanCount; ++fan) {
        const auto& state = fans_[fan];
        JsonObject item = fans.add<JsonObject>();
        item["fan"] = fan + 1;
        item["sensor"] = fan / 2 + 1;
        item["mode"] = config.fans[fan].automatic ? "auto" : "fixed";
        item["fixed_duty"] = config.fans[fan].duty;
        item["target_duty"] = state.requestedDuty;
        item["applied_duty"] = state.appliedDuty;
        item["rpm"] = state.rpm;
        item["pwm_ok"] = state.pwmReady;
        item["boost"] = state.boosting;
    }
}

}  // namespace controller
