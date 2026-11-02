#pragma once

#include <Arduino.h>
#include <map>

namespace fake {

inline std::map<std::string, String> nvs;
inline bool storageAvailable = true;
inline bool storageWriteOk = true;
inline bool storageRemoveOk = true;

}  // namespace fake

class Preferences {
public:
    bool begin(const char*, bool) { return fake::storageAvailable; }
    bool isKey(const char* key) { return fake::nvs.count(key) != 0; }
    String getString(const char* key) {
        const auto found = fake::nvs.find(key);
        return found == fake::nvs.end() ? String("") : found->second;
    }
    bool remove(const char* key) { return fake::storageRemoveOk && fake::nvs.erase(key) != 0; }
    size_t getString(const char* key, char* buffer, size_t size) {
        const auto& value = fake::nvs.at(key);
        if (value.length() + 1 > size) {
            return 0;
        }
        std::memcpy(buffer, value.c_str(), value.length() + 1);
        return value.length() + 1;
    }
    size_t putString(const char* key, const String& value) {
        if (!fake::storageWriteOk) {
            return 0;
        }
        fake::nvs[key] = value;
        return value.length();
    }
};
