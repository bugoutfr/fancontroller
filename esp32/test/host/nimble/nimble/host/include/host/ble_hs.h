#pragma once

#define MYNEWT_VAL(name) MYNEWT_VAL_ ## name
#define MYNEWT_VAL_BLE_SM_LEGACY 0
#define MYNEWT_VAL_BLE_SM_SC 1

struct ble_store_status_event { int event_code; };
struct FakeBleConfig {
    int sm_sc_only = 0;
    int (*store_status_cb)(ble_store_status_event*, void*) = nullptr;
};
inline FakeBleConfig ble_hs_cfg;
constexpr int BLE_HS_ENOMEM = 6;
constexpr int BLE_STORE_EVENT_OVERFLOW = 1;
constexpr int BLE_STORE_EVENT_FULL = 2;
