#include "BleTransport.h"
#include <algorithm>
#include <cstring>
#include <nimble/nimble/host/include/host/ble_gap.h>

namespace controller {
namespace {

constexpr char ServiceUuid[] = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
constexpr char RxUuid[] = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
constexpr char TxUuid[] = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";
constexpr char OtaUuid[] = "6e400004-b5a3-f393-e0a9-e50e24dcca9e";

}  // namespace

void BleTransport::disconnectWithError(const char* message, uint16_t handle) {
    Serial.printf("ERROR: BLE %s; disconnecting client\n", message);
    server_->disconnect(handle);
}

void BleTransport::onConnect(NimBLEServer*, NimBLEConnInfo& info) {
    security_.setConnected(false);
    pairing_.begin(millis(), security_.enrollmentOpen());
    connectionHandle_ = info.getConnHandle();
    subscribed_ = false;
    otaSubscribed_ = false;
    otaPacketInFlight_ = false;
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
        security_.setConnected(true);
        server_->updateConnParams(info.getConnHandle(), 12, 24, 0, 400);
        server_->setDataLen(info.getConnHandle(), 251);
        Serial.println("BLE: encrypted owner session authorized");
    }
}

void BleTransport::onDisconnect(NimBLEServer*, NimBLEConnInfo&, int reason) {
    security_.setConnected(false);
    connected_ = false;
    pairing_.end();
    subscribed_ = false;
    otaSubscribed_ = false;
    otaSessionCancellationPending_ = true;
    ++session_;
    Serial.printf("BLE disconnected: %d\n", reason);
    if (security_.resumeAdvertising()) {
        health_.state.store(BleHealthState::Ready);
    } else {
        health_.state.store(BleHealthState::AdvertisingRestartFailed);
    }
}

void BleTransport::onMTUChange(uint16_t mtu, NimBLEConnInfo&) {
    const unsigned payload = mtu > 7 ? std::min(unsigned(mtu - 7), 508u) : 0;
    Serial.printf("BLE: negotiated MTU %u; OTA payload up to %u bytes\n", unsigned(mtu), payload);
}

void BleTransport::onSubscribe(NimBLECharacteristic* characteristic, NimBLEConnInfo&, uint16_t value) {
    const bool notifyEnabled = (value & 1) != 0;
    if (characteristic == tx_) {
        subscribed_ = notifyEnabled;
        if (!notifyEnabled) {
            otaSessionCancellationPending_ = true;
        }
    } else if (characteristic == ota_) {
        otaSubscribed_ = notifyEnabled;
        if (!notifyEnabled) {
            otaSessionCancellationPending_ = true;
        }
    }
}

