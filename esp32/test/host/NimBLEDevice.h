#pragma once

#include <Arduino.h>
#include <string>

constexpr int BLE_HS_IO_NO_INPUT_OUTPUT = 3;

class NimBLEAddress {
public:
    explicit NimBLEAddress(std::string value = "11:22:33:44:55:66") : value_(value) {}
    std::string toString() const { return value_; }
    uint8_t getType() const { return 0; }
private:
    std::string value_;
};

class NimBLEConnInfo {
public:
    NimBLEAddress address;
    bool encrypted = true, bonded = true;
    uint8_t keySize = 16;
    NimBLEAddress getIdAddress() const { return address; }
    bool isEncrypted() const { return encrypted; }
    bool isBonded() const { return bonded; }
    uint8_t getSecKeySize() const { return keySize; }
};

class NimBLEDevice {
public:
    static inline bool hasBond = true;
    static inline bool deleteOk = true;
    static inline bool stopOk = true, startOk = true, advertising = false;
    static inline bool preserveBondOnDelete = false;
    static inline int deletions = 0;
    static inline bool bonding = false, mitm = true, sc = false;
    static void setSecurityAuth(bool bond, bool authenticated, bool secureConnections) {
        bonding = bond; mitm = authenticated; sc = secureConnections;
    }
    static void setSecurityIOCap(int) {}
    static bool isBonded(const NimBLEAddress&) { return hasBond; }
    static bool stopAdvertising() {
        if (!stopOk) return false;
        advertising = false;
        return true;
    }
    static bool startAdvertising() {
        if (!startOk) return false;
        advertising = true;
        return true;
    }
    static int getNumBonds() { return hasBond ? 1 : 0; }
    static bool deleteBond(const NimBLEAddress&) {
        ++deletions;
        if (!deleteOk || advertising) return false;
        if (!preserveBondOnDelete) hasBond = false;
        return true;
    }
    static bool deleteAllBonds() { return deleteBond(NimBLEAddress()); }
};
