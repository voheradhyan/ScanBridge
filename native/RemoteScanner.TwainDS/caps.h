// caps.h — TWAIN capability container marshalling and the data source's capability store.
//
// Applications probe capabilities aggressively (MSG_GET, MSG_GETCURRENT, MSG_GETDEFAULT,
// MSG_QUERYSUPPORT, MSG_RESET) and reject data sources that answer inconsistently, so this
// is a real implementation rather than a lookup table: every capability is backed by the
// values the *physical* scanner reported over the channel.

#ifndef RS_TWAINDS_CAPS_H
#define RS_TWAINDS_CAPS_H

#include <windows.h>
#include <map>
#include <vector>
#include <algorithm>

#include "../include/rs_twain.h"
#include "../include/rs_protocol.h"

namespace rs {

// ------------------------------------------------------------ memory plumbing

/// TWAIN 2.x hands the data source the DSM's allocator via DAT_ENTRYPOINT — but only to a
/// source that declared itself 2.x. We report TWAIN 1.9, because the manager asks who we are
/// with a zeroed identity during its scan and there is nothing to adapt to at that moment, so
/// DAT_ENTRYPOINT never arrives and the global heap is the normal path rather than a fallback.
/// Mixing the two would free with the wrong allocator, so it is centralised here.
struct DsmMemory {
    DSM_MEMALLOCATE allocate = nullptr;
    DSM_MEMFREE     freeMem = nullptr;
    DSM_MEMLOCK     lock = nullptr;
    DSM_MEMUNLOCK   unlock = nullptr;

    TW_HANDLE alloc(TW_UINT32 size) const {
        return allocate ? allocate(size) : GlobalAlloc(GHND, size);
    }
    void release(TW_HANDLE handle) const {
        if (!handle) return;
        if (freeMem) freeMem(handle); else GlobalFree(static_cast<HGLOBAL>(handle));
    }
    void* acquire(TW_HANDLE handle) const {
        if (!handle) return nullptr;
        return lock ? lock(handle) : GlobalLock(static_cast<HGLOBAL>(handle));
    }
    void relinquish(TW_HANDLE handle) const {
        if (!handle) return;
        if (unlock) unlock(handle); else GlobalUnlock(static_cast<HGLOBAL>(handle));
    }
};

// ------------------------------------------------------------------- TW_FIX32

inline TW_FIX32 toFix32(double value) {
    TW_FIX32 fix{};
    const bool negative = value < 0;
    const int32_t scaled = static_cast<int32_t>(value * 65536.0 + (negative ? -0.5 : 0.5));
    fix.Whole = static_cast<TW_INT16>(scaled >> 16);
    fix.Frac = static_cast<TW_UINT16>(scaled & 0xFFFF);
    return fix;
}

inline double fromFix32(TW_FIX32 fix) {
    return static_cast<double>(fix.Whole) + static_cast<double>(fix.Frac) / 65536.0;
}

/// Fix32 values live in the capability store as their raw 32-bit pattern.
inline uint32_t packFix32(double value) {
    TW_FIX32 fix = toFix32(value);
    return (static_cast<uint32_t>(static_cast<uint16_t>(fix.Whole)) << 16) | fix.Frac;
}

inline double unpackFix32(uint32_t packed) {
    TW_FIX32 fix{};
    fix.Whole = static_cast<TW_INT16>(static_cast<int16_t>(packed >> 16));
    fix.Frac = static_cast<TW_UINT16>(packed & 0xFFFF);
    return fromFix32(fix);
}

inline size_t itemSize(TW_UINT16 type) {
    switch (type) {
        case TWTY_INT8:
        case TWTY_UINT8:  return 1;
        case TWTY_INT16:
        case TWTY_UINT16:
        case TWTY_BOOL:   return 2;
        case TWTY_INT32:
        case TWTY_UINT32:
        case TWTY_FIX32:  return 4;
        case TWTY_FRAME:  return sizeof(TW_FRAME);
        case TWTY_STR32:  return sizeof(TW_STR32);
        case TWTY_STR64:  return sizeof(TW_STR64);
        case TWTY_STR128: return sizeof(TW_STR128);
        case TWTY_STR255: return sizeof(TW_STR255);
        case TWTY_HANDLE: return sizeof(TW_HANDLE);
        default:          return 0;
    }
}

/// Writes one item of `type` from a 32-bit store value. Only the fixed-width numeric types
/// are ever stored this way; string capabilities are handled separately.
inline void writeItem(uint8_t* dest, TW_UINT16 type, uint32_t value) {
    switch (type) {
        case TWTY_INT8:
        case TWTY_UINT8:  *dest = static_cast<uint8_t>(value); break;
        case TWTY_INT16:
        case TWTY_UINT16:
        case TWTY_BOOL: { uint16_t v = static_cast<uint16_t>(value); memcpy(dest, &v, 2); break; }
        default:          memcpy(dest, &value, 4); break;
    }
}

inline uint32_t readItem(const uint8_t* src, TW_UINT16 type) {
    switch (type) {
        case TWTY_INT8:   return static_cast<uint32_t>(static_cast<int32_t>(*reinterpret_cast<const int8_t*>(src)));
        case TWTY_UINT8:  return *src;
        case TWTY_INT16: { int16_t v; memcpy(&v, src, 2); return static_cast<uint32_t>(static_cast<int32_t>(v)); }
        case TWTY_UINT16:
        case TWTY_BOOL:  { uint16_t v; memcpy(&v, src, 2); return v; }
        default:         { uint32_t v; memcpy(&v, src, 4); return v; }
    }
}

// ------------------------------------------------------------ container build

class ContainerBuilder {
public:
    explicit ContainerBuilder(const DsmMemory& memory) : memory_(memory) {}