void BleTransport::onWrite(NimBLECharacteristic* characteristic, NimBLEConnInfo& info) {
    if (!pairing_.authorized() || !info.isEncrypted()) {
        disconnectWithError("unauthorized RX", info.getConnHandle());
        return;
    }
    const auto value = characteristic->getValue();
    if (characteristic == ota_) {
        if (!otaSubscribed_.load()) {
            disconnectWithError("subscribe to OTA notifications before writing firmware data", info.getConnHandle());
            return;
        }
        bool expected = false;
        if (!otaPacketInFlight_.compare_exchange_strong(expected, true)) {
            disconnectWithError("multiple OTA packets sent before acknowledgment", info.getConnHandle());
            return;
        }
        if (value.size() > sizeof(OtaRxPacket::data)) {
            otaPacketInFlight_ = false;
            disconnectWithError("OTA packet too large", info.getConnHandle());
            return;
        }
        OtaRxPacket packet{};
        packet.session = session_.load();
        packet.length = value.size();
        memcpy(packet.data, value.data(), value.size());
        if (xQueueSend(otaRxQueue_, &packet, 0) != pdTRUE) {
            otaPacketInFlight_ = false;
            disconnectWithError("OTA queue full", info.getConnHandle());
        }
        return;
    }
    if (!subscribed_.load()) {
        disconnectWithError("subscribe to TX before writing RX", info.getConnHandle());
        return;
    }
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
    otaRxQueue_ = xQueueCreate(1, sizeof(OtaRxPacket));
    if (rxQueue_ == nullptr || otaRxQueue_ == nullptr) {
        health_.state.store(BleHealthState::QueueAllocationFailed);
        Serial.println("ERROR: BLE queue allocation failed; local fan control remains active");
        return;
    }
    if (!NimBLEDevice::init("ESP32-FanController")) {
        health_.state.store(BleHealthState::StackInitializationFailed);
        Serial.println("ERROR: BLE initialization failed; local fan control remains active");
        return;
    }
    if (!NimBLEDevice::setMTU(517)) {
        health_.state.store(BleHealthState::StackInitializationFailed);
        Serial.println("ERROR: BLE MTU configuration failed; local fan control remains active");
        return;
    }
    if (!security_.begin()) {
        health_.state.store(BleHealthState::PairingStorageUnavailable);
        Serial.println("ERROR: BLE pairing storage unavailable; advertising disabled, local fan control remains active");
        return;
    }
    server_ = NimBLEDevice::createServer();
    if (server_ == nullptr) {
        health_.state.store(BleHealthState::ResourceAllocationFailed);
        Serial.println("ERROR: BLE server allocation failed; local fan control remains active");
        return;
    }
    server_->setCallbacks(this);
    NimBLEService* service = server_->createService(ServiceUuid);
    if (service == nullptr) {
        health_.state.store(BleHealthState::ResourceAllocationFailed);
        Serial.println("ERROR: BLE service allocation failed; local fan control remains active");
        return;
    }
    NimBLECharacteristic* rx = service->createCharacteristic(
        RxUuid, NIMBLE_PROPERTY::WRITE | NIMBLE_PROPERTY::WRITE_ENC, 512);
    tx_ = service->createCharacteristic(TxUuid, NIMBLE_PROPERTY::NOTIFY, MaxNotificationBytes);
    ota_ = service->createCharacteristic(
        OtaUuid,
        NIMBLE_PROPERTY::WRITE | NIMBLE_PROPERTY::WRITE_NR | NIMBLE_PROPERTY::WRITE_ENC | NIMBLE_PROPERTY::NOTIFY,
        512);
    if (rx == nullptr || tx_ == nullptr || ota_ == nullptr) {
        health_.state.store(BleHealthState::ResourceAllocationFailed);
        Serial.println("ERROR: BLE characteristic allocation failed; local fan control remains active");
        return;
    }
    rx->setCallbacks(this);
    tx_->setCallbacks(this);
    ota_->setCallbacks(this);
    if (!server_->start()) {
        health_.state.store(BleHealthState::ServerStartFailed);
        Serial.println("ERROR: BLE server start failed; local fan control remains active");
        return;
    }
    NimBLEAdvertising* advertising = NimBLEDevice::getAdvertising();
    if (advertising == nullptr) {
        health_.state.store(BleHealthState::ResourceAllocationFailed);
        Serial.println("ERROR: BLE advertising allocation failed; local fan control remains active");
        return;
    }
    advertising->enableScanResponse(true);
    if (!advertising->addServiceUUID(ServiceUuid) ||
        !advertising->setName("ESP32-FanController")) {
        health_.state.store(BleHealthState::AdvertisingConfigurationFailed);
        Serial.println("ERROR: BLE advertising data setup failed; local fan control remains active");
        return;
    }
    if (!advertising->start()) {
        health_.state.store(BleHealthState::AdvertisingStartFailed);
        Serial.println("ERROR: BLE advertising failed; local fan control remains active");
        return;
    }
    health_.state.store(BleHealthState::Ready);
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
    const uint16_t mtu = server_->getPeerMTU(handle);
    if (mtu < 23) {
        disconnectWithError("invalid peer MTU; response cannot be sent", handle);
        return;
    }
    const size_t payload = std::min(size_t(mtu - 3), size_t(MaxNotificationBytes));
    const size_t length = std::min(payload, output_.length() - outputOffset_);
    tx_->setValue(reinterpret_cast<const uint8_t*>(output_.c_str() + outputOffset_), length);
    const auto& value = tx_->getValue();
    if (value.size() != length ||
        std::memcmp(value.data(), output_.c_str() + outputOffset_, length) != 0) {
        output_ = "";
        disconnectWithError("TX value update failed; response incomplete", handle);
        return;
    }
    if (!tx_->notify(handle)) {
        output_ = "";
        disconnectWithError("notification failed; response incomplete", handle);
        return;
    }
    outputOffset_ += length;
    lastTx_ = now;
    if (outputOffset_ == output_.length()) {
        commands_.otaActivationReplyDrained(now);
        output_ = "";
        outputOffset_ = 0;
    }
}

