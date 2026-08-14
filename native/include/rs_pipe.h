// rs_pipe.h — framed named-pipe client plus the crypto helpers the native side needs.
//
// Used by the virtual TWAIN data source (to reach the per-session agent) and by the DVC
// plugin (to reach the local tray agent). Blocking with explicit timeouts, because TWAIN
// calls into the data source synchronously and an async model would buy nothing there.

#ifndef RS_PIPE_H
#define RS_PIPE_H

#include <windows.h>
#include <bcrypt.h>
#include <wincrypt.h>
#include <sddl.h>       // ConvertSidToStringSidW — naming the hive we read, rather than assuming
#include <atomic>
#include <chrono>
#include <shared_mutex>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <stdexcept>

#include "rs_protocol.h"
#include "rs_log.h"

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "advapi32.lib")

namespace rs {

class PipeError : public std::runtime_error {
public:
    explicit PipeError(const std::string& what, DWORD code = 0)
        : std::runtime_error(what), code_(code) {}
    DWORD code() const { return code_; }
private:
    DWORD code_;
};

// ------------------------------------------------------------------- crypto

inline std::vector<uint8_t> hmacSha256(const std::vector<uint8_t>& key,
                                       const std::vector<uint8_t>& message) {
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    std::vector<uint8_t> result(32);

    NTSTATUS status = BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr,
                                                  BCRYPT_ALG_HANDLE_HMAC_FLAG);
    if (status != 0) throw PipeError("BCryptOpenAlgorithmProvider failed", static_cast<DWORD>(status));

    status = BCryptCreateHash(alg, &hash, nullptr, 0,
                              const_cast<PUCHAR>(key.data()), static_cast<ULONG>(key.size()), 0);
    if (status != 0) {
        BCryptCloseAlgorithmProvider(alg, 0);
        throw PipeError("BCryptCreateHash failed", static_cast<DWORD>(status));
    }

    if (!message.empty())
        BCryptHashData(hash, const_cast<PUCHAR>(message.data()), static_cast<ULONG>(message.size()), 0);
    BCryptFinishHash(hash, result.data(), static_cast<ULONG>(result.size()), 0);

    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(alg, 0);
    return result;
}

/// Short, non-reversible tag for a secret, safe to log. Mirrors SecretStore.Fingerprint on the
/// managed side so the two can be compared by eye across a client log and an agent log.
inline std::string secretFingerprint(const std::vector<uint8_t>& secret) {
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    uint8_t digest[32]{};

    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0)
        return "unknown";

    std::string result = "unknown";
    if (BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) == 0) {
        BCryptHashData(hash, const_cast<PUCHAR>(secret.data()), static_cast<ULONG>(secret.size()), 0);
        BCryptFinishHash(hash, digest, sizeof(digest), 0);
        BCryptDestroyHash(hash);

        char text[9]{};
        for (int i = 0; i < 4; ++i) sprintf_s(text + i * 2, 3, "%02X", digest[i]);
        result = text;
    }

    BCryptCloseAlgorithmProvider(alg, 0);
    return result;
}

inline std::vector<uint8_t> randomBytes(size_t count) {
    std::vector<uint8_t> out(count);
    NTSTATUS status = BCryptGenRandom(nullptr, out.data(), static_cast<ULONG>(out.size()),
                                      BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    if (status != 0) throw PipeError("BCryptGenRandom failed", static_cast<DWORD>(status));
    return out;
}

/// Constant-time comparison. Never compare a MAC with memcmp.
inline bool fixedTimeEquals(const std::vector<uint8_t>& a, const std::vector<uint8_t>& b) {
    if (a.size() != b.size()) return false;
    uint8_t diff = 0;
    for (size_t i = 0; i < a.size(); ++i) diff |= static_cast<uint8_t>(a[i] ^ b[i]);
    return diff == 0;
}

/// Reads a DPAPI-protected value from HKCU and unprotects it with the current user's key.
/// Only the user who wrote it can read it back, which is what scopes the secret to a session.
/// Reads the secret on a thread of our own making, never on the caller's.
///
/// HKEY_CURRENT_USER and DPAPI both resolve against the *thread* token, not the process token.
/// This code is loaded into mstsc.exe, which does credential work — CredSSP, saved credentials,
/// network-level authentication — and a thread that is impersonating while this runs would open
/// a different user's hive and unprotect with a different user's key. The result is not an error
/// but a *different valid secret*, and the only symptom is that the tray agent rejects the link
/// as a shared-secret mismatch, intermittently, depending on connection timing.
///
/// A freshly created thread starts with the process token and no impersonation, so reading there
/// is deterministic regardless of what the host process was doing.
inline std::vector<uint8_t> loadProtectedSecretOnCleanThread(
    const std::wstring& subKey, const std::wstring& valueName);

/// The SID of the user this process actually runs as, as text. Empty if it cannot be found.
inline std::wstring processUserSid() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return {};

    DWORD size = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    if (size == 0) { CloseHandle(token); return {}; }

    std::vector<uint8_t> buffer(size);
    if (!GetTokenInformation(token, TokenUser, buffer.data(), size, &size)) {
        CloseHandle(token);
        return {};
    }
    CloseHandle(token);

    auto* user = reinterpret_cast<TOKEN_USER*>(buffer.data());
    LPWSTR text = nullptr;
    if (!ConvertSidToStringSidW(user->User.Sid, &text)) return {};

    std::wstring sid = text;
    LocalFree(text);
    return sid;
}

