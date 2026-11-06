#include "FirmwareOta.h"

#include <Arduino.h>
#include <new>
#include <cstring>

#include <esp_err.h>
#include <esp_ota_ops.h>
#include <esp_partition.h>
#include <mbedtls/sha256.h>

namespace controller {
namespace {

constexpr uint32_t TransferTimeoutMs = 30000;
constexpr uint32_t RestartDelayMs = 1000;

OtaResult success() {
    return {true, nullptr, nullptr};
}

OtaResult failure(const char* code, const char* message) {
    return {false, code, message};
}

}  // namespace

struct FirmwareOta::Impl {
    OtaState state = OtaState::Idle;
    const esp_partition_t* partition = nullptr;
    esp_ota_handle_t handle = 0;
    uint32_t expectedSize = 0;
    uint32_t acceptedOffset = 0;
    uint32_t lastActivity = 0;
    uint32_t transferStartedAt = 0;
    uint8_t expectedSha256[32]{};
    mbedtls_sha256_context sha256{};
    bool shaInitialized = false;
    bool activationPending = false;
    bool activationScheduled = false;
    uint32_t restartAt = 0;

    void clearTransfer() {
        if (shaInitialized) {
            mbedtls_sha256_free(&sha256);
            shaInitialized = false;
        }
        state = OtaState::Idle;
        partition = nullptr;
        handle = 0;
        expectedSize = 0;
        acceptedOffset = 0;
        lastActivity = 0;
        transferStartedAt = 0;
        std::memset(expectedSha256, 0, sizeof(expectedSha256));
    }

