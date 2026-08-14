// pipetest.cpp — regression test for rs::PipeClient's transport behaviour.
//
// Why this exists
// ---------------
// The DVC plugin runs two threads over one pipe handle: a reader parked in ReadFile waiting
// for the tray agent to speak, and the RDP callback thread writing the request that would
// make it speak. On a handle opened without FILE_FLAG_OVERLAPPED, Windows serialises all I/O
// on the file object, so that write queues behind the pending read and neither ever finishes.
//
// That deadlock shipped. It cost weeks, because every layer reported itself healthy: the
// server logged the request as sent, the client logged the channel as bridged, and the
// request simply never arrived. The only trace was a single "WriteFile on pipe failed" that
// surfaced forty-six minutes later when the session was torn down.
//
// The data source never hit it — it is strictly request-then-response on one thread — which
// is why every local test passed while the product did not work.
//
// So the tests below deliberately use PipeClient the way the plugin does, not the way the
// data source does. Check 1 is the one that matters; it hangs on the old implementation.
//
// Exit code 0 = all checks passed.

#include <windows.h>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <functional>
#include <memory>
#include <string>
#include <thread>
#include <vector>

#include "../include/rs_pipe.h"
#include "../include/rs_protocol.h"

using namespace rs;

namespace {

int g_failures = 0;

void pass(const char* name) { printf("  [ ok ] %s\n", name); }

void fail(const char* name, const std::string& detail) {
    printf("  [FAIL] %s: %s\n", name, detail.c_str());
    ++g_failures;
}

/// Minimal blocking pipe server standing in for the tray agent.
class TestServer {
public:
    explicit TestServer(const std::wstring& name) {
        path_ = L"\\\\.\\pipe\\" + name;
        handle_ = CreateNamedPipeW(path_.c_str(), PIPE_ACCESS_DUPLEX,
                                   PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                                   1, 64 * 1024, 64 * 1024, 0, nullptr);
    }

    ~TestServer() { if (handle_ != INVALID_HANDLE_VALUE) CloseHandle(handle_); }

    bool valid() const { return handle_ != INVALID_HANDLE_VALUE; }