/// Opens this user's registry hive, without going through HKEY_CURRENT_USER.
///
/// HKEY_CURRENT_USER is not a fixed handle: Windows resolves it once per process and caches
/// the result, and a process that impersonates somebody — as mstsc.exe does while handling
/// credentials — can end up with it bound to a different hive for the rest of its life. Loaded
/// into such a process, this code read a *valid but different* secret than every other
/// component on the same PC, so the tray agent refused the link, the virtual channel had no
/// listener, and scanning was dead with no error that pointed anywhere near the cause.
///
/// Opening HKEY_USERS\<our SID> asks for exactly one hive by name and cannot be redirected.
/// RegOpenCurrentUser is the documented second choice; plain HKEY_CURRENT_USER is the last,
/// and only so this never fails outright in some context neither of the others handles.
inline HKEY openUserHive(bool* viaSid = nullptr) {
    if (viaSid) *viaSid = false;

    const std::wstring sid = processUserSid();
    if (!sid.empty()) {
        HKEY hive{};
        if (RegOpenKeyExW(HKEY_USERS, sid.c_str(), 0, KEY_READ, &hive) == ERROR_SUCCESS) {
            if (viaSid) *viaSid = true;
            return hive;
        }
    }

    // Falling back is not a quiet detail. The fallbacks are the paths that produced a valid but
    // wrong secret in the first place, so if we are on one, the log says so before anything
    // goes wrong rather than after.
    HKEY current{};
    if (RegOpenCurrentUser(KEY_READ, &current) == ERROR_SUCCESS) {
        RS_LOG_WARN("could not open this user's registry hive by SID; using RegOpenCurrentUser. "
                    "If the shared secret is refused, this is why.");
        return current;
    }

    RS_LOG_WARN("could not open this user's registry hive by SID or by RegOpenCurrentUser; "
                "falling back to HKEY_CURRENT_USER, which a host process can have cached "
                "against the wrong user.");
    return HKEY_CURRENT_USER;
}

inline void closeUserHive(HKEY hive) {
    if (hive && hive != HKEY_CURRENT_USER) RegCloseKey(hive);
}

inline std::vector<uint8_t> loadProtectedSecret(const std::wstring& subKey, const std::wstring& valueName) {
    bool viaSid = false;
    HKEY hive = openUserHive(&viaSid);

    HKEY key{};
    const LONG opened = RegOpenKeyExW(hive, subKey.c_str(), 0, KEY_READ, &key);
    closeUserHive(hive);

    if (opened != ERROR_SUCCESS)
        throw PipeError("shared secret not present; is the ScanBridge agent running in this session?");

    RS_LOG_DEBUG("reading the shared secret from %S (hive resolved %s)",
                 subKey.c_str(), viaSid ? "by SID" : "through the process default");

    DWORD size = 0, type = 0;
    LONG rc = RegQueryValueExW(key, valueName.c_str(), nullptr, &type, nullptr, &size);
    if (rc != ERROR_SUCCESS || type != REG_BINARY || size == 0) {
        RegCloseKey(key);
        throw PipeError("shared secret value is missing or malformed");
    }

    std::vector<uint8_t> protectedBlob(size);
    rc = RegQueryValueExW(key, valueName.c_str(), nullptr, &type, protectedBlob.data(), &size);
    RegCloseKey(key);
    if (rc != ERROR_SUCCESS) throw PipeError("failed to read shared secret");

    DATA_BLOB in{ static_cast<DWORD>(protectedBlob.size()), protectedBlob.data() };
    DATA_BLOB out{};
    if (!CryptUnprotectData(&in, nullptr, nullptr, nullptr, nullptr, 0, &out))
        throw PipeError("CryptUnprotectData failed on the shared secret", GetLastError());

    std::vector<uint8_t> secret(out.pbData, out.pbData + out.cbData);
    SecureZeroMemory(out.pbData, out.cbData);
    LocalFree(out.pbData);
    return secret;
}