    void discard(bool abortHandle) {
        if (abortHandle && state == OtaState::Receiving) {
            const esp_err_t error = esp_ota_abort(handle);
            if (error != ESP_OK) {
                Serial.printf("OTA: esp_ota_abort failed: %s\n", esp_err_to_name(error));
            }
        }
        clearTransfer();
    }
};

FirmwareOta::FirmwareOta() : impl_(new (std::nothrow) Impl()) {}

FirmwareOta::~FirmwareOta() {
    if (impl_ != nullptr) {
        impl_->discard(true);
        delete impl_;
    }
}

OtaResult FirmwareOta::info(uint32_t& maxSize, char runningImageSha256[65]) const {
    if (impl_ == nullptr) {
        return failure("ota_unavailable", "OTA resources are unavailable");
    }
    const esp_partition_t* next = esp_ota_get_next_update_partition(nullptr);
    const esp_partition_t* running = esp_ota_get_running_partition();
    if (next == nullptr || running == nullptr) {
        return failure("ota_partition_unavailable", "Running or inactive OTA partition is unavailable");
    }
    uint8_t digest[32];
    const esp_err_t error = esp_partition_get_sha256(running, digest);
    if (error != ESP_OK) {
        return failure("ota_digest_failed", "Running image SHA-256 could not be read");
    }
    maxSize = next->size;
    formatSha256Hex(digest, runningImageSha256);
    return success();
}

OtaResult FirmwareOta::begin(uint32_t size, const char* sha256, uint32_t now) {
    if (impl_ == nullptr) {
        return failure("ota_unavailable", "OTA resources are unavailable");
    }
    if (impl_->state != OtaState::Idle || impl_->activationPending || impl_->activationScheduled) {
        return failure("ota_busy", "An OTA transfer or staged image already exists");
    }
    uint8_t digest[32];
    if (size == 0 || !parseSha256Hex(sha256, digest)) {
        return failure("invalid_ota_request", "Expected a positive size and a 64-character SHA-256 hex digest");
    }
    const esp_partition_t* partition = esp_ota_get_next_update_partition(nullptr);
    if (partition == nullptr || size > partition->size) {
        return failure("ota_size_exceeded", "Image size exceeds the inactive OTA partition");
    }

    esp_ota_handle_t handle = 0;
    const esp_err_t error = esp_ota_begin(partition, size, &handle);
    if (error != ESP_OK) {
        Serial.printf("OTA: esp_ota_begin failed: %s\n", esp_err_to_name(error));
        return failure("ota_begin_failed", "Inactive OTA partition could not be prepared");
    }
    mbedtls_sha256_init(&impl_->sha256);
    if (mbedtls_sha256_starts(&impl_->sha256, 0) != 0) {
        mbedtls_sha256_free(&impl_->sha256);
        esp_ota_abort(handle);
        return failure("ota_hash_failed", "SHA-256 could not be initialized");
    }
    impl_->shaInitialized = true;
    impl_->state = OtaState::Receiving;
    impl_->partition = partition;
    impl_->handle = handle;
    impl_->expectedSize = size;
    impl_->acceptedOffset = 0;
    impl_->lastActivity = now;
    impl_->transferStartedAt = millis();
    std::memcpy(impl_->expectedSha256, digest, sizeof(digest));
    return success();
}

OtaResult FirmwareOta::finish(uint32_t& size, char verifiedSha256[65]) {
    if (impl_ == nullptr || impl_->state != OtaState::Receiving) {
        return failure("ota_not_receiving", "No OTA image is currently being received");
    }
    if (impl_->acceptedOffset != impl_->expectedSize) {
        impl_->discard(true);
        return failure("ota_incomplete", "Received image size does not match ota_begin");
    }
    uint8_t digest[32];
    if (mbedtls_sha256_finish(&impl_->sha256, digest) != 0) {
        impl_->discard(true);
        return failure("ota_hash_failed", "Received image SHA-256 could not be finalized");
    }
    mbedtls_sha256_free(&impl_->sha256);
    impl_->shaInitialized = false;
    if (std::memcmp(digest, impl_->expectedSha256, sizeof(digest)) != 0) {
        impl_->discard(true);
        return failure("ota_hash_mismatch", "Received image SHA-256 does not match ota_begin");
    }

    const esp_err_t error = esp_ota_end(impl_->handle);
    impl_->handle = 0;
    if (error != ESP_OK) {
        impl_->clearTransfer();
        Serial.printf("OTA: esp_ota_end rejected image: %s\n", esp_err_to_name(error));
        return failure("ota_image_invalid", "ESP32 OTA image validation failed");
    }
    size = impl_->acceptedOffset;
    formatSha256Hex(digest, verifiedSha256);
    impl_->state = OtaState::Staged;
    impl_->expectedSize = size;
    impl_->acceptedOffset = size;
    const uint32_t elapsed = millis() - impl_->transferStartedAt;
    Serial.printf("OTA: verified %u bytes in %u ms (%.1f KiB/s)\n",
        unsigned(size), unsigned(elapsed), elapsed == 0 ? 0.0 : size * 1000.0 / elapsed / 1024.0);
    return success();
}

OtaResult FirmwareOta::activate() {
    if (impl_ == nullptr || impl_->state != OtaState::Staged ||
        impl_->activationPending || impl_->activationScheduled) {
        return failure("ota_not_staged", "No validated staged image is ready to activate");
    }
    const esp_err_t error = esp_ota_set_boot_partition(impl_->partition);
    if (error != ESP_OK) {
        Serial.printf("OTA: esp_ota_set_boot_partition failed: %s\n", esp_err_to_name(error));
        return failure("ota_activate_failed", "The staged image could not be selected for boot");
    }
    impl_->activationPending = true;
    return success();
}

OtaResult FirmwareOta::abort() {
    if (impl_ == nullptr || (impl_->state == OtaState::Idle &&
        !impl_->activationPending && !impl_->activationScheduled)) {
        return failure("ota_not_active", "No OTA transfer or staged image can be aborted");
    }
    if (impl_->activationScheduled) {
        return failure("ota_already_activated", "Activation was acknowledged and can no longer be canceled");
    }
    if (impl_->activationPending) {
        const esp_partition_t* running = esp_ota_get_running_partition();
        if (running == nullptr || esp_ota_set_boot_partition(running) != ESP_OK) {
            Serial.println("ERROR: OTA activation reply canceled but the prior boot partition could not be restored");
            impl_->activationPending = false;
            impl_->discard(false);
            return failure("ota_cancel_failed", "Prior boot partition could not be restored");
        }
        impl_->activationPending = false;
    }
    impl_->discard(true);
    return success();
}

OtaAcknowledgment FirmwareOta::processPacket(
    const uint8_t* packet,
    size_t length,
    uint32_t now) {
    if (impl_ == nullptr || impl_->state != OtaState::Receiving) {
        return {OtaPacketStatus::NotReceiving, 0};
    }
    uint32_t nextOffset = impl_->acceptedOffset;
    const OtaPacketStatus validation = validateOtaPacket(
        packet, length, impl_->acceptedOffset, impl_->expectedSize, nextOffset);
    if (validation != OtaPacketStatus::Ok) {
        const uint32_t accepted = impl_->acceptedOffset;
        impl_->discard(true);
        Serial.printf("OTA: rejected binary packet, status=%u, accepted=%u\n",
                      static_cast<unsigned>(validation), accepted);
        return {validation, accepted};
    }
    const size_t payloadLength = length - OtaPacketHeaderSize;
    const uint8_t* payload = packet + OtaPacketHeaderSize;
    const esp_err_t error = esp_ota_write(impl_->handle, payload, payloadLength);
    if (error != ESP_OK) {
        const uint32_t accepted = impl_->acceptedOffset;
        impl_->discard(true);
        Serial.printf("OTA: flash write failed: %s\n", esp_err_to_name(error));
        return {OtaPacketStatus::FlashWriteFailed, accepted};
    }
    if (mbedtls_sha256_update(&impl_->sha256, payload, payloadLength) != 0) {
        const uint32_t accepted = impl_->acceptedOffset;
        impl_->discard(true);
        Serial.println("OTA: SHA-256 update failed");
        return {OtaPacketStatus::FlashWriteFailed, accepted};
    }
    impl_->acceptedOffset = nextOffset;
    impl_->lastActivity = now;
    return {OtaPacketStatus::Ok, impl_->acceptedOffset};
}

void FirmwareOta::update(uint32_t now) {
    if (impl_ != nullptr && impl_->state == OtaState::Receiving &&
        now - impl_->lastActivity >= TransferTimeoutMs) {
        Serial.printf("OTA: transfer timed out after 30 seconds at offset %u\n", impl_->acceptedOffset);
        impl_->discard(true);
    }
}

void FirmwareOta::endSession() {
    if (impl_ == nullptr || impl_->activationScheduled) {
        return;
    }
    if (impl_->activationPending) {
        const esp_partition_t* running = esp_ota_get_running_partition();
        const esp_err_t error = running == nullptr
            ? ESP_ERR_NOT_FOUND
            : esp_ota_set_boot_partition(running);
        if (error != ESP_OK) {
            Serial.printf("ERROR: OTA session ended before activation reply drained; boot restore failed: %s\n",
                          esp_err_to_name(error));
        }
        impl_->activationPending = false;
    }
    if (impl_->state != OtaState::Idle) {
        Serial.println("OTA: discarded transfer or staged image because the authorized session ended");
        impl_->discard(true);
    }
}

bool FirmwareOta::activationReplyPending() const {
    return impl_ != nullptr && impl_->activationPending;
}

void FirmwareOta::activationReplyDrained(uint32_t now) {
    if (impl_ != nullptr && impl_->activationPending) {
        impl_->activationPending = false;
        impl_->activationScheduled = true;
        impl_->restartAt = now + RestartDelayMs;
    }
}

bool FirmwareOta::restartDue(uint32_t now) const {
    return impl_ != nullptr && impl_->activationScheduled &&
        static_cast<int32_t>(now - impl_->restartAt) >= 0;
}

OtaState FirmwareOta::state() const {
    return impl_ == nullptr ? OtaState::Idle : impl_->state;
}

}  // namespace controller