    TW_HANDLE oneValue(TW_UINT16 type, uint32_t value) const {
        const size_t size = offsetof(TW_ONEVALUE, Item) + (std::max)(itemSize(type), sizeof(TW_UINT32));
        TW_HANDLE handle = memory_.alloc(static_cast<TW_UINT32>(size));
        if (!handle) return nullptr;

        auto* raw = static_cast<uint8_t*>(memory_.acquire(handle));
        if (!raw) { memory_.release(handle); return nullptr; }

        ZeroMemory(raw, size);
        auto* container = reinterpret_cast<pTW_ONEVALUE>(raw);
        container->ItemType = type;
        writeItem(raw + offsetof(TW_ONEVALUE, Item), type, value);

        memory_.relinquish(handle);
        return handle;
    }

    TW_HANDLE enumeration(TW_UINT16 type, const std::vector<uint32_t>& values,
                          uint32_t currentIndex, uint32_t defaultIndex) const {
        const size_t unit = itemSize(type);
        if (unit == 0 || values.empty()) return nullptr;

        const size_t size = offsetof(TW_ENUMERATION, ItemList) + unit * values.size();
        TW_HANDLE handle = memory_.alloc(static_cast<TW_UINT32>(size));
        if (!handle) return nullptr;

        auto* raw = static_cast<uint8_t*>(memory_.acquire(handle));
        if (!raw) { memory_.release(handle); return nullptr; }

        ZeroMemory(raw, size);
        auto* container = reinterpret_cast<pTW_ENUMERATION>(raw);
        container->ItemType = type;
        container->NumItems = static_cast<TW_UINT32>(values.size());
        container->CurrentIndex = currentIndex;
        container->DefaultIndex = defaultIndex;

        uint8_t* items = raw + offsetof(TW_ENUMERATION, ItemList);
        for (size_t i = 0; i < values.size(); ++i)
            writeItem(items + i * unit, type, values[i]);

        memory_.relinquish(handle);
        return handle;
    }

    TW_HANDLE range(TW_UINT16 type, uint32_t minimum, uint32_t maximum, uint32_t step,
                    uint32_t defaultValue, uint32_t currentValue) const {
        TW_HANDLE handle = memory_.alloc(sizeof(TW_RANGE));
        if (!handle) return nullptr;

        auto* container = static_cast<pTW_RANGE>(memory_.acquire(handle));
        if (!container) { memory_.release(handle); return nullptr; }

        container->ItemType = type;
        container->MinValue = minimum;
        container->MaxValue = maximum;
        container->StepSize = step;
        container->DefaultValue = defaultValue;
        container->CurrentValue = currentValue;

        memory_.relinquish(handle);
        return handle;
    }

    TW_HANDLE array(TW_UINT16 type, const std::vector<uint32_t>& values) const {
        const size_t unit = itemSize(type);
        if (unit == 0) return nullptr;

        const size_t size = offsetof(TW_ARRAY, ItemList) + unit * (std::max)(values.size(), size_t{ 1 });
        TW_HANDLE handle = memory_.alloc(static_cast<TW_UINT32>(size));
        if (!handle) return nullptr;

        auto* raw = static_cast<uint8_t*>(memory_.acquire(handle));
        if (!raw) { memory_.release(handle); return nullptr; }

        ZeroMemory(raw, size);
        auto* container = reinterpret_cast<pTW_ARRAY>(raw);
        container->ItemType = type;
        container->NumItems = static_cast<TW_UINT32>(values.size());

        uint8_t* items = raw + offsetof(TW_ARRAY, ItemList);
        for (size_t i = 0; i < values.size(); ++i)
            writeItem(items + i * unit, type, values[i]);

        memory_.relinquish(handle);
        return handle;
    }

private:
    const DsmMemory& memory_;
};

// ------------------------------------------------------------ capability store

struct Capability {
    TW_UINT16 id = 0;
    TW_UINT16 type = TWTY_UINT16;
    uint32_t current = 0;
    uint32_t defaultValue = 0;