/// True when the calling thread is impersonating somebody. Logged rather than acted on: it
/// explains a whole class of "it works on my machine, intermittently" faults in one line.
inline bool threadIsImpersonating() {
    HANDLE token = nullptr;
    if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &token)) return false;
    CloseHandle(token);
    return true;
}

inline std::vector<uint8_t> loadProtectedSecretOnCleanThread(
    const std::wstring& subKey, const std::wstring& valueName) {

    std::vector<uint8_t> secret;
    std::string failure;

    std::thread worker([&] {
        try { secret = loadProtectedSecret(subKey, valueName); }
        catch (const std::exception& ex) { failure = ex.what(); }
    });
    worker.join();

    if (!failure.empty()) throw PipeError(failure);
    return secret;
}

// -------------------------------------------------------------- pipe client

/// Framed client for one named-pipe connection.
///
/// The handle is opened with FILE_FLAG_OVERLAPPED and every operation carries an OVERLAPPED,
/// even though nothing here is asynchronous to the caller. That is not a style choice.
///
/// Windows serialises all I/O on a *synchronous* file handle: a WriteFile issued while a
/// ReadFile is pending on the same handle does not run alongside it, it queues behind it. The
/// DVC plugin drives one handle from two threads — a reader parked waiting for the tray agent
/// to speak, and the RDP callback thread writing the request that would make it speak. On a
/// synchronous handle that is a deadlock with no error and no timeout: the request is never
/// delivered, both ends report a healthy connection, and the only symptom is that scanning
/// over RDP does not work. It shipped that way, and it cost weeks.
///
/// An overlapped handle removes the serialisation, so reads and writes proceed independently.
/// The cost is that overlapped I/O has to be handled exactly:
///
///   * the OVERLAPPED and its event are members, never on the stack — a cancelled operation
///     that completes later writes into the OVERLAPPED it was given, and if that has gone out
///     of scope the kernel corrupts the stack;
///   * a timed-out operation is cancelled *and reaped* with GetOverlappedResult before the
///     call returns, for the same reason;
///   * close() cancels outstanding I/O first and only then takes the lifetime lock, so it can
///     never wait on a read that only it could have woken.
///
/// The data source's request/response use is unaffected; it simply never had two operations
/// in flight to be serialised.
class PipeClient {
public:
    PipeClient() = default;
    ~PipeClient() { close(); }

    PipeClient(const PipeClient&) = delete;
    PipeClient& operator=(const PipeClient&) = delete;

    bool isOpen() const {
        return handle_ != INVALID_HANDLE_VALUE && handle_ != nullptr &&
               !closing_.load(std::memory_order_acquire);
    }

    /// Connects to \\.\pipe\<name>. Waits up to timeoutMs for a free instance.
    void connect(const std::wstring& name, DWORD timeoutMs = 5000) {
        close();
        std::wstring path = L"\\\\.\\pipe\\" + name;

        HANDLE opened = INVALID_HANDLE_VALUE;
        const DWORD deadline = GetTickCount() + timeoutMs;
        for (;;) {
            opened = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                 OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
            if (opened != INVALID_HANDLE_VALUE) break;

            DWORD error = GetLastError();
            if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND)
                throw PipeError("CreateFile on pipe failed", error);

            DWORD remaining = (GetTickCount() >= deadline) ? 0 : deadline - GetTickCount();
            if (remaining == 0)
                throw PipeError("timed out connecting to the ScanBridge agent", error);

            // ERROR_FILE_NOT_FOUND means the server has not created the pipe yet; poll.
            if (error == ERROR_FILE_NOT_FOUND) Sleep(100);
            else WaitNamedPipeW(path.c_str(), remaining);
        }

