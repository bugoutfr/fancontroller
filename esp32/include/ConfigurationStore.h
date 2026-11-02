#pragma once

#include <Preferences.h>
#include "Controller.h"

namespace controller {

class ConfigurationStore {
public:
    void begin();
    bool save();
    void replace(const Config& config);
    const Config& config() const { return config_; }
    bool dirty() const { return dirty_; }
    bool available() const { return ready_; }
    const char* state() const { return state_; }

private:
    static constexpr size_t MaxStoredBytes = 1024;
    Preferences preferences_;
    Config config_ = defaults();
    bool ready_ = false;
    bool dirty_ = false;
    const char* state_ = "defaults";
};

}  // namespace controller
