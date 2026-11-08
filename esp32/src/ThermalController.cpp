#include "ThermalController.h"
#include <cstdio>
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
    const uint32_t nowUs = micros();
    auto& window = counter.window;
    ++window.pulses;
    if (counter.hasLastEdge) {
        const uint32_t intervalUs = nowUs - counter.lastEdgeUs;
        ++window.intervals;
        window.spanUs += intervalUs;
        counter.filter.add(intervalUs);
        if (intervalUs < window.minUs) {
            window.minUs = intervalUs;
        }
        if (intervalUs > window.maxUs) {
            window.maxUs = intervalUs;
        }
        auto& trace = counter.trace;
        if (trace.count > 0 && trace.count < TachTraceSize) {
            trace.intervalsUs[trace.count++] = intervalUs;
        } else if (trace.count == 0 && counter.recentCount == TachTraceBefore) {
            const uint32_t previousUs = counter.recentUs[
                (counter.recentNext + TachTraceBefore - 1) % TachTraceBefore];
            // Trigger only: every edge still contributes to both RPM estimators.
            if (uint64_t(intervalUs) * 4 < uint64_t(previousUs) * 3) {
                for (size_t i = 0; i < TachTraceBefore; ++i) {
                    trace.intervalsUs[i] = counter.recentUs[
                        (counter.recentNext + i) % TachTraceBefore];
                }
                trace.intervalsUs[TachTraceBefore] = intervalUs;
                trace.count = TachTraceBefore + 1;
                trace.triggerUs = nowUs;
                trace.referenceUs = previousUs;
            }
        }
        counter.recentUs[counter.recentNext] = intervalUs;
        counter.recentNext = (counter.recentNext + 1) % TachTraceBefore;
        if (counter.recentCount < TachTraceBefore) {
            ++counter.recentCount;
        }
    }
    counter.lastEdgeUs = nowUs;
    counter.hasLastEdge = true;
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
        tach_[fan].windowStartedUs = micros();
        attachInterruptArg(hardware::TachPins[fan], tachInterrupt, &tach_[fan], FALLING);
    }
    analogReadResolution(12);
    for (const uint8_t pin : hardware::SensorPins) {
        pinMode(pin, INPUT);
        analogSetPinAttenuation(pin, ADC_11db);
    }
    lastControl_ = millis();
    lastRpm_ = lastControl_;
    if (hardware::TachDiagnosticsSerial) {
        Serial.println("TACH,fan,window_us,pulses,intervals,min_us,max_us,span_us,"
                       "rpm_count,rpm_period,rpm_display,pwm_pct");
    }
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
    TachWindow windows[FanCount];
    uint32_t windowUs[FanCount];
    TachTrace traces[FanCount];
    CorrectedPeriods corrected[FanCount];
    float rawDisplayRpm[FanCount];
    for (size_t fan = 0; fan < FanCount; ++fan) {
        auto& counter = tach_[fan];
        portENTER_CRITICAL(&counter.mux);
        const uint32_t snapshotUs = micros();
        windows[fan] = counter.window;
        windowUs[fan] = snapshotUs - counter.windowStartedUs;
        counter.window = {};
        counter.windowStartedUs = snapshotUs;
        corrected[fan] = counter.filter.window;
        counter.filter.window = {};
        if (counter.trace.count == TachTraceSize) {
            traces[fan] = counter.trace;
            counter.trace = {};
        }
        // Forget stale edges after an empty window, including across micros() wraps.
        if (windows[fan].pulses == 0) {
            counter.hasLastEdge = false;
            counter.recentCount = 0;
            counter.recentNext = 0;
            counter.trace = {};
            counter.filter = {};
        }
        portEXIT_CRITICAL(&counter.mux);
        rawDisplayRpm[fan] = rpmFromPulses(
            windows[fan].pulses, elapsed, hardware::PulsesPerRevolution[fan]);
        const uint8_t ppr = hardware::PulsesPerRevolution[fan];
        fans_[fan].rpm = corrected[fan].spanUs != 0 && ppr != 0
            ? static_cast<float>(corrected[fan].intervals * 60000000.0 / corrected[fan].spanUs / ppr)
            : rawDisplayRpm[fan];
    }
    lastRpm_ = now;
    const uint64_t sequence = ++tachSequence_;
    auto& snapshot = tachHistory_[(sequence - 1) % TachHistorySize];
    snapshot.sequence = sequence;
    snapshot.uptimeMs = now;
    for (size_t fan = 0; fan < FanCount; ++fan) {
        snapshot.fans[fan] = {
            windows[fan], windowUs[fan], fans_[fan].rpm, fans_[fan].appliedDuty,
            traces[fan], corrected[fan], rawDisplayRpm[fan]};
    }
    // Snapshot every fan before writing serial data, which may block.
    if (hardware::TachDiagnosticsSerial) {
        for (size_t fan = 0; fan < FanCount; ++fan) {
            const auto& window = windows[fan];
            const uint8_t pulsesPerRevolution = hardware::PulsesPerRevolution[fan];
            char countRpm[24] = "NA";
            char periodRpm[24] = "NA";
            if (windowUs[fan] != 0 && pulsesPerRevolution != 0) {
                std::snprintf(countRpm, sizeof(countRpm), "%.2f",
                              window.pulses * 60000000.0 / windowUs[fan] / pulsesPerRevolution);
            }
            if (window.intervals != 0 && window.spanUs != 0 && pulsesPerRevolution != 0) {
                std::snprintf(periodRpm, sizeof(periodRpm), "%.2f",
                              window.intervals * 60000000.0 / window.spanUs / pulsesPerRevolution);
            }
            Serial.printf("TACH,%u,%lu,%lu,%lu,%lu,%lu,%llu,%s,%s,%.2f,%.2f\n",
                          unsigned(fan + 1),
                          static_cast<unsigned long>(windowUs[fan]),
                          static_cast<unsigned long>(window.pulses),
                          static_cast<unsigned long>(window.intervals),
                          static_cast<unsigned long>(window.intervals == 0 ? 0 : window.minUs),
                          static_cast<unsigned long>(window.maxUs),
                          static_cast<unsigned long long>(window.spanUs),
                          countRpm, periodRpm, double(fans_[fan].rpm),
                          double(fans_[fan].appliedDuty));
        }
    }
}

