#include "PairingSecurity.h"
#include "HardwareConfig.h"
#include <nimble/nimble/host/include/host/ble_hs.h>

namespace controller {

static_assert(MYNEWT_VAL(BLE_SM_LEGACY) == 0, "Legacy pairing must remain disabled");
static_assert(MYNEWT_VAL(BLE_SM_SC) == 1, "Secure Connections must remain enabled");

String PairingSecurity::identity(const NimBLEConnInfo& info) {
    const auto address = info.getIdAddress();
    char text[24];
    snprintf(text, sizeof(text), "%s/%u", address.toString().c_str(), address.getType());
    return String(text);
}

bool PairingSecurity::begin() {
    pinMode(0, INPUT_PULLUP);
    digitalWrite(hardware::PairingLedPin, LOW);
    pinMode(hardware::PairingLedPin, OUTPUT);
    ready_ = storage_.begin("ble-owner", false);
    if (!ready_) {
        Serial.println("ERROR: pairing NVS unavailable; all BLE access denied");
    } else {
        owner_ = storage_.getString("identity");
    }
    NimBLEDevice::setSecurityAuth(true, false, true);
    NimBLEDevice::setSecurityIOCap(BLE_HS_IO_NO_INPUT_OUTPUT);
    // NimBLE's SC-only mode also requires MITM at ATT level, incompatible with Just Works.
    // Disable legacy at compile time instead; authorization still requires a 16-byte key.
    ble_hs_cfg.sm_sc_only = 0;
    ble_hs_cfg.store_status_cb = [](struct ble_store_status_event* event, void*) {
        // FULL pessimistically counts the in-flight pairing; reject only actual overflow.
        if (event->event_code == BLE_STORE_EVENT_FULL) {
            return 0;
        }
        Serial.println("ERROR: BLE key storage full; refusing eviction of existing bonds");
        return BLE_HS_ENOMEM;
    };
    Serial.println("BLE: BOOT hold 2s then release to pair (60s); 5s then release revokes owner and opens pairing");
    return ready_;
}

bool PairingSecurity::update(uint32_t now) {
    std::lock_guard<std::mutex> guard(mutex_);
    const auto action = button_.update(digitalRead(0) == LOW, now);
    if (action == ButtonAction::Revoke) {
        button_.close();
        connected_ = false;
        const char* failure = nullptr;
        if (!ready_) {
            failure = "pairing storage unavailable";
        } else if (!NimBLEDevice::stopAdvertising()) {
            failure = "cannot stop advertising before deleting keys";
        } else if (!NimBLEDevice::deleteAllBonds() || NimBLEDevice::getNumBonds() != 0) {
            failure = "cannot delete/verify BLE keys";
        } else if ((storage_.isKey("identity") && !storage_.remove("identity")) ||
                   storage_.isKey("identity")) {
            failure = "cannot remove/verify owner identity";
        }
        if (failure != nullptr) {
            ready_ = false;
            Serial.printf("ERROR: bond revocation failed (%s); BLE disabled until restart\n", failure);
        } else {
            owner_ = "";
            button_.open(now);
            revokedAt_ = now;
            revocationFlash_ = true;
            Serial.println("BLE: bonds revoked; enrollment OPEN for 60s (Just Works; no MITM protection); remove old Windows pairing before connecting");
        }
        updateIndicator(now);
        return true;
    }
    if (action == ButtonAction::Enroll) {
        if (!ready_ || !owner_.isEmpty()) {
            Serial.println("BLE: enrollment denied; hold BOOT 5s then release to revoke owner and open pairing");
        } else {
            button_.open(now);
            Serial.println("BLE: enrollment OPEN for 60s (Just Works; no MITM protection)");
        }
    }
    updateIndicator(now);
    return false;
}

void PairingSecurity::updateIndicator(uint32_t now) {
    bool on = ready_ && connected_;
    if (ready_ && owner_.isEmpty() && button_.isOpen(now)) {
        const uint32_t elapsed = now - revokedAt_;
        if (revocationFlash_ && elapsed < 400) {
            on = elapsed < 100 || (elapsed >= 200 && elapsed < 300);
        } else {
            revocationFlash_ = false;
            on = (now / 250) % 2 == 0;
        }
    } else {
        revocationFlash_ = false;
    }
    digitalWrite(hardware::PairingLedPin, on ? HIGH : LOW);
}

void PairingSecurity::setConnected(bool connected) {
    std::lock_guard<std::mutex> guard(mutex_);
    connected_ = connected;
    updateIndicator(millis());
}

bool PairingSecurity::resumeAdvertising() {
    std::lock_guard<std::mutex> guard(mutex_);
    if (!ready_) {
        Serial.println("BLE: advertising not resumed; pairing security unavailable");
        return false;
    }
    if (!NimBLEDevice::startAdvertising()) {
        Serial.println("ERROR: BLE advertising restart failed");
        return false;
    }
    return true;
}

bool PairingSecurity::enrollmentOpen() {
    std::lock_guard<std::mutex> guard(mutex_);
    return ready_ && owner_.isEmpty() && button_.isOpen(millis());
}

bool PairingSecurity::canConnect(const NimBLEConnInfo& info) {
    std::lock_guard<std::mutex> guard(mutex_);
    if (!ready_) {
        return false;
    }
    return (!owner_.isEmpty() && owner_ == identity(info) &&
            NimBLEDevice::isBonded(info.getIdAddress())) ||
           (owner_.isEmpty() && button_.isOpen(millis()));
}

bool PairingSecurity::authorize(const NimBLEConnInfo& info) {
    std::lock_guard<std::mutex> guard(mutex_);
    if (!ready_ || !info.isEncrypted() || !info.isBonded() || info.getSecKeySize() != 16) {
        Serial.println("BLE: encryption/bond verification failed");
        return false;
    }
    const String peer = identity(info);
    if (!owner_.isEmpty()) {
        return owner_ == peer && NimBLEDevice::isBonded(info.getIdAddress());
    }
    if (!button_.isOpen(millis())) {
        if (!NimBLEDevice::deleteBond(info.getIdAddress())) {
            Serial.println("ERROR: cannot remove expired enrollment bond");
        }
        return false;
    }
    if (storage_.putString("identity", peer) != peer.length() ||
        storage_.getString("identity") != peer) {
        ready_ = false;
        Serial.println("ERROR: cannot persist owner; BLE denied");
        return false;
    }
    owner_ = peer;
    button_.close();
    updateIndicator(millis());
    Serial.println("BLE: owner enrolled and stored; enrollment CLOSED");
    return true;
}

}
