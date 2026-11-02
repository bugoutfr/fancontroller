#pragma once

#include <atomic>
#include <cstdint>
#include <mutex>

namespace controller {

class PairingSession {
public:
    void begin(uint32_t now, bool enrolling) {
        std::lock_guard<std::mutex> guard(mutex_);
        authorized_ = false;
        expired_ = false;
        connectedAt_ = now;
        enrolling_ = enrolling;
    }

    template <typename Authorize>
    bool authenticate(Authorize authorize) {
        std::lock_guard<std::mutex> guard(mutex_);
        if (expired_) {
            return false;
        }
        authorized_ = authorize();
        return authorized_;
    }

    template <typename Clock, typename EnrollmentOpen>
    bool expire(Clock clock, EnrollmentOpen enrollmentOpen) {
        std::lock_guard<std::mutex> guard(mutex_);
        if (expired_ || authorized_) {
            return false;
        }
        // Sample time here, not before a callback can establish a newer connection.
        if (clock() - connectedAt_ >= 30000 || (enrolling_ && !enrollmentOpen())) {
            expired_ = true;
            return true;
        }
        return false;
    }

    void end() {
        std::lock_guard<std::mutex> guard(mutex_);
        expired_ = true;
        authorized_ = false;
    }

    bool authorized() const { return authorized_.load(); }

private:
    std::mutex mutex_;
    std::atomic<bool> authorized_{false};
    bool expired_ = true;
    bool enrolling_ = false;
    uint32_t connectedAt_ = 0;
};

}  // namespace controller