void BleTransport::receiveOtaPacket(uint32_t now) {
    if (otaRxQueue_ == nullptr || !connected_.load() || !pairing_.authorized() ||
        !otaSubscribed_.load()) {
        return;
    }
    OtaRxPacket packet{};
    if (xQueueReceive(otaRxQueue_, &packet, 0) != pdTRUE) {
        return;
    }
    if (packet.session == session_.load()) {
        const OtaAcknowledgment ack = commands_.processOtaPacket(packet.data, packet.length, now);
        uint8_t bytes[5] = {
            static_cast<uint8_t>(ack.status),
            static_cast<uint8_t>(ack.acceptedOffset),
            static_cast<uint8_t>(ack.acceptedOffset >> 8),
            static_cast<uint8_t>(ack.acceptedOffset >> 16),
            static_cast<uint8_t>(ack.acceptedOffset >> 24)
        };
        const uint16_t handle = connectionHandle_.load();
        ota_->setValue(bytes, sizeof(bytes));
        // A fast client may send the next packet as soon as this notification arrives.
        otaPacketInFlight_ = false;
        if (!ota_->notify(handle)) {
            disconnectWithError("OTA acknowledgment notification failed", handle);
            return;
        }
        return;
    }
    otaPacketInFlight_ = false;
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
                int8_t rssi = 127;
                const int rc = ble_gap_conn_rssi(connectionHandle_.load(), &rssi);
                const bool validRssi = rc == 0 && rssi >= -127 && rssi <= 20;
                if (!validRssi) {
                    Serial.printf("BLE RSSI unavailable: rc=%d, value=%d\n", rc, rssi);
                }
                output_ = commands_.process(input_, validRssi ? &rssi : nullptr, otaSubscribed_.load());
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
    if (rxQueue_ == nullptr || otaRxQueue_ == nullptr) {
        return;
    }
    commands_.updateOta(now);
    if (security_.update(now)) {
        pairing_.end();
        commands_.endOtaSession();
        xQueueReset(otaRxQueue_);
        otaPacketInFlight_ = false;
        if (connected_) {
            disconnectWithError("physical bond revocation", connectionHandle_.load());
        } else {
            if (security_.resumeAdvertising()) {
                health_.state.store(BleHealthState::Ready);
            } else {
                health_.state.store(BleHealthState::AdvertisingRestartFailed);
            }
        }
    }
    if (connected_ && pairing_.expire([] { return millis(); }, [&] { return security_.enrollmentOpen(); })) {
        commands_.endOtaSession();
        xQueueReset(otaRxQueue_);
        otaPacketInFlight_ = false;
        disconnectWithError("pairing expired or encryption timeout", connectionHandle_.load());
    }
    const uint32_t currentSession = session_.load();
    if (currentSession != activeSession_) {
        commands_.endOtaSession();
        xQueueReset(otaRxQueue_);
        otaPacketInFlight_ = false;
        resetFrames();
        activeSession_ = currentSession;
    }
    if (otaSessionCancellationPending_.exchange(false)) {
        commands_.endOtaSession();
        xQueueReset(otaRxQueue_);
        otaPacketInFlight_ = false;
    }
    if (!connected_.load() || !pairing_.authorized()) {
        resetFrames();
        return;
    }
    receiveOtaPacket(now);
    if (!subscribed_.load()) {
        resetFrames();
        return;
    }
    if (!output_.isEmpty()) {
        sendNextChunk(now);
    } else {
        receiveCommands(now);
    }
}

}  // namespace controller