    /// Discrete supported values. When empty the capability is a range.
    std::vector<uint32_t> values;

    bool isRange = false;
    uint32_t rangeMin = 0, rangeMax = 0, rangeStep = 1;

    bool readOnly = false;

    /// Set when the application has changed the value in state 4; some applications check
    /// that MSG_RESET actually restores the default.
    bool modified = false;

    bool allows(uint32_t candidate) const {
        if (isRange) {
            // Ranges of FIX32 are compared as signed reals, not as raw bit patterns.
            if (type == TWTY_FIX32) {
                const double v = unpackFix32(candidate);
                return v >= unpackFix32(rangeMin) - 1e-9 && v <= unpackFix32(rangeMax) + 1e-9;
            }
            return candidate >= rangeMin && candidate <= rangeMax;
        }
        return std::find(values.begin(), values.end(), candidate) != values.end();
    }

    uint32_t currentIndex() const {
        auto it = std::find(values.begin(), values.end(), current);
        return it == values.end() ? 0 : static_cast<uint32_t>(it - values.begin());
    }

    uint32_t defaultIndex() const {
        auto it = std::find(values.begin(), values.end(), defaultValue);
        return it == values.end() ? 0 : static_cast<uint32_t>(it - values.begin());
    }

    TW_UINT16 querySupport() const {
        TW_UINT16 flags = TWQC_GET | TWQC_GETCURRENT | TWQC_GETDEFAULT;
        if (!readOnly) flags |= TWQC_SET | TWQC_RESET;
        return flags;
    }
};

class CapabilityStore {
public:
    void defineEnum(TW_UINT16 id, TW_UINT16 type, std::vector<uint32_t> values,
                    uint32_t defaultValue, bool readOnly = false) {
        Capability cap;
        cap.id = id;
        cap.type = type;
        cap.values = std::move(values);
        cap.defaultValue = defaultValue;
        cap.current = defaultValue;
        cap.readOnly = readOnly;
        caps_[id] = std::move(cap);
    }

    void defineRange(TW_UINT16 id, TW_UINT16 type, uint32_t minimum, uint32_t maximum,
                     uint32_t step, uint32_t defaultValue, bool readOnly = false) {
        Capability cap;
        cap.id = id;
        cap.type = type;
        cap.isRange = true;
        cap.rangeMin = minimum;
        cap.rangeMax = maximum;
        cap.rangeStep = step;
        cap.defaultValue = defaultValue;
        cap.current = defaultValue;
        cap.readOnly = readOnly;
        caps_[id] = std::move(cap);
    }

    void defineBool(TW_UINT16 id, bool defaultValue, bool readOnly = false) {
        defineEnum(id, TWTY_BOOL, { 0, 1 }, defaultValue ? 1u : 0u, readOnly);
    }

    void defineSingle(TW_UINT16 id, TW_UINT16 type, uint32_t value, bool readOnly = true) {
        defineEnum(id, type, { value }, value, readOnly);
    }

    Capability* find(TW_UINT16 id) {
        auto it = caps_.find(id);
        return it == caps_.end() ? nullptr : &it->second;
    }

    const Capability* find(TW_UINT16 id) const {
        auto it = caps_.find(id);
        return it == caps_.end() ? nullptr : &it->second;
    }

    bool has(TW_UINT16 id) const { return caps_.count(id) != 0; }

    uint32_t current(TW_UINT16 id, uint32_t fallback = 0) const {
        const Capability* cap = find(id);
        return cap ? cap->current : fallback;
    }

    bool currentBool(TW_UINT16 id, bool fallback = false) const {
        const Capability* cap = find(id);
        return cap ? cap->current != 0 : fallback;
    }

    double currentFix32(TW_UINT16 id, double fallback = 0.0) const {
        const Capability* cap = find(id);
        return cap ? unpackFix32(cap->current) : fallback;
    }

    void resetAll() {
        for (auto& entry : caps_) {
            entry.second.current = entry.second.defaultValue;
            entry.second.modified = false;
        }
    }

    /// CAP_SUPPORTEDCAPS must list every capability we answer to, in ascending order.
    std::vector<uint32_t> supportedList() const {
        std::vector<uint32_t> ids;
        ids.reserve(caps_.size());
        for (const auto& entry : caps_) ids.push_back(entry.first);
        std::sort(ids.begin(), ids.end());
        return ids;
    }

    void clear() { caps_.clear(); }

private:
    std::map<TW_UINT16, Capability> caps_;
};

}  // namespace rs

#endif  // RS_TWAINDS_CAPS_H
