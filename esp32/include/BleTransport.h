#pragma once

#include <Arduino.h>
#include <NimBLEDevice.h>
#include <atomic>
#include "BleHealth.h"
#include "CommandProcessor.h"
#include "PairingSecurity.h"
#include "PairingSession.h"

namespace controller {

class BleTransport : private NimBLEServerCallbacks, private NimBLECharacteristicCallbacks {
public:
    BleTransport(CommandProcessor& commands, BleHealth& health) : commands_(commands), health_(health) {}
    BleTransport(const BleTransport&) = delete;
    BleTransport& operator=(const BleTransport&) = delete;

    void begin();
    void update(uint32_t now);

private:
    static constexpr size_t MaxCommandBytes = 1024;
    static constexpr uint16_t MaxNotificationBytes = 512;

    struct RxPacket {
        uint32_t session;
        uint16_t length;
        char data[512];
    };

    struct OtaRxPacket {
        uint32_t session;
        uint16_t length;
        uint8_t data[512];
    };

    void onConnect(NimBLEServer*, NimBLEConnInfo& info) override;
    void onDisconnect(NimBLEServer*, NimBLEConnInfo&, int reason) override;
    void onSubscribe(NimBLECharacteristic*, NimBLEConnInfo&, uint16_t value) override;
    void onWrite(NimBLECharacteristic* characteristic, NimBLEConnInfo& info) override;
    void onAuthenticationComplete(NimBLEConnInfo& info) override;
    void onMTUChange(uint16_t mtu, NimBLEConnInfo& info) override;
    void disconnectWithError(const char* message, uint16_t handle);
    void resetFrames();
    void sendNextChunk(uint32_t now);
    void receiveCommands(uint32_t now);
    void receiveOtaPacket(uint32_t now);

    CommandProcessor& commands_;
    BleHealth& health_;
    PairingSecurity security_;
    PairingSession pairing_;
    QueueHandle_t rxQueue_ = nullptr;
    QueueHandle_t otaRxQueue_ = nullptr;
    NimBLECharacteristic* tx_ = nullptr;
    NimBLECharacteristic* ota_ = nullptr;
    NimBLEServer* server_ = nullptr;
    std::atomic<uint32_t> session_{0};
    std::atomic<bool> connected_{false};
    std::atomic<bool> subscribed_{false};
    std::atomic<bool> otaSubscribed_{false};
    std::atomic<bool> otaPacketInFlight_{false};
    std::atomic<bool> otaSessionCancellationPending_{false};
    std::atomic<uint16_t> connectionHandle_{0};
    uint32_t activeSession_ = 0;
    RxPacket currentPacket_{};
    size_t packetOffset_ = 0;
    String input_;
    String output_;
    size_t outputOffset_ = 0;
    bool discardingInput_ = false;
    uint32_t lastRx_ = 0;
    uint32_t lastTx_ = 0;
};

}  // namespace controller
