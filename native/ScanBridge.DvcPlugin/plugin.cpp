// plugin.cpp — ScanBridge RDP Dynamic Virtual Channel plugin.
//
// mstsc.exe loads this DLL in-process at connect time, discovering it from
//   HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\ScanBridge
//     Name = <full path to this DLL>
// and calling the exported VirtualChannelGetInstance to obtain an IWTSPlugin.
//
// Its only job is to be a byte pump: it owns the client end of the "ScanBridge" dynamic
// virtual channel and splices it to a named pipe served by the local tray agent. It does not
// parse the protocol beyond its own authentication handshake, because the real endpoints are
// the virtual data source on the server and the tray agent on this PC.
//
// Why native C++: this runs inside mstsc.exe. Loading the CLR into the user's RDP client to
// move some bytes would be an unreasonable risk to their remote session.

#include <windows.h>
#include <unknwn.h>
#include <tsvirtualchannels.h>
#include <atomic>
#include <thread>
#include <mutex>
#include <vector>

#include "../include/rs_protocol.h"
#include "../include/rs_pipe.h"
#include "../include/rs_log.h"

using namespace rs;

namespace {

std::atomic<LONG> g_objectCount{ 0 };

/// Bridges one DVC instance to one pipe connection to the local agent.
///
/// Lifetime: RDP owns this object through IWTSVirtualChannelCallback. The reader thread
/// holds its own reference to the channel so a close racing with a write cannot use a
/// released interface.
class ChannelBridge final : public IWTSVirtualChannelCallback {
public:
    explicit ChannelBridge(IWTSVirtualChannel* channel) : channel_(channel) {
        if (channel_) channel_->AddRef();
        ++g_objectCount;
    }

    /// Connects to the tray agent and starts pumping agent -> RDP. Returns false if the
    /// agent is not running, in which case RDP is told to reject the channel.
    bool start() {
        try {
            // Read on a clean thread: this callback runs on an RDP worker thread inside
            // mstsc.exe, and if that thread is impersonating, HKEY_CURRENT_USER and DPAPI both
            // follow the impersonated identity — yielding a valid secret belonging to the wrong
            // user, and a link the tray agent refuses for no visible reason.
            std::vector<uint8_t> secret =
                loadProtectedSecretOnCleanThread(L"Software\\ScanBridge", L"Secret");

            RS_LOG_INFO("shared secret loaded: key %s, user %S (calling thread impersonating: %s)",
                        secretFingerprint(secret).c_str(),
                        processUserSid().c_str(),
                        threadIsImpersonating() ? "yes" : "no");

            pipe_.connect(kAgentPipeName, 5000);

            const uint32_t capabilities =
                static_cast<uint32_t>(PeerCaps::FlowControl) |
                static_cast<uint32_t>(PeerCaps::Cancellation);

            // Session id is 0 on the client side of the link: this hop is scoped by the
            // user's own DPAPI key and pipe ACL, not by a terminal services session.
            performHandshake(pipe_, PeerRole::DvcPlugin, capabilities, secret, 0, 0);
            SecureZeroMemory(secret.data(), secret.size());

            running_.store(true);
            reader_ = std::thread([this] { pumpAgentToChannel(); });
            RS_LOG_INFO("channel bridged to the local agent");
            return true;
        }
        catch (const std::exception& ex) {
            RS_LOG_ERROR("cannot bridge channel: %s", ex.what());
            return false;
        }
    }

    /// Records a chunk crossing the RDP connection, naming the message type where it can.
    ///
    /// Logged at INFO for control messages, so it is present without anyone having switched on
    /// diagnostics first. Scanning is a handful of control frames plus a flood of page data;
    /// only the page data is noisy, and only that is held back to DEBUG.
    ///
    /// This is the client half of the same record the session agent keeps on the server. With
    /// both, a frame that never arrives is attributable to a side: the server logging a send
    /// with no matching line here means it was lost on the wire, and no send at all means it
    /// never left. Without it the two ends both look healthy and the request simply vanishes.
    ///
    /// The buffer is raw channel bytes, so this reads the frame header directly rather than
    /// decoding: chunks can also be partial, which is why a short or unrecognised buffer is
    /// reported by size alone instead of being treated as an error.
    static void logCrossing(const char* direction, const BYTE* data, ULONG size) {
        if (size < rs::kHeaderSize || data[0] != rs::kMagic) {
            RS_LOG_DEBUG("%s: %lu bytes (continuation)", direction, static_cast<unsigned long>(size));
            return;
        }

        const auto type = static_cast<rs::MsgType>(
            static_cast<uint16_t>(data[2]) | (static_cast<uint16_t>(data[3]) << 8));

        // FlowCredit rides alongside the page data - one per data frame - so it belongs with it
        // at DEBUG rather than among the few lines that describe what the scan did.
        if (type == rs::MsgType::ScanPageData || type == rs::MsgType::FlowCredit) {
            RS_LOG_DEBUG("%s: bulk, %lu bytes", direction, static_cast<unsigned long>(size));
            return;
        }

        RS_LOG_INFO("%s: message 0x%02X, %lu bytes",
                    direction, static_cast<unsigned>(type), static_cast<unsigned long>(size));
    }

