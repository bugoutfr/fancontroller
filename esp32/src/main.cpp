#include <Arduino.h>
#include <esp_system.h>
#include "BleTransport.h"
#include "CommandProcessor.h"
#include "ConfigurationStore.h"
#include "FirmwareOta.h"
#include "ThermalController.h"
#include "FirmwareVersion.h"

namespace {

controller::ConfigurationStore storage;
controller::ThermalController thermal;
controller::BleHealth bleHealth;
controller::FirmwareOta ota;
controller::CommandProcessor commands(storage, thermal, bleHealth, ota);
controller::BleTransport bluetooth(commands, bleHealth);

}  // namespace

void setup() {
    Serial.begin(115200);
    Serial.printf("FanController firmware %s\n", controller::FirmwareVersion);
    thermal.begin();
    storage.begin();
    bluetooth.begin();
    Serial.println("Fan controller ready; PWM 25 kHz, BLE JSON protocol v1");
}

void loop() {
    const uint32_t now = millis();
    thermal.update(now, storage.config());
    bluetooth.update(now);
    if (commands.otaRestartDue(now)) {
        Serial.println("OTA: activation acknowledged; restarting into staged image");
        ESP.restart();
    }
    delay(1);
}
