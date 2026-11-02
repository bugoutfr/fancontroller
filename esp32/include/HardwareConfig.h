#pragma once

#include "Controller.h"

namespace controller::hardware {

constexpr uint8_t PwmPins[FanCount] = {15, 16, 17, 18};
constexpr uint8_t TachPins[FanCount] = {13, 34, 14, 27};
constexpr uint8_t SensorPins[SensorCount] = {33, 32};
constexpr float TemperatureOffsetC = -0.7f;
constexpr uint8_t PulsesPerRevolution[FanCount] = {2, 2, 2, 2};
constexpr uint32_t PwmFrequency = 25000;
constexpr uint8_t PwmBits = 10;
constexpr bool PwmInverted = false;
constexpr uint32_t ControlIntervalMs = 250;
constexpr uint32_t RpmIntervalMs = 1000;
constexpr uint32_t StartupBoostMs = 1000;

}  // namespace controller::hardware
