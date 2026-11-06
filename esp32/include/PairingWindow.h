#pragma once

#include <cstdint>

namespace controller {

enum class ButtonAction { None, Enroll, Revoke };

class PairingWindow {
public:
    ButtonAction update(bool pressed, uint32_t now) {
        if (!pressed && !armed_) {
            armed_ = true;
        }
        if (!armed_) {
            return ButtonAction::None;
        }
        if (pressed != raw_) {
            raw_ = pressed;
            changedAt_ = now;
        }
        if (now - changedAt_ < 50 || stable_ == raw_) {
            return ButtonAction::None;
        }
        stable_ = raw_;
        if (stable_) {
            pressedAt_ = now;
            return ButtonAction::None;
        }
        const uint32_t duration = now - pressedAt_;
        if (duration >= 5000) {
            return ButtonAction::Revoke;
        }
        return duration >= 2000 ? ButtonAction::Enroll : ButtonAction::None;
    }

    void open(uint32_t now) { openedAt_ = now; open_ = true; }
    void close() { open_ = false; }
    bool isOpen(uint32_t now) const { return open_ && now - openedAt_ < 60000; }

private:
    bool armed_ = false;
    bool raw_ = false;
    bool stable_ = false;
    bool open_ = false;
    uint32_t changedAt_ = 0;
    uint32_t pressedAt_ = 0;
    uint32_t openedAt_ = 0;
};

}