const ThermalController::TachSnapshot* ThermalController::nextTachSnapshot(
    uint64_t after, bool& dropped) const {
    const uint64_t oldest = tachSequence_ < TachHistorySize ? 1 : tachSequence_ - TachHistorySize + 1;
    dropped = after > tachSequence_ || (after < oldest - 1);
    if (after >= tachSequence_ || dropped) {
        return nullptr;
    }
    return &tachHistory_[after % TachHistorySize];
}

void ThermalController::update(uint32_t now, const Config& config) {
    if (now - lastControl_ >= hardware::ControlIntervalMs) {
        readSensors();
        controlFans(now, config);
        lastControl_ = now;
    }
    updateRpm(now);
}

ThermalStatus ThermalController::status(const Config& config) const {
    ThermalStatus result{};
    for (size_t sensor = 0; sensor < SensorCount; ++sensor) {
        result.sensors[sensor] = {
            sensors_[sensor].millivolts,
            sensors_[sensor].temperature,
            safetyActive(sensors_[sensor].temperature)
        };
    }
    for (size_t fan = 0; fan < FanCount; ++fan) {
        result.fans[fan] = {
            fans_[fan].requestedDuty,
            fans_[fan].appliedDuty,
            fans_[fan].rpm,
            config.fans[fan].duty,
            config.fans[fan].automatic,
            fans_[fan].boosting,
            fans_[fan].pwmReady
        };
    }
    return result;
}

}  // namespace controller
