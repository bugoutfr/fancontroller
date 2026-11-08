#pragma once

#include <array>
#include <cassert>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

#define IRAM_ATTR
constexpr int INPUT = 0;
constexpr int INPUT_PULLUP = 2;
constexpr int OUTPUT = 1;
constexpr int LOW = 0;
constexpr int HIGH = 1;
constexpr int FALLING = 2;
constexpr int ADC_11db = 3;

class String {
public:
    String() = default;
    String(const char* value) : value_(value ? value : "") {}
    String& operator=(const char* value) {
        value_ = value ? value : "";
        readOffset_ = 0;
        return *this;
    }
    String& operator+=(char value) {
        value_ += value;
        return *this;
    }
    bool concat(const char* value) {
        value_ += value;
        return true;
    }
    int read() const {
        return readOffset_ < value_.length()
            ? static_cast<unsigned char>(value_[readOffset_++])
            : -1;
    }
    size_t readBytes(char* buffer, size_t length) const {
        const size_t remaining = value_.length() - readOffset_;
        const size_t count = length < remaining ? length : remaining;
        std::memcpy(buffer, value_.data() + readOffset_, count);
        readOffset_ += count;
        return count;
    }
    size_t write(uint8_t value) {
        value_ += static_cast<char>(value);
        return 1;
    }
    size_t write(const uint8_t* value, size_t length) {
        value_.append(reinterpret_cast<const char*>(value), length);
        return length;
    }
    const char* c_str() const { return value_.c_str(); }
    size_t length() const { return value_.length(); }
    bool isEmpty() const { return value_.empty(); }
    bool operator!=(const String& other) const { return value_ != other.value_; }
    bool operator==(const String& other) const { return value_ == other.value_; }

private:
    std::string value_;
    mutable size_t readOffset_ = 0;
};

namespace fake {

inline uint32_t now = 0;
inline uint32_t microseconds = 0;
inline bool useMicroseconds = false;
inline std::array<uint32_t, 40> adc{};
inline std::array<uint32_t, 40> pwm{};
inline std::array<int, 40> levels{};
inline std::array<void (*)(void*), 40> interrupts{};
inline std::array<void*, 40> interruptArguments{};
inline bool pwmWriteOk = true;
inline int buttonLevel = HIGH;

}  // namespace fake

struct FakeSerial {
    std::string output;
    void println(const char* text) { output += std::string(text) + '\n'; }
    template <typename... Args>
    void printf(const char* format, Args... args) {
        const int length = std::snprintf(nullptr, 0, format, args...);
        assert(length >= 0);
        std::string buffer(static_cast<size_t>(length) + 1, '\0');
        std::snprintf(buffer.data(), buffer.size(), format, args...);
        output.append(buffer.data(), static_cast<size_t>(length));
    }
};

inline FakeSerial Serial;
inline uint32_t millis() { return fake::now; }
inline uint32_t micros() {
    return fake::useMicroseconds ? fake::microseconds : fake::now * 1000U;
}
inline void pinMode(uint8_t, int) {}
inline int digitalRead(uint8_t) { return fake::buttonLevel; }
inline void digitalWrite(uint8_t pin, int value) { fake::levels.at(pin) = value; }
inline void analogReadResolution(int) {}
inline void analogSetPinAttenuation(uint8_t, int) {}
inline uint32_t analogReadMilliVolts(uint8_t pin) { return fake::adc.at(pin); }
inline bool ledcAttachChannel(uint8_t, uint32_t, uint8_t, size_t) { return true; }
inline void ledcDetach(uint8_t) {}
inline bool ledcWrite(uint8_t pin, uint32_t value) {
    if (!fake::pwmWriteOk) {
        return false;
    }
    fake::pwm.at(pin) = value;
    return true;
}
inline void attachInterruptArg(uint8_t pin, void (*callback)(void*), void* argument, int) {
    fake::interrupts.at(pin) = callback;
    fake::interruptArguments.at(pin) = argument;
}

struct portMUX_TYPE {};
#define portMUX_INITIALIZER_UNLOCKED {}
inline void portENTER_CRITICAL(portMUX_TYPE*) {}
inline void portEXIT_CRITICAL(portMUX_TYPE*) {}
inline void portENTER_CRITICAL_ISR(portMUX_TYPE*) {}
inline void portEXIT_CRITICAL_ISR(portMUX_TYPE*) {}
