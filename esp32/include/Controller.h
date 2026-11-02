#pragma once

#include <cmath>
#include <cstddef>
#include <cstdint>

namespace controller {

constexpr size_t SensorCount = 2;
constexpr size_t FanCount = 4;
constexpr size_t MaxPoints = 8;
constexpr float DividerOhms = 10000.0f;
constexpr float NtcOhms = 10000.0f;
constexpr float Beta = 3950.0f;
constexpr float SupplyMv = 3300.0f;
constexpr float MinimumTemperature = -20.0f;
constexpr float MaximumTemperature = 120.0f;
constexpr float SafetyTemperature = 80.0f;

struct Point {
    float temperature;
    float duty;
};

struct Profile {
    size_t count;
    Point points[MaxPoints];
};

struct FanMode {
    bool automatic;
    float duty;
};

struct Config {
    Profile profiles[SensorCount];
    FanMode fans[FanCount];
};

enum class SensorFault { None, ShortCircuit, OpenCircuit, OutOfRange };

struct Temperature {
    float celsius;
    SensorFault fault;
};

inline Config defaults() {
    Config config{};
    for (auto& profile : config.profiles) {
        profile.count = 5;
        profile.points[0] = {25.0f, 30.0f};
        profile.points[1] = {35.0f, 40.0f};
        profile.points[2] = {45.0f, 60.0f};
        profile.points[3] = {55.0f, 80.0f};
        profile.points[4] = {65.0f, 100.0f};
    }
    for (auto& fan : config.fans) {
        fan = {true, 0.0f};
    }
    return config;
}

inline bool validDuty(float duty) {
    return std::isfinite(duty) && duty >= 0.0f && duty <= 100.0f;
}

inline bool validProfile(const Profile& profile) {
    if (profile.count < 2 || profile.count > MaxPoints) {
        return false;
    }
    for (size_t i = 0; i < profile.count; ++i) {
        const auto& point = profile.points[i];
        if (!std::isfinite(point.temperature) ||
            point.temperature < MinimumTemperature || point.temperature > MaximumTemperature ||
            !validDuty(point.duty)) {
            return false;
        }
        if (i > 0 && (point.temperature <= profile.points[i - 1].temperature ||
                      point.duty < profile.points[i - 1].duty)) {
            return false;
        }
    }
    return true;
}

inline bool validConfig(const Config& config) {
    for (const auto& profile : config.profiles) {
        if (!validProfile(profile)) {
            return false;
        }
    }
    for (const auto& fan : config.fans) {
        if (!validDuty(fan.duty)) {
            return false;
        }
    }
    return true;
}

inline float interpolate(const Profile& profile, float temperature) {
    if (temperature <= profile.points[0].temperature) {
        return profile.points[0].duty;
    }
    for (size_t i = 1; i < profile.count; ++i) {
        const auto& low = profile.points[i - 1];
        const auto& high = profile.points[i];
        if (temperature <= high.temperature) {
            return low.duty + (high.duty - low.duty) *
                (temperature - low.temperature) / (high.temperature - low.temperature);
        }
    }
    return profile.points[profile.count - 1].duty;
}

inline Temperature temperatureFromMv(float millivolts, float offsetC = 0.0f) {
    if (!std::isfinite(millivolts)) {
        return {0.0f, SensorFault::OutOfRange};
    }
    if (millivolts <= 50.0f) {
        return {0.0f, SensorFault::ShortCircuit};
    }
    // The ESP32 ADC saturates below 3.3 V at its highest attenuation.
    if (millivolts >= 3050.0f) {
        return {0.0f, SensorFault::OpenCircuit};
    }
    const float resistance = DividerOhms * millivolts / (SupplyMv - millivolts);
    const float kelvin = 1.0f / (1.0f / 298.15f + std::log(resistance / NtcOhms) / Beta);
    const float celsius = kelvin - 273.15f;
    if (!std::isfinite(celsius) || celsius < MinimumTemperature || celsius > MaximumTemperature) {
        return {0.0f, SensorFault::OutOfRange};
    }
    const float calibrated = celsius + offsetC;
    if (!std::isfinite(calibrated) ||
        calibrated < MinimumTemperature || calibrated > MaximumTemperature) {
        return {0.0f, SensorFault::OutOfRange};
    }
    return {calibrated, SensorFault::None};
}

inline bool safetyActive(const Temperature& temperature) {
    return temperature.fault != SensorFault::None || temperature.celsius >= SafetyTemperature;
}

inline float targetDuty(const Config& config, size_t fan, const Temperature& temperature) {
    if (safetyActive(temperature)) {
        return 100.0f;
    }
    return config.fans[fan].automatic
        ? interpolate(config.profiles[fan / 2], temperature.celsius)
        : config.fans[fan].duty;
}

inline float rpmFromPulses(uint32_t pulses, uint32_t elapsedMs, uint8_t pulsesPerRevolution) {
    return elapsedMs == 0 || pulsesPerRevolution == 0
        ? 0.0f
        : static_cast<float>(pulses) * 60000.0f / elapsedMs / pulsesPerRevolution;
}

}  // namespace controller