    // ---- IWTSVirtualChannelCallback

    /// Data arriving from the RDP server. Forwarded verbatim to the agent.
    HRESULT STDMETHODCALLTYPE OnDataReceived(ULONG cbSize, BYTE* pBuffer) override {
        if (cbSize == 0 || pBuffer == nullptr) return S_OK;
        try {
            std::lock_guard<std::mutex> lock(writeMutex_);
            pipe_.writeRaw(pBuffer, cbSize);

            logCrossing("server -> agent", pBuffer, cbSize);
            return S_OK;
        }
        catch (const std::exception& ex) {
            RS_LOG_ERROR("forwarding %lu bytes to the agent failed: %s", cbSize, ex.what());
            shutdown();
            return E_FAIL;
        }
    }

    HRESULT STDMETHODCALLTYPE OnClose() override {
        RS_LOG_INFO("RDP closed the scanner channel");
        shutdown();
        return S_OK;
    }

    // ---- IUnknown

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IWTSVirtualChannelCallback) {
            *ppv = static_cast<IWTSVirtualChannelCallback*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(++refs_); }

    ULONG STDMETHODCALLTYPE Release() override {
        const LONG remaining = --refs_;
        if (remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }

private:
    ~ChannelBridge() {
        shutdown();
        if (channel_) channel_->Release();
        --g_objectCount;
    }

    void shutdown() {
        bool expected = true;
        if (!running_.compare_exchange_strong(expected, false)) {
            // Already shutting down; still make sure the pipe is closed so a blocked
            // reader wakes up.
            pipe_.close();
            return;
        }

        pipe_.close();
        if (reader_.joinable() && reader_.get_id() != std::this_thread::get_id())
            reader_.join();
        else if (reader_.joinable())
            reader_.detach();
    }

    /// Agent -> RDP direction. Reads whatever the agent produces and writes it to the DVC.
    ///
    /// Two buffers, used alternately, rather than one reused. IWTSVirtualChannel::Write reports
    /// no completion, so there is no moment at which the bytes handed to it are known to have
    /// been consumed. With a single buffer the next readSome would begin overwriting them
    /// immediately — harmless while traffic is one frame at a time, but a scan is hundreds of
    /// back-to-back page-data frames, which is exactly when the previous write is most likely
    /// to still be in flight. Alternating gives every write a full round of breathing room for
    /// the cost of one extra 32 KB buffer.
    void pumpAgentToChannel() {
        std::vector<uint8_t> buffers[2] = {
            std::vector<uint8_t>(kMaxFrameSize),
            std::vector<uint8_t>(kMaxFrameSize),
        };
        size_t current = 0;

        try {
            while (running_.load()) {
                std::vector<uint8_t>& buffer = buffers[current];
                current ^= 1;

                const DWORD read = pipe_.readSome(buffer.data(), static_cast<DWORD>(buffer.size()));
                if (read == 0) break;                       // agent closed the pipe

                HRESULT hr = channel_->Write(read, buffer.data(), nullptr);
                if (FAILED(hr)) {
                    RS_LOG_ERROR("IWTSVirtualChannel::Write failed: 0x%08lX", static_cast<unsigned long>(hr));
                    break;
                }
                logCrossing("agent -> server", buffer.data(), read);
            }
        }
        catch (const std::exception& ex) {
            if (running_.load()) RS_LOG_ERROR("agent pump stopped: %s", ex.what());
        }

        running_.store(false);
        if (channel_) channel_->Close();
    }

    std::atomic<LONG> refs_{ 1 };
    IWTSVirtualChannel* channel_ = nullptr;
    PipeClient pipe_;
    std::mutex writeMutex_;
    std::thread reader_;
    std::atomic<bool> running_{ false };
};

/// Accepts inbound channel openings from the RDP server.
class ListenerCallback final : public IWTSListenerCallback {
public:
    ListenerCallback() { ++g_objectCount; }

