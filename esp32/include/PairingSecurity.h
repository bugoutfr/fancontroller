#pragma once

#include <NimBLEDevice.h>
#include <Preferences.h>
#include <mutex>
#include "PairingWindow.h"

namespace controller {

class PairingSecurity {
public:
    void begin();
    bool update(uint32_t now);
    bool canConnect(const NimBLEConnInfo& info);
    bool authorize(const NimBLEConnInfo& info);
    bool enrollmentOpen();
    bool resumeAdvertising();

private:
    static String identity(const NimBLEConnInfo& info);
    std::mutex mutex_;
    Preferences storage_;
    PairingWindow button_;
    String owner_;
    bool ready_ = false;
};

}