    /// Accepts on a background thread so the client can connect on this one.
    void acceptInBackground() {
        accepting_ = std::thread([this] {
            if (!ConnectNamedPipe(handle_, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED)
                return;
            connected_.store(true);
        });
    }

    void joinAccept() { if (accepting_.joinable()) accepting_.join(); }

    DWORD pending() const {
        DWORD available = 0;
        PeekNamedPipe(handle_, nullptr, 0, nullptr, &available, nullptr);
        return available;
    }

    bool read(uint8_t* buffer, DWORD count) {
        DWORD got = 0;
        return ReadFile(handle_, buffer, count, &got, nullptr) && got == count;
    }

    bool write(const uint8_t* data, DWORD count) {
        DWORD sent = 0;
        return WriteFile(handle_, data, count, &sent, nullptr) && sent == count;
    }

private:
    std::wstring path_;
    HANDLE handle_ = INVALID_HANDLE_VALUE;
    std::thread accepting_;
    std::atomic<bool> connected_{ false };
};

std::wstring uniqueName(const wchar_t* tag) {
    wchar_t buffer[128]{};
    _snwprintf_s(buffer, _TRUNCATE, L"RemoteScanner.Test.%s.%lu", tag, GetCurrentProcessId());
    return buffer;
}

/// Runs `work` on its own thread and reports whether it finished inside the budget. Every
/// check needs this: the failure being tested for is a hang, and a hanging test that simply
/// never returns tells the build nothing and blocks it forever.
bool within(DWORD budgetMs, const std::function<void()>& work, DWORD* elapsedMs = nullptr) {
    auto done = std::make_shared<std::atomic<bool>>(false);
    auto start = std::chrono::steady_clock::now();

    std::thread([done, work] {
        try { work(); } catch (...) { }
        done->store(true);
    }).detach();

    for (DWORD waited = 0; waited < budgetMs; waited += 5) {
        if (done->load()) break;
        Sleep(5);
    }

    auto end = std::chrono::steady_clock::now();
    if (elapsedMs)
        *elapsedMs = static_cast<DWORD>(
            std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count());
    return done->load();
}

// ---------------------------------------------------------------- the checks

/// THE regression test. A write must not be held up by a read that is parked on the same
/// handle waiting for data only that write can produce.
void checkConcurrentWriteWhileReadParked() {
    const char* name = "a write completes while a read is parked on the same handle";

    TestServer server(uniqueName(L"concurrent"));
    if (!server.valid()) { fail(name, "could not create the test pipe"); return; }
    server.acceptInBackground();

    PipeClient pipe;
    try { pipe.connect(uniqueName(L"concurrent"), 3000); }
    catch (const std::exception& ex) { fail(name, ex.what()); return; }
    server.joinAccept();

    // Reader parks. The server never speaks, exactly like the tray agent before it is asked.
    std::atomic<bool> readerParked{ false };
    std::thread reader([&] {
        std::vector<uint8_t> buffer(4096);
        readerParked.store(true);
        try { pipe.readSome(buffer.data(), static_cast<DWORD>(buffer.size())); } catch (...) { }
    });
    reader.detach();

    while (!readerParked.load()) Sleep(5);
    Sleep(200);   // let the ReadFile actually reach the kernel and pend

    const uint8_t frame[12] = { 0x52, 0x01, 0x10, 0x00, 1, 0, 0, 0, 0, 0, 0, 0 };
    std::atomic<bool> wrote{ false };

    DWORD elapsed = 0;
    bool finished = within(1000, [&] {
        try { pipe.writeRaw(frame, sizeof(frame)); wrote.store(true); } catch (...) { }
    }, &elapsed);

    if (!finished) {
        fail(name, "the write never completed — it is serialised behind the pending read");
        return;
    }
    if (!wrote.load()) { fail(name, "the write threw"); return; }

    Sleep(100);
    DWORD delivered = server.pending();
    if (delivered != sizeof(frame)) {
        char detail[128];
        _snprintf_s(detail, _TRUNCATE, "the far end received %lu bytes, expected 12", delivered);
        fail(name, detail);
        return;
    }

    printf("         (delivered in %lu ms)\n", elapsed);
    pass(name);
}

/// The data source's pattern: send, then wait for the answer. This is what already worked,
/// and the overlapped rewrite must not change it.
void checkRequestResponseRoundTrip() {
    const char* name = "request/response round trip still works";

    TestServer server(uniqueName(L"roundtrip"));
    if (!server.valid()) { fail(name, "could not create the test pipe"); return; }
    server.acceptInBackground();

    PipeClient pipe;
    try { pipe.connect(uniqueName(L"roundtrip"), 3000); }
    catch (const std::exception& ex) { fail(name, ex.what()); return; }
    server.joinAccept();

    // Echo one frame back with a different type, on a background thread.
    std::thread responder([&] {
        uint8_t header[kHeaderSize]{};
        if (!server.read(header, kHeaderSize)) return;

        MsgType type{};
        uint32_t streamId = 0;
        uint32_t length = parseHeader(header, type, streamId);

        std::vector<uint8_t> payload(length);
        if (length > 0 && !server.read(payload.data(), length)) return;

        std::vector<uint8_t> reply = encode(MsgType::ScannerEnumResponse, streamId, payload);
        server.write(reply.data(), static_cast<DWORD>(reply.size()));
    });

    std::atomic<bool> ok{ false };
    std::string error;

    bool finished = within(3000, [&] {
        try {
            std::vector<uint8_t> payload{ 1, 2, 3, 4, 5 };
            pipe.sendFrame(MsgType::ScannerEnumRequest, 7, payload);
            Frame response = pipe.readFrame(2000);
            ok.store(response.type == MsgType::ScannerEnumResponse &&
                     response.streamId == 7 &&
                     response.payload == payload);
        }
        catch (const std::exception& ex) { error = ex.what(); }
    });

    responder.join();

    if (!finished) fail(name, "the round trip hung");
    else if (!ok.load()) fail(name, error.empty() ? "the reply did not match what was sent" : error);
    else pass(name);
}

/// A silent peer must produce a timeout, not a wedged host application. TWAIN calls into the
/// data source synchronously, so a read that never returns freezes the user's scanning
/// application outright.
void checkReadTimesOutOnASilentPeer() {
    const char* name = "readFrame times out on a silent peer";

    TestServer server(uniqueName(L"timeout"));
    if (!server.valid()) { fail(name, "could not create the test pipe"); return; }
    server.acceptInBackground();

    PipeClient pipe;
    try { pipe.connect(uniqueName(L"timeout"), 3000); }
    catch (const std::exception& ex) { fail(name, ex.what()); return; }
    server.joinAccept();

    std::atomic<bool> threw{ false };
    DWORD elapsed = 0;

    bool finished = within(2000, [&] {
        try { pipe.readFrame(400); }
        catch (const std::exception&) { threw.store(true); }
    }, &elapsed);

    if (!finished) fail(name, "the read never returned");
    else if (!threw.load()) fail(name, "the read returned without data and without throwing");
    else if (elapsed > 1500) fail(name, "the timeout took far longer than it was given");
    else pass(name);
}

/// Shutdown must not be able to hang. The plugin closes the pipe from its RDP callback
/// thread while the reader is still parked; on a synchronous handle CloseHandle waits for
/// that pending read, and the RDP client's thread is stuck with it.
void checkCloseReturnsWhileAReadIsParked() {
    const char* name = "close() returns promptly while a read is parked";

    TestServer server(uniqueName(L"close"));
    if (!server.valid()) { fail(name, "could not create the test pipe"); return; }
    server.acceptInBackground();

    PipeClient pipe;
    try { pipe.connect(uniqueName(L"close"), 3000); }
    catch (const std::exception& ex) { fail(name, ex.what()); return; }
    server.joinAccept();

    std::atomic<bool> readerParked{ false };
    std::thread reader([&] {
        std::vector<uint8_t> buffer(4096);
        readerParked.store(true);
        try { pipe.readSome(buffer.data(), static_cast<DWORD>(buffer.size())); } catch (...) { }
    });
    reader.detach();

    while (!readerParked.load()) Sleep(5);
    Sleep(200);

    DWORD elapsed = 0;
    bool finished = within(1000, [&] { pipe.close(); }, &elapsed);

    if (!finished) fail(name, "close() blocked behind the pending read");
    else { printf("         (closed in %lu ms)\n", elapsed); pass(name); }
}

/// The shared secret must come from this user's own hive, opened by SID.
///
/// HKEY_CURRENT_USER is resolved once per process and cached, and a host that impersonates —
/// mstsc.exe does, while handling credentials — can have it pointing at another hive for its
/// whole life. Loaded into such a process this code read a valid but wrong key, the tray agent
/// refused the link, and scanning was dead with nothing anywhere naming the cause.
///
/// The fix opens HKEY_USERS\<our SID> instead. This checks the fix is actually in force rather
/// than quietly falling back to the path that was broken — a silent fallback would look exactly
/// like success right up until it wasn't.
void checkTheSecretComesFromThisUsersHive() {
    const char* name = "the shared secret is read from this user's own hive, by SID";

    bool viaSid = false;
    HKEY hive = openUserHive(&viaSid);
    closeUserHive(hive);

    if (!viaSid) {
        fail(name, "the hive could not be opened by SID, so the read fell back to a path a "
                   "host process can have cached against the wrong user");
        return;
    }

    std::wstring sid = processUserSid();
    if (sid.empty()) { fail(name, "this process has no resolvable user SID"); return; }

    printf("         (user %S)\n", sid.c_str());
    pass(name);
}

/// The whole native client hop, against the real tray agent: connect, HELLO, HELLO_ACK,
/// AUTHENTICATE, AUTH_RESULT.
///
/// Everything above this point talks to a pipe this test created, which proves the transport
/// moves bytes but proves nothing about what the bytes mean. The DVC plugin's failure mode was
/// exactly there: framing was fine, the handshake completed, and the tray agent rejected the
/// MAC — so redirection was dead while every transport check passed. This is the one check
/// that would have caught it, so it runs against the live agent whenever one is present.
///
/// Skipped, not failed, when the tray agent is not running: this also runs on a build machine.
/// When true, the handshake is attempted with a key that matches nothing — neither what the
/// agent loaded nor what the registry holds. That is the state that took scanning down: the
/// plugin held a valid key from somewhere, and every other component held a different one.
/// The agent should still admit the link, because the caller is this same Windows user and the
/// pipe admits nobody else. Opt-in, so a normal run never proves the wrong thing.
bool g_useAWrongKey = false;

void checkHandshakeAgainstTheLocalAgent() {
    const char* name = g_useAWrongKey
        ? "a link is admitted on identity when the keys disagree"
        : "the native handshake is accepted by the tray agent";

    PipeClient pipe;
    try {
        pipe.connect(kAgentPipeName, 1500);
    }
    catch (const std::exception&) {
        printf("  [skip] %s (no tray agent running on this PC)\n", name);
        return;
    }

    std::vector<uint8_t> secret;
    try {
        secret = loadProtectedSecret(L"Software\\RemoteScanner", L"Secret");
    }
    catch (const std::exception& ex) {
        printf("  [skip] %s (%s)\n", name, ex.what());
        return;
    }

    if (g_useAWrongKey) secret = randomBytes(secret.size());

    // Printed so this can be compared directly against the key the tray agent logs and the key
    // the DVC plugin logs. Three readings of the same registry value that must agree.
    printf("         (secret key %s, %zu bytes%s)\n",
           secretFingerprint(secret).c_str(), secret.size(),
           g_useAWrongKey ? ", deliberately wrong" : "");

    const uint32_t capabilities =
        static_cast<uint32_t>(PeerCaps::FlowControl) |
        static_cast<uint32_t>(PeerCaps::Cancellation);

    std::string error;
    bool ok = false;

    bool finished = within(15000, [&] {
        try {
            performHandshake(pipe, PeerRole::DvcPlugin, capabilities, secret, 0, 0);
            ok = true;
        }
        catch (const std::exception& ex) { error = ex.what(); }
    });

    SecureZeroMemory(secret.data(), secret.size());
    pipe.close();

    if (!finished) fail(name, "the handshake hung");
    else if (!ok) fail(name, error);
    else pass(name);
}

}  // namespace

int main(int argc, char** argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);

    for (int i = 1; i < argc; ++i)
        if (std::string(argv[i]) == "--wrong-key") g_useAWrongKey = true;

    printf("rs::PipeClient transport checks\n");
    printf("-------------------------------\n");

    checkConcurrentWriteWhileReadParked();
    checkRequestResponseRoundTrip();
    checkReadTimesOutOnASilentPeer();
    checkCloseReturnsWhileAReadIsParked();
    checkTheSecretComesFromThisUsersHive();
    checkHandshakeAgainstTheLocalAgent();

    printf("-------------------------------\n");
    if (g_failures == 0) printf("all checks passed\n");
    else printf("%d check(s) FAILED\n", g_failures);

    // Hard exit. A thread stuck inside a synchronous ReadFile can never be joined, and on a
    // failing build that is precisely the state we are reporting — the process must still be
    // able to return an exit code to the build.
    ExitProcess(g_failures == 0 ? 0 : 1);
}
