#pragma once

#include <NimBLEDevice.h>
#include <Preferences.h>
#include <mutex>
#include "PairingWindow.h"

namespace controller {

class PairingSecurity {
public:
    bool begin();
    bool update(uint32_t now);
    bool canConnect(const NimBLEConnInfo& info);
    bool authorize(const NimBLEConnInfo& info);
    bool enrollmentOpen();
    bool resumeAdvertising();
    void setConnected(bool connected);

private:
    static String identity(const NimBLEConnInfo& info);
    void updateIndicator(uint32_t now);
    std::mutex mutex_;
    Preferences storage_;
    PairingWindow button_;
    String owner_;
    bool ready_ = false;
    bool connected_ = false;
    bool revocationFlash_ = false;
    uint32_t revokedAt_ = 0;
};

}