        DWORD mode = PIPE_READMODE_BYTE;
        SetNamedPipeHandleState(opened, &mode, nullptr, nullptr);

        // Manual reset: the kernel signals these, and each operation resets its own before
        // issuing. Auto-reset would be wrong here — GetOverlappedResult(wait) may also wait on
        // the event, and an auto-reset event consumed by the first waiter would hang.
        HANDLE readEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        HANDLE writeEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!readEvent || !writeEvent) {
            DWORD error = GetLastError();
            if (readEvent) CloseHandle(readEvent);
            if (writeEvent) CloseHandle(writeEvent);
            CloseHandle(opened);
            throw PipeError("could not create the pipe's completion events", error);
        }

        {
            std::unique_lock<std::shared_timed_mutex> lock(lifetime_);
            handle_ = opened;
            readEvent_ = readEvent;
            writeEvent_ = writeEvent;
            closing_.store(false, std::memory_order_release);
        }

        RS_LOG_INFO("pipe connected: %S", name.c_str());
    }

    /// Safe to call from any thread, including while another thread is parked in a read.
    ///
    /// The order matters and is the whole point: raise the flag so no new operation is
    /// issued, cancel the ones already in flight so their threads wake and release the
    /// lifetime lock, and only then take that lock and close the handle. Closing first would
    /// block — a synchronous CloseHandle waits for pending I/O, which is how shutdown used to
    /// wedge the RDP client's own thread.
    void close() {
        closing_.store(true, std::memory_order_release);

        HANDLE pending = handle_;
        std::unique_lock<std::shared_timed_mutex> lock(lifetime_, std::defer_lock);

        // Cancel and retry rather than cancel once. An operation can slip past the closing_
        // check and reach ReadFile just after the first CancelIoEx, and a single cancel would
        // miss it and leave close() waiting on a read only a cancel can end. Re-cancelling on
        // every attempt closes that window without ever blocking on the lock.
        for (int attempt = 0; attempt < 50; ++attempt) {
            if (pending != INVALID_HANDLE_VALUE && pending != nullptr)
                CancelIoEx(pending, nullptr);
            if (lock.try_lock_for(std::chrono::milliseconds(20))) break;
        }

        if (!lock.owns_lock()) {
            // Never seen; one second of repeated cancellation should end any operation. If it
            // somehow has not, leaking the handle is the safe answer — closing it underneath a
            // live overlapped operation corrupts memory in the host process.
            RS_LOG_ERROR("pipe close abandoned: an operation would not cancel; handle left open");
            return;
        }

        if (handle_ != INVALID_HANDLE_VALUE && handle_ != nullptr) {
            CloseHandle(handle_);
            RS_LOG_DEBUG("pipe closed");
        }
        handle_ = INVALID_HANDLE_VALUE;

        if (readEvent_) { CloseHandle(readEvent_); readEvent_ = nullptr; }
        if (writeEvent_) { CloseHandle(writeEvent_); writeEvent_ = nullptr; }
    }

    void sendFrame(MsgType type, uint32_t streamId, const PayloadWriter& payload) {
        sendFrame(type, streamId, payload.data());
    }

    void sendFrame(MsgType type, uint32_t streamId, const std::vector<uint8_t>& payload) {
        std::vector<uint8_t> frame = encode(type, streamId, payload);
        writeAll(frame.data(), frame.size());
    }

    /// Reads one frame. Throws PipeError on transport failure or timeout.
    Frame readFrame(DWORD timeoutMs = 30000) {
        uint8_t header[kHeaderSize];
        readAll(header, kHeaderSize, timeoutMs);

        Frame frame;
        uint32_t length = parseHeader(header, frame.type, frame.streamId);
        if (length > 0) {
            frame.payload.resize(length);
            readAll(frame.payload.data(), length, timeoutMs);
        }
        return frame;
    }

    /// Byte-pump interface used by the DVC plugin, which relays opaque bytes rather than
    /// parsing frames.
    void writeRaw(const uint8_t* data, size_t length) { writeAll(data, length); }

    /// Blocking read of up to `capacity` bytes. Returns 0 when the peer closes the pipe,
    /// which the caller treats as a clean shutdown rather than an error.
    DWORD readSome(uint8_t* buffer, DWORD capacity) {
        try {
            return transfer(true, buffer, capacity, INFINITE);
        }
        catch (const PipeError& ex) {
            if (isDisconnect(ex.code())) return 0;
            throw;
        }
    }