    HRESULT STDMETHODCALLTYPE OnNewChannelConnection(
        IWTSVirtualChannel* pChannel, BSTR /*data*/, BOOL* pbAccept,
        IWTSVirtualChannelCallback** ppCallback) override {
        if (!pbAccept || !ppCallback) return E_POINTER;

        *pbAccept = FALSE;
        *ppCallback = nullptr;

        auto* bridge = new (std::nothrow) ChannelBridge(pChannel);
        if (!bridge) return E_OUTOFMEMORY;

        if (!bridge->start()) {
            // The tray agent is not running. Declining is the honest answer: the server
            // side will report "no local agent" rather than hanging on a dead channel.
            bridge->Release();
            RS_LOG_WARN("declined a scanner channel because the local agent is unavailable");
            return S_OK;
        }

        *pbAccept = TRUE;
        *ppCallback = bridge;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IWTSListenerCallback) {
            *ppv = static_cast<IWTSListenerCallback*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(++refs_); }

    ULONG STDMETHODCALLTYPE Release() override {
        const LONG remaining = --refs_;
        if (remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }

private:
    ~ListenerCallback() { --g_objectCount; }
    std::atomic<LONG> refs_{ 1 };
};

class ScannerPlugin final : public IWTSPlugin {
public:
    ScannerPlugin() { ++g_objectCount; }

    HRESULT STDMETHODCALLTYPE Initialize(IWTSVirtualChannelManager* pChannelMgr) override {
        if (!pChannelMgr) return E_POINTER;

        auto* callback = new (std::nothrow) ListenerCallback();
        if (!callback) return E_OUTOFMEMORY;

        IWTSListener* listener = nullptr;
        HRESULT hr = pChannelMgr->CreateListener(kDvcChannelNameA, 0, callback, &listener);
        callback->Release();

        if (FAILED(hr)) {
            RS_LOG_ERROR("CreateListener('%s') failed: 0x%08lX",
                         kDvcChannelNameA, static_cast<unsigned long>(hr));
            return hr;
        }
        if (listener) listener->Release();

        RS_LOG_INFO("listening on dynamic virtual channel '%s'", kDvcChannelNameA);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Connected() override {
        RS_LOG_INFO("RDP session connected");
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Disconnected(DWORD dwDisconnectCode) override {
        RS_LOG_INFO("RDP session disconnected (code %lu)", dwDisconnectCode);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Terminated() override {
        RS_LOG_INFO("RDP client terminating the plugin");
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IWTSPlugin) {
            *ppv = static_cast<IWTSPlugin*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(++refs_); }

    ULONG STDMETHODCALLTYPE Release() override {
        const LONG remaining = --refs_;
        if (remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }

private:
    ~ScannerPlugin() { --g_objectCount; }
    std::atomic<LONG> refs_{ 1 };
};

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        Log::instance().init(L"dvcplugin");

        // Stop Windows handing this process a cached HKEY_CURRENT_USER. mstsc.exe impersonates
        // while it handles credentials, and a cached mapping made from that moment points at
        // the wrong hive for the life of the process — which is how this plugin came to read a
        // valid but wrong shared secret and be refused by the agent on the same PC. Harmless
        // where the mapping was already correct; the reads also name their hive explicitly.
        RegDisablePredefinedCache();

        RS_LOG_INFO("ScanBridge DVC plugin loaded into pid %lu", GetCurrentProcessId());
    }
    return TRUE;
}

/// The entry point mstsc.exe looks for. Returns one IWTSPlugin instance.
/// The RDP client resolves this by name and calls it __stdcall (VCAPITYPE in the SDK's
/// cchannel.h, which we do not otherwise need).
extern "C" HRESULT __stdcall VirtualChannelGetInstance(REFIID refiid, ULONG* pNumObjs, VOID** ppObjArray) {
    if (!pNumObjs) return E_POINTER;

    if (refiid != IID_IWTSPlugin) return E_NOINTERFACE;

    // The documented probe: a null array asks how many objects we would supply.
    if (ppObjArray == nullptr) {
        *pNumObjs = 1;
        return S_OK;
    }

    if (*pNumObjs < 1) return E_INVALIDARG;

    auto* plugin = new (std::nothrow) ScannerPlugin();
    if (!plugin) return E_OUTOFMEMORY;

    ppObjArray[0] = static_cast<IWTSPlugin*>(plugin);
    *pNumObjs = 1;
    return S_OK;
}
