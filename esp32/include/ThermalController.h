#pragma once

#include <Arduino.h>
#include "Controller.h"

namespace controller {

class ThermalController {
public:
    ThermalController() = default;
    ThermalController(const ThermalController&) = delete;
    ThermalController& operator=(const ThermalController&) = delete;

    void begin();
    void update(uint32_t now, const Config& config);
    ThermalStatus status(const Config& config) const;

private:
    struct SensorState {
        Temperature temperature{0.0f, SensorFault::OutOfRange};
        float millivolts = 0.0f;
    };

    struct FanState {
        float appliedDuty = 100.0f;
        float requestedDuty = 100.0f;
        float rpm = 0.0f;
        bool boosting = false;
        uint32_t boostStarted = 0;
        bool pwmReady = false;
    };

    struct TachCounter {
        volatile uint32_t pulses = 0;
        portMUX_TYPE mux = portMUX_INITIALIZER_UNLOCKED;
    };

    static void IRAM_ATTR tachInterrupt(void* argument);
    void holdFullSpeed(size_t fan);
    void writePwm(size_t fan, float duty);
    void readSensors();
    void controlFans(uint32_t now, const Config& config);
    void updateRpm(uint32_t now);

    SensorState sensors_[SensorCount];
    FanState fans_[FanCount];
    TachCounter tach_[FanCount];
    uint32_t lastControl_ = 0;
    uint32_t lastRpm_ = 0;
};

}  // namespace controller