private:
    /// Errors that mean "the other end went away", which is an ordinary end of life for every
    /// one of these connections and must not be reported as a fault.
    static bool isDisconnect(DWORD error) {
        return error == ERROR_BROKEN_PIPE || error == ERROR_PIPE_NOT_CONNECTED ||
               error == ERROR_OPERATION_ABORTED || error == ERROR_INVALID_HANDLE ||
               error == ERROR_NO_DATA || error == ERROR_HANDLE_EOF;
    }

    /// One overlapped ReadFile or WriteFile, waited to completion.
    ///
    /// Reads and writes take different locks and different OVERLAPPEDs, which is what lets
    /// them run at the same time. Two operations in the same direction are serialised, which
    /// is required: interleaved writes would corrupt the frame stream.
    DWORD transfer(bool reading, void* buffer, DWORD count, DWORD timeoutMs) {
        if (count == 0) return 0;

        // Shared, so several transfers coexist; close() takes it exclusively, but only after
        // cancelling outstanding I/O, so it cannot wait forever on a parked read.
        std::shared_lock<std::shared_timed_mutex> alive(lifetime_);

        if (!isOpen())
            throw PipeError(reading ? "read on a closed pipe" : "write on a closed pipe",
                            ERROR_INVALID_HANDLE);

        std::unique_lock<std::mutex> direction(reading ? readMutex_ : writeMutex_);

        OVERLAPPED& overlapped = reading ? readOverlapped_ : writeOverlapped_;
        HANDLE event = reading ? readEvent_ : writeEvent_;

        ZeroMemory(&overlapped, sizeof(overlapped));
        overlapped.hEvent = event;
        ResetEvent(event);

        // lpNumberOfBytesTransferred must be NULL when an OVERLAPPED is supplied; the count
        // comes from GetOverlappedResult, which is correct whether the operation completed
        // immediately or was queued.
        const BOOL issued = reading
            ? ReadFile(handle_, buffer, count, nullptr, &overlapped)
            : WriteFile(handle_, static_cast<const void*>(buffer), count, nullptr, &overlapped);

        if (!issued) {
            const DWORD error = GetLastError();
            if (error != ERROR_IO_PENDING)
                throw PipeError(reading ? "ReadFile on pipe failed" : "WriteFile on pipe failed", error);

            if (WaitForSingleObject(event, timeoutMs) == WAIT_TIMEOUT) {
                // Cancel, then wait unconditionally for the operation to finish unwinding.
                // Returning while the kernel may still write into this OVERLAPPED would be a
                // use-after-scope on the next call that reuses it.
                CancelIoEx(handle_, &overlapped);

                DWORD abandoned = 0;
                if (GetOverlappedResult(handle_, &overlapped, &abandoned, TRUE) && abandoned > 0)
                    return abandoned;      // it completed in the gap; the data is real, keep it

                throw PipeError("timed out waiting for a response from the agent", WAIT_TIMEOUT);
            }
        }

        DWORD transferred = 0;
        if (!GetOverlappedResult(handle_, &overlapped, &transferred, TRUE))
            throw PipeError(reading ? "ReadFile on pipe failed" : "WriteFile on pipe failed",
                            GetLastError());

        return transferred;
    }

    void writeAll(const uint8_t* data, size_t length) {
        size_t written = 0;
        while (written < length) {
            DWORD chunk = transfer(false, const_cast<uint8_t*>(data + written),
                                   static_cast<DWORD>(length - written), INFINITE);
            if (chunk == 0) throw PipeError("pipe write returned 0 bytes");
            written += chunk;
        }
    }

    /// Reads exactly `length` bytes, or throws. The timeout is the budget for the whole
    /// read, not for each chunk of it — a peer that dribbles bytes must not be able to hold
    /// the caller indefinitely by staying just barely alive.
    void readAll(uint8_t* buffer, size_t length, DWORD timeoutMs) {
        const DWORD deadline = GetTickCount() + timeoutMs;

        size_t read = 0;
        while (read < length) {
            const DWORD now = GetTickCount();
            const DWORD remaining = (now >= deadline) ? 0 : deadline - now;
            if (remaining == 0)
                throw PipeError("timed out waiting for a response from the agent", WAIT_TIMEOUT);

            DWORD chunk = transfer(true, buffer + read, static_cast<DWORD>(length - read), remaining);
            if (chunk == 0) throw PipeError("pipe read returned 0 bytes; peer closed", ERROR_BROKEN_PIPE);
            read += chunk;
        }
    }

    HANDLE handle_ = INVALID_HANDLE_VALUE;
    HANDLE readEvent_ = nullptr;
    HANDLE writeEvent_ = nullptr;

    // Members, not locals: a cancelled operation completes against the OVERLAPPED it was
    // given, whenever the kernel gets round to it.
    OVERLAPPED readOverlapped_{};
    OVERLAPPED writeOverlapped_{};

    std::shared_timed_mutex lifetime_;   // held shared by transfers, exclusively by close()
    std::mutex readMutex_;
    std::mutex writeMutex_;
    std::atomic<bool> closing_{ false };
};

