#pragma once

#include <Arduino.h>
#include "Controller.h"

namespace controller {

class ThermalController {
public:
    struct TachWindow {
        uint32_t pulses = 0;
        uint32_t intervals = 0;
        uint32_t minUs = UINT32_MAX;
        uint32_t maxUs = 0;
        uint64_t spanUs = 0;
    };

    static constexpr size_t TachTraceBefore = 4;
    static constexpr size_t TachTraceSize = 9;

    struct TachTrace {
        uint32_t intervalsUs[TachTraceSize]{};
        uint32_t triggerUs = 0;
        uint32_t referenceUs = 0;
        size_t count = 0;
    };

    struct CorrectedPeriods {
        uint32_t intervals = 0;
        uint32_t mergedPairs = 0;
        uint64_t spanUs = 0;
    };

    struct PeriodFilter {
        uint32_t recentUs[5]{};
        size_t recentCount = 0;
        size_t recentNext = 0;
        uint32_t pendingUs[2]{};
        size_t pendingCount = 0;
        uint32_t referenceUs = 0;
        CorrectedPeriods window;

        void IRAM_ATTR accept(uint32_t intervalUs) {
            ++window.intervals;
            window.spanUs += intervalUs;
            recentUs[recentNext] = intervalUs;
            ++recentNext;
            if (recentNext == 5) {
                recentNext = 0;
            }
            if (recentCount < 5) {
                ++recentCount;
            }
        }

        static bool IRAM_ATTR near(uint64_t intervalUs, uint32_t reference) {
            return intervalUs * 10 >= uint64_t(reference) * 9 &&
                   intervalUs * 10 <= uint64_t(reference) * 11;
        }

        void IRAM_ATTR add(uint32_t intervalUs) {
            if (pendingCount == 2) {
                const uint64_t sum = uint64_t(pendingUs[0]) + pendingUs[1];
                if (near(intervalUs, referenceUs)) {
                    accept(static_cast<uint32_t>(sum));
                    ++window.mergedPairs;
                } else {
                    accept(pendingUs[0]);
                    accept(pendingUs[1]);
                }
                pendingCount = 0;
                accept(intervalUs);
                return;
            }
            if (pendingCount == 1) {
                if (uint64_t(intervalUs) * 4 < uint64_t(referenceUs) * 3 &&
                    uint64_t(pendingUs[0]) + intervalUs <= UINT32_MAX &&
                    near(uint64_t(pendingUs[0]) + intervalUs, referenceUs)) {
                    pendingUs[1] = intervalUs;
                    pendingCount = 2;
                    return;
                }
                accept(pendingUs[0]);
                pendingCount = 0;
                accept(intervalUs);
                return;
            }
            if (recentCount == 5) {
                uint32_t sorted[5];
                for (size_t i = 0; i < 5; ++i) {
                    sorted[i] = recentUs[i];
                }
                for (size_t i = 1; i < 5; ++i) {
                    const uint32_t value = sorted[i];
                    size_t j = i;
                    while (j > 0 && sorted[j - 1] > value) {
                        sorted[j] = sorted[j - 1];
                        --j;
                    }
                    sorted[j] = value;
                }
                referenceUs = sorted[2];
                if (referenceUs != 0 && near(sorted[0], referenceUs) &&
                    near(sorted[4], referenceUs) &&
                    uint64_t(intervalUs) * 4 < uint64_t(referenceUs) * 3) {
                    pendingUs[0] = intervalUs;
                    pendingCount = 1;
                    return;
                }
            }
            accept(intervalUs);
        }
    };

    struct TachReading {
        TachWindow edges;
        uint32_t windowUs = 0;
        float displayRpm = 0;
        float duty = 0;
        TachTrace trace;
        CorrectedPeriods corrected;
        float rawDisplayRpm = 0;
    };

    struct TachSnapshot {
        uint64_t sequence = 0;
        uint32_t uptimeMs = 0;
        TachReading fans[FanCount];
    };

    ThermalController() = default;
    ThermalController(const ThermalController&) = delete;
    ThermalController& operator=(const ThermalController&) = delete;

    void begin();
    void update(uint32_t now, const Config& config);
    ThermalStatus status(const Config& config) const;
    uint64_t tachSequence() const { return tachSequence_; }
    const TachSnapshot* nextTachSnapshot(uint64_t after, bool& dropped) const;

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
        TachWindow window;
        uint32_t windowStartedUs = 0;
        uint32_t lastEdgeUs = 0;
        bool hasLastEdge = false;
        uint32_t recentUs[TachTraceBefore]{};
        size_t recentCount = 0;
        size_t recentNext = 0;
        TachTrace trace;
        PeriodFilter filter;
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
    static constexpr size_t TachHistorySize = 128;
    TachSnapshot tachHistory_[TachHistorySize];
    uint64_t tachSequence_ = 0;
    uint32_t lastControl_ = 0;
    uint32_t lastRpm_ = 0;
};

}  // namespace controller
