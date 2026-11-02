#include <cassert>
#include <cstdio>
#include <limits>
#include <future>
#include "PairingSecurity.h"
#include "PairingSession.h"
#include <nimble/nimble/host/include/host/ble_hs.h>

using namespace controller;

namespace {

void gesture(PairingSecurity& security, uint32_t start, uint32_t duration) {
    fake::buttonLevel = LOW;
    fake::now = start;
    security.update(fake::now);
    fake::now += 60;
    security.update(fake::now);
    fake::buttonLevel = HIGH;
    fake::now += duration;
    security.update(fake::now);
    fake::now += 60;
    security.update(fake::now);
}

void testButton() {
    PairingWindow window;
    assert(window.update(true, 0) == ButtonAction::None);
    assert(window.update(true, 20000) == ButtonAction::None);
    assert(window.update(false, 20100) == ButtonAction::None);
    assert(window.update(true, 21000) == ButtonAction::None);
    assert(window.update(true, 21060) == ButtonAction::None);
    assert(window.update(false, 23100) == ButtonAction::None);
    assert(window.update(false, 23160) == ButtonAction::Enroll);
    const uint32_t nearWrap = std::numeric_limits<uint32_t>::max() - 1000;
    window.open(nearWrap);
    assert(window.isOpen(nearWrap + 59999));
    assert(!window.isOpen(nearWrap + 60000));
    window.close();
    assert(!window.isOpen(0));
}

void testAuthorizationAndRevocation() {
    fake::nvs.clear();
    fake::now = 0;
    fake::buttonLevel = HIGH;
    PairingSecurity security;
    security.begin();
    assert(NimBLEDevice::bonding && NimBLEDevice::sc && !NimBLEDevice::mitm);
    assert(ble_hs_cfg.sm_sc_only == 0);
    ble_store_status_event full{BLE_STORE_EVENT_FULL}, overflow{BLE_STORE_EVENT_OVERFLOW};
    assert(ble_hs_cfg.store_status_cb(&full, nullptr) == 0);
    assert(ble_hs_cfg.store_status_cb(&overflow, nullptr) == BLE_HS_ENOMEM);
    security.update(0);
    NimBLEConnInfo info;
    assert(!security.canConnect(info));
    gesture(security, 100, 2100);
    assert(security.enrollmentOpen());
    assert(security.canConnect(info));
    info.encrypted = false;
    assert(!security.authorize(info));
    info.encrypted = true;
    info.bonded = false;
    assert(!security.authorize(info));
    info.bonded = true;
    info.keySize = 7;
    assert(!security.authorize(info));
    info.keySize = 16;
    assert(security.authorize(info));
    assert(!security.enrollmentOpen());
    assert(security.canConnect(info));
    NimBLEConnInfo stranger;
    stranger.address = NimBLEAddress("aa:bb:cc:dd:ee:ff");
    assert(!security.canConnect(stranger));
    assert(!security.authorize(stranger));
    PairingSecurity rebooted;
    rebooted.begin();
    assert(rebooted.canConnect(info));
    assert(rebooted.authorize(info));
    rebooted.update(fake::now);
    gesture(rebooted, 10000, 2100);
    assert(!rebooted.enrollmentOpen());
    assert(rebooted.resumeAdvertising());
    assert(NimBLEDevice::advertising);
    gesture(rebooted, 15000, 10100);
    assert(!NimBLEDevice::advertising);
    assert(NimBLEDevice::getNumBonds() == 0);
    assert(!rebooted.canConnect(info));
    assert(!fake::nvs.count("identity"));
    assert(rebooted.resumeAdvertising());
    assert(NimBLEDevice::advertising);
    NimBLEDevice::stopAdvertising();
    gesture(rebooted, 30000, 2100);
    assert(rebooted.enrollmentOpen());
    fake::now += 60000;
    assert(!rebooted.canConnect(info));
    const int deletions = NimBLEDevice::deletions;
    assert(!rebooted.authorize(info));
    assert(NimBLEDevice::deletions == deletions + 1);
}

void testRevocationErrors() {
    for (int failure = 0; failure < 4; ++failure) {
        fake::nvs["identity"] = String("11:22:33:44:55:66/0");
        fake::now = 0;
        fake::buttonLevel = HIGH;
        NimBLEDevice::hasBond = true;
        NimBLEDevice::deleteOk = failure != 0;
        NimBLEDevice::stopOk = failure != 1;
        NimBLEDevice::preserveBondOnDelete = failure == 2;
        fake::storageRemoveOk = failure != 3;
        PairingSecurity security;
        security.begin();
        security.update(0);
        assert(security.resumeAdvertising());
        gesture(security, 100, 10100);
        assert(!security.canConnect(NimBLEConnInfo()));
        assert(!security.resumeAdvertising());
        assert(fake::nvs.count("identity"));
    }
    NimBLEDevice::deleteOk = NimBLEDevice::stopOk = fake::storageRemoveOk = true;
    NimBLEDevice::preserveBondOnDelete = false;
}

void testSessionTimeout() {
    PairingSession session;
    assert(!session.authorized());
    // The loop timestamp may precede the callback's connection timestamp.
    const uint32_t loopTime = 100;
    session.begin(loopTime + 1, true);
    assert(!session.expire([] { return 102u; }, [] { return true; }));
    assert(session.authenticate([] { return true; }));
    // Successful enrollment closes the window, but must not expire the session.
    assert(!session.expire([] { return 90000u; }, [] { return false; }));
    assert(session.authorized());
    session.end();
    assert(!session.authorized());
    session.begin(100, false);
    assert(!session.expire([] { return 30099u; }, [] { return false; }));
    assert(session.expire([] { return 30100u; }, [] { return false; }));
    bool called = false;
    assert(!session.authenticate([&] { called = true; return true; }));
    assert(!called);
    session.begin(100, true);
    assert(session.expire([] { return 101u; }, [] { return false; }));
    assert(!session.authorized());
    const uint32_t nearWrap = std::numeric_limits<uint32_t>::max() - 1000;
    session.begin(nearWrap, false);
    assert(!session.expire([&] { return nearWrap + 29999; }, [] { return false; }));
    assert(session.expire([&] { return nearWrap + 30000; }, [] { return false; }));
}

void testAuthenticationAndExpiryAreSerialized() {
    PairingSession session;
    session.begin(0, true);
    std::promise<void> authEntered, finishAuth, expiryStarted;
    auto finish = finishAuth.get_future();
    auto authenticated = std::async(std::launch::async, [&] {
        return session.authenticate([&] {
            authEntered.set_value();
            finish.wait();
            return true;
        });
    });
    authEntered.get_future().wait();
    auto expired = std::async(std::launch::async, [&] {
        expiryStarted.set_value();
        return session.expire([] { return 100u; }, [] { return false; });
    });
    expiryStarted.get_future().wait();
    finishAuth.set_value();
    assert(authenticated.get());
    assert(!expired.get());
    assert(session.authorized());
}

}

int main() {
    testButton();
    testAuthorizationAndRevocation();
    testRevocationErrors();
    testSessionTimeout();
    testAuthenticationAndExpiryAreSerialized();
    std::puts("PASS: BOOT, owner authorization/persistence, revocation with advertising paused and verified, fail-closed errors, fresh clock/wrap, concurrent authentication/expiry");
}