// ---------------------------------------------------------------- handshake

constexpr char kInitiatorLabel[] = "ScanBridge/v1/initiator";
constexpr char kResponderLabel[] = "ScanBridge/v1/responder";

inline std::vector<uint8_t> computeMac(const std::vector<uint8_t>& key,
                                       const char* label,
                                       const std::vector<uint8_t>& initiatorNonce,
                                       const std::vector<uint8_t>& responderNonce,
                                       uint32_t sessionId) {
    std::vector<uint8_t> message;
    const size_t labelLen = strlen(label);
    message.reserve(labelLen + initiatorNonce.size() + responderNonce.size() + sizeof(uint32_t));
    message.insert(message.end(), label, label + labelLen);
    message.insert(message.end(), initiatorNonce.begin(), initiatorNonce.end());
    message.insert(message.end(), responderNonce.begin(), responderNonce.end());
    const uint8_t* sid = reinterpret_cast<const uint8_t*>(&sessionId);
    message.insert(message.end(), sid, sid + sizeof(uint32_t));
    return hmacSha256(key, message);
}

inline std::string machineName() {
    char buffer[MAX_COMPUTERNAME_LENGTH + 1]{};
    DWORD size = sizeof(buffer);
    if (!GetComputerNameA(buffer, &size)) return "unknown";
    return std::string(buffer, size);
}

/// Performs HELLO / HELLO_ACK / AUTHENTICATE / AUTH_RESULT. Throws on any failure.
/// Returns the peer's advertised capability bitmask.
inline uint32_t performHandshake(PipeClient& pipe,
                                 PeerRole role,
                                 uint32_t capabilities,
                                 const std::vector<uint8_t>& sharedSecret,
                                 uint32_t sessionId,
                                 uint32_t streamId) {
    std::vector<uint8_t> nonce = randomBytes(kNonceLength);

    PayloadWriter hello;
    hello.u8(kVersion);
    hello.u8(static_cast<uint8_t>(role));
    hello.str(machineName());
    hello.bytes(nonce.data(), nonce.size());
    hello.u32(capabilities);
    pipe.sendFrame(MsgType::Hello, streamId, hello);

    Frame ack = pipe.readFrame(10000);
    if (ack.type != MsgType::HelloAck)
        throw PipeError("expected HELLO_ACK from the agent");

    PayloadReader r = ack.reader();
    uint8_t negotiated = r.u8();
    if (negotiated != kVersion)
        throw PipeError("protocol version mismatch with the ScanBridge agent");
    r.u8();          // peer role
    r.str();         // peer machine name
    std::vector<uint8_t> peerNonce = r.fixedBytes(kNonceLength);
    uint32_t peerCaps = r.u32();

    std::vector<uint8_t> mac = computeMac(sharedSecret, kInitiatorLabel, nonce, peerNonce, sessionId);
    PayloadWriter auth;
    auth.bytes(mac.data(), mac.size());
    pipe.sendFrame(MsgType::Authenticate, streamId, auth);

    Frame result = pipe.readFrame(10000);
    if (result.type != MsgType::AuthResult)
        throw PipeError("expected AUTH_RESULT from the agent");

    PayloadReader ar = result.reader();
    AuthStatus status = static_cast<AuthStatus>(ar.u8());
    std::string detail = ar.str();
    if (status != AuthStatus::Ok)
        throw PipeError("authentication rejected by the agent: " + detail);

    RS_LOG_INFO("handshake complete, peer capabilities=0x%08X", peerCaps);
    return peerCaps;
}

}  // namespace rs

#endif  // RS_PIPE_H
