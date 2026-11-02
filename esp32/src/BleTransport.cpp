#include "BleTransport.h"
#include <algorithm>

namespace controller {
namespace {

constexpr char ServiceUuid[] = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
constexpr char RxUuid[] = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
constexpr char TxUuid[] = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";

}  // namespace

void BleTransport::disconnectWithError(const char* message, uint16_t handle) {
    Serial.printf("ERROR: BLE %s; disconnecting client\n", message);
    server_->disconnect(handle);
}

void BleTransport::onConnect(NimBLEServer*, NimBLEConnInfo& info) {
    pairing_.begin(millis(), security_.enrollmentOpen());
    connectionHandle_ = info.getConnHandle();
    subscribed_ = false;
    ++session_;
    connected_ = true;
    if (!security_.canConnect(info)) {
        disconnectWithError("peer not enrolled; use BOOT pairing window", info.getConnHandle());
        return;
    }
    Serial.println("BLE connected; awaiting encrypted bonded session");
}

void BleTransport::onAuthenticationComplete(NimBLEConnInfo& info) {
    if (!pairing_.authenticate([&] { return security_.authorize(info); })) {
        disconnectWithError("bond authorization failed", info.getConnHandle());
    } else {
        Serial.println("BLE: encrypted owner session authorized");
    }
}

void BleTransport::onDisconnect(NimBLEServer*, NimBLEConnInfo&, int reason) {
    connected_ = false;
    pairing_.end();
    subscribed_ = false;
    ++session_;
    Serial.printf("BLE disconnected: %d\n", reason);
    security_.resumeAdvertising();
}

void BleTransport::onSubscribe(NimBLECharacteristic*, NimBLEConnInfo&, uint16_t value) {
    subscribed_ = (value & 1) != 0;
}

void BleTransport::onWrite(NimBLECharacteristic* characteristic, NimBLEConnInfo& info) {
    if (!pairing_.authorized() || !info.isEncrypted()) {
        disconnectWithError("unauthorized RX", info.getConnHandle());
        return;
    }
    if (!subscribed_.load()) {
        disconnectWithError("subscribe to TX before writing RX", info.getConnHandle());
        return;
    }
    const auto value = characteristic->getValue();
    if (value.size() > sizeof(RxPacket::data)) {
        disconnectWithError("RX packet too large", info.getConnHandle());
        return;
    }
    RxPacket packet{};
    packet.session = session_.load();
    packet.length = value.size();
    memcpy(packet.data, value.data(), value.size());
    if (xQueueSend(rxQueue_, &packet, 0) != pdTRUE) {
        disconnectWithError("RX queue full", info.getConnHandle());
    }
}

void BleTransport::begin() {
    rxQueue_ = xQueueCreate(8, sizeof(RxPacket));
    if (rxQueue_ == nullptr) {
        Serial.println("ERROR: BLE queue allocation failed; local fan control remains active");
        return;
    }
    if (!NimBLEDevice::init("ESP32-FanController")) {
        Serial.println("ERROR: BLE initialization failed; local fan control remains active");
        return;
    }
    security_.begin();
    server_ = NimBLEDevice::createServer();
    server_->setCallbacks(this);
    NimBLEService* service = server_->createService(ServiceUuid);
    NimBLECharacteristic* rx = service->createCharacteristic(
        RxUuid, NIMBLE_PROPERTY::WRITE | NIMBLE_PROPERTY::WRITE_ENC, 512);
    tx_ = service->createCharacteristic(TxUuid, NIMBLE_PROPERTY::NOTIFY, 20);
    rx->setCallbacks(this);
    tx_->setCallbacks(this);
    if (!server_->start()) {
        Serial.println("ERROR: BLE server start failed; local fan control remains active");
        return;
    }
    NimBLEAdvertising* advertising = NimBLEDevice::getAdvertising();
    advertising->enableScanResponse(true);
    if (!advertising->addServiceUUID(ServiceUuid) ||
        !advertising->setName("ESP32-FanController")) {
        Serial.println("ERROR: BLE advertising data setup failed; local fan control remains active");
        return;
    }
    if (!advertising->start()) {
        Serial.println("ERROR: BLE advertising failed; local fan control remains active");
    }
}

void BleTransport::resetFrames() {
    input_ = "";
    output_ = "";
    outputOffset_ = 0;
    packetOffset_ = 0;
    currentPacket_.length = 0;
    discardingInput_ = false;
}

void BleTransport::sendNextChunk(uint32_t now) {
    const uint16_t handle = connectionHandle_.load();
    if (!pairing_.authorized() || activeSession_ != session_.load() ||
        !server_->getPeerInfoByHandle(handle).isEncrypted()) {
        resetFrames();
        return;
    }
    if (now - lastTx_ < 20) {
        return;
    }
    const size_t length = std::min(size_t(20), output_.length() - outputOffset_);
    tx_->setValue(reinterpret_cast<const uint8_t*>(output_.c_str() + outputOffset_), length);
    if (!tx_->notify(handle)) {
        output_ = "";
        disconnectWithError("notification failed; response incomplete", handle);
        return;
    }
    outputOffset_ += length;
    lastTx_ = now;
    if (outputOffset_ == output_.length()) {
        output_ = "";
        outputOffset_ = 0;
    }
}

void BleTransport::receiveCommands(uint32_t now) {
    // Bound BLE work so a busy client cannot starve the control loop.
    for (size_t processed = 0; processed < 512; ++processed) {
        if (packetOffset_ >= currentPacket_.length) {
            if (xQueueReceive(rxQueue_, &currentPacket_, 0) != pdTRUE) {
                break;
            }
            packetOffset_ = 0;
            if (currentPacket_.session != activeSession_) {
                currentPacket_.length = 0;
                continue;
            }
        }
        if (packetOffset_ >= currentPacket_.length) {
            continue;
        }
        const char byte = currentPacket_.data[packetOffset_++];
        lastRx_ = now;
        if (byte == '\n') {
            if (!pairing_.authorized() || activeSession_ != session_.load()) {
                resetFrames();
                return;
            }
            if (discardingInput_) {
                discardingInput_ = false;
            } else if (!input_.isEmpty()) {
                output_ = commands_.process(input_);
            } else {
                output_ = CommandProcessor::errorResponse("empty_command", "Expected a JSON command before LF");
            }
            input_ = "";
            if (!output_.isEmpty()) {
                return;
            }
        } else if (!discardingInput_ && byte != '\r') {
            if (input_.length() >= MaxCommandBytes || byte == '\0') {
                input_ = "";
                discardingInput_ = true;
                output_ = CommandProcessor::errorResponse("invalid_frame", "Command exceeds 1024 bytes or contains a NUL");
                return;
            }
            input_ += byte;
        }
    }
    if ((!input_.isEmpty() || discardingInput_) && now - lastRx_ >= 5000) {
        input_ = "";
        discardingInput_ = false;
        output_ = CommandProcessor::errorResponse("frame_timeout", "Incomplete command discarded after 5 seconds; send a new complete line");
    }
}

void BleTransport::update(uint32_t now) {
    if (rxQueue_ == nullptr) {
        return;
    }
    if (security_.update(now)) {
        pairing_.end();
        if (connected_) {
            disconnectWithError("physical bond revocation", connectionHandle_.load());
        } else {
            security_.resumeAdvertising();
        }
    }
    if (connected_ && pairing_.expire([] { return millis(); }, [&] { return security_.enrollmentOpen(); })) {
        disconnectWithError("pairing expired or encryption timeout", connectionHandle_.load());
    }
    const uint32_t currentSession = session_.load();
    if (currentSession != activeSession_ || !connected_.load() || !subscribed_.load() ||
        !pairing_.authorized()) {
        resetFrames();
        activeSession_ = currentSession;
        return;
    }
    if (!output_.isEmpty()) {
        sendNextChunk(now);
    } else {
        receiveCommands(now);
    }
}

}  // namespace controller
