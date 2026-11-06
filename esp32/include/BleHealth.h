#pragma once

#include <atomic>

namespace controller {

enum class BleHealthState {
    Starting,
    Ready,
    QueueAllocationFailed,
    StackInitializationFailed,
    PairingStorageUnavailable,
    ResourceAllocationFailed,
    ServerStartFailed,
    AdvertisingConfigurationFailed,
    AdvertisingStartFailed,
    AdvertisingRestartFailed
};

struct BleHealth {
    std::atomic<BleHealthState> state{BleHealthState::Starting};
};

}  // namespace controller
