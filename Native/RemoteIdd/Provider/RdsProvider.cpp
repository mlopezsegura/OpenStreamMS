// RdsProvider.cpp — ver RdsProvider.h.

#include "RdsProvider.h"
#include <cfgmgr32.h>
#include <cstdio>
#include <new>
#include <vector>

namespace osms::rds {

static void Log(const wchar_t* fmt, ...)
{
    wchar_t buf[256];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(buf, _TRUNCATE, fmt, args);
    va_end(args);
    OutputDebugStringW(buf);
}

#define OSMS_IUNKNOWN                                                         \
    ULONG STDMETHODCALLTYPE AddRef() override { return AddRefImpl(); }       \
    ULONG STDMETHODCALLTYPE Release() override { return ReleaseImpl(); }

// ============================================================ conexión
// Delega todo en la conexión de rdpcorets salvo la elección y carga del IDD.
class Connection final : public RefCounted, public IWRdsProtocolConnection,
                         public IWRdsWddmIddProps, public IWRdsWddmIddProps1
{
public:
    Connection(IWRdsProtocolConnection* inner, SessionSink& sink, ULONG refreshHz)
        : m_inner(inner), m_sink(sink), m_refreshHz(refreshHz)
    {
        m_inner->AddRef();
    }
    ~Connection() override { m_inner->Release(); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IWRdsProtocolConnection))
            *ppv = static_cast<IWRdsProtocolConnection*>(this);
        else if (riid == __uuidof(IWRdsWddmIddProps))
            *ppv = static_cast<IWRdsWddmIddProps*>(this);
        else if (riid == __uuidof(IWRdsWddmIddProps1))
            *ppv = static_cast<IWRdsWddmIddProps1*>(this);
        else
            return m_inner->QueryInterface(riid, ppv);   // resto de interfaces: las de Microsoft
        AddRef();
        return S_OK;
    }
    OSMS_IUNKNOWN

    // ---- IWRdsProtocolConnection: delegación ----
    HRESULT STDMETHODCALLTYPE GetLogonErrorRedirector(IWRdsProtocolLogonErrorRedirector** p) override { return m_inner->GetLogonErrorRedirector(p); }
    HRESULT STDMETHODCALLTYPE AcceptConnection() override { return m_inner->AcceptConnection(); }
    HRESULT STDMETHODCALLTYPE GetClientData(WRDS_CLIENT_DATA* p) override
    {
        // Solo se guarda la resolución pedida; el resto (credenciales incluidas) no se toca.
        HRESULT hr = m_inner->GetClientData(p);
        if (SUCCEEDED(hr) && p)
        {
            m_width = p->HRes;
            m_height = p->VRes;
        }
        return hr;
    }
    HRESULT STDMETHODCALLTYPE GetClientMonitorData(UINT* n, UINT* primary) override { return m_inner->GetClientMonitorData(n, primary); }
    HRESULT STDMETHODCALLTYPE GetUserCredentials(WRDS_USER_CREDENTIAL* p) override { return m_inner->GetUserCredentials(p); }
    HRESULT STDMETHODCALLTYPE GetLicenseConnection(IWRdsProtocolLicenseConnection** p) override { return m_inner->GetLicenseConnection(p); }
    HRESULT STDMETHODCALLTYPE AuthenticateClientToSession(WRDS_SESSION_ID* s) override { return m_inner->AuthenticateClientToSession(s); }
    HRESULT STDMETHODCALLTYPE NotifySessionId(WRDS_SESSION_ID* s, HANDLE_PTR h) override { return m_inner->NotifySessionId(s, h); }
    HRESULT STDMETHODCALLTYPE GetInputHandles(HANDLE_PTR* k, HANDLE_PTR* m, HANDLE_PTR* b) override { return m_inner->GetInputHandles(k, m, b); }
    HRESULT STDMETHODCALLTYPE GetVideoHandle(HANDLE_PTR* v) override { return m_inner->GetVideoHandle(v); }
    HRESULT STDMETHODCALLTYPE ConnectNotify(ULONG s) override { return m_inner->ConnectNotify(s); }
    HRESULT STDMETHODCALLTYPE IsUserAllowedToLogon(ULONG s, HANDLE_PTR t, WCHAR* d, WCHAR* u) override { return m_inner->IsUserAllowedToLogon(s, t, d, u); }
    HRESULT STDMETHODCALLTYPE SessionArbitrationEnumeration(HANDLE_PTR t, BOOL single, ULONG* ids, ULONG* count) override { return m_inner->SessionArbitrationEnumeration(t, single, ids, count); }
    HRESULT STDMETHODCALLTYPE LogonNotify(HANDLE_PTR t, WCHAR* u, WCHAR* d, WRDS_SESSION_ID* s, PWRDS_CONNECTION_SETTINGS c) override { return m_inner->LogonNotify(t, u, d, s, c); }
    HRESULT STDMETHODCALLTYPE PreDisconnect(ULONG r) override { return m_inner->PreDisconnect(r); }
    HRESULT STDMETHODCALLTYPE DisconnectNotify() override { return m_inner->DisconnectNotify(); }
    HRESULT STDMETHODCALLTYPE Close() override { return m_inner->Close(); }
    HRESULT STDMETHODCALLTYPE GetProtocolStatus(WRDS_PROTOCOL_STATUS* p) override { return m_inner->GetProtocolStatus(p); }
    HRESULT STDMETHODCALLTYPE GetLastInputTime(ULONG64* t) override { return m_inner->GetLastInputTime(t); }
    HRESULT STDMETHODCALLTYPE SetErrorInfo(ULONG e) override { return m_inner->SetErrorInfo(e); }
    HRESULT STDMETHODCALLTYPE CreateVirtualChannel(CHAR* n, BOOL st, ULONG prio, ULONG_PTR* ch) override { return m_inner->CreateVirtualChannel(n, st, prio, ch); }
    HRESULT STDMETHODCALLTYPE QueryProperty(GUID q, ULONG nIn, ULONG nOut, PWRDS_PROPERTY_VALUE in, PWRDS_PROPERTY_VALUE out) override { return m_inner->QueryProperty(q, nIn, nOut, in, out); }
    HRESULT STDMETHODCALLTYPE GetShadowConnection(IWRdsProtocolShadowConnection** p) override { return m_inner->GetShadowConnection(p); }
    HRESULT STDMETHODCALLTYPE NotifyCommandProcessCreated(ULONG s) override { return m_inner->NotifyCommandProcessCreated(s); }

    // ---- IWRdsWddmIddProps / IWRdsWddmIddProps1: lo único propio ----
    HRESULT STDMETHODCALLTYPE GetHardwareId(WCHAR* id, DWORD count) override
    {
        if (!id || !count) return E_INVALIDARG;
        // Comprobar antes: wcscpy_s con búfer corto aborta el proceso (y esto corre en el
        // svchost de Terminal Services).
        constexpr size_t needed = ARRAYSIZE(REMOTEIDD_HARDWARE_ID);
        if (count < needed) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        memcpy(id, REMOTEIDD_HARDWARE_ID, needed * sizeof(wchar_t));
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnDriverLoad(ULONG sessionId, HANDLE_PTR driver) override
    {
        Log(L"OsmsRds: RemoteIdd cargado para la sesión %lu\n", sessionId);
        return m_sink.ToDriverHandle(Seat(sessionId), reinterpret_cast<HANDLE>(driver));
    }
    HRESULT STDMETHODCALLTYPE OnDriverLoad(ULONG sessionId, LPCWSTR deviceInstance) override
    {
        Log(L"OsmsRds: RemoteIdd cargado para la sesión %lu (%ls)\n", sessionId, deviceInstance ? deviceInstance : L"?");
        return m_sink.ToDeviceInstance(Seat(sessionId), deviceInstance);
    }
    HRESULT STDMETHODCALLTYPE OnDriverUnload(ULONG) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE EnableWddmIdd(BOOL enabled) override
    {
        // Se informa también a rdpcorets para que su estado sea coherente con la sesión.
        IWRdsWddmIddProps* props = nullptr;
        if (SUCCEEDED(m_inner->QueryInterface(__uuidof(IWRdsWddmIddProps), reinterpret_cast<void**>(&props))))
        {
            props->EnableWddmIdd(enabled);
            props->Release();
        }
        m_wddmIdd = enabled != FALSE;
        return S_OK;
    }

    bool WddmIddEnabled() const { return m_wddmIdd; }

private:
    REMOTEIDD_SET_SESSION Seat(ULONG sessionId) const
    {
        REMOTEIDD_SET_SESSION seat = {};
        seat.Version = REMOTEIDD_SET_SESSION_VERSION;
        seat.SessionId = sessionId;
        seat.Width = m_width;
        seat.Height = m_height;
        seat.RefreshHz = m_refreshHz;
        return seat;
    }

    IWRdsProtocolConnection* m_inner;
    SessionSink& m_sink;
    ULONG m_refreshHz;
    ULONG m_width = 0, m_height = 0;    // del cliente, al pasar por GetClientData
    bool m_wddmIdd = false;
};

// ============================================================ callback del listener
// rdpcorets avisa de cada conexión entrante; se envuelve antes de pasarla a Windows.
class ListenerCallback final : public RefCounted, public IWRdsProtocolListenerCallback
{
public:
    ListenerCallback(IWRdsProtocolListenerCallback* outer, SessionSink& sink, ULONG refreshHz)
        : m_outer(outer), m_sink(sink), m_refreshHz(refreshHz)
    {
        m_outer->AddRef();
    }
    ~ListenerCallback() override { m_outer->Release(); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid != __uuidof(IUnknown) && riid != __uuidof(IWRdsProtocolListenerCallback))
        {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        *ppv = static_cast<IWRdsProtocolListenerCallback*>(this);
        AddRef();
        return S_OK;
    }
    OSMS_IUNKNOWN

    HRESULT STDMETHODCALLTYPE OnConnected(IWRdsProtocolConnection* connection, PWRDS_CONNECTION_SETTINGS settings,
                                          IWRdsProtocolConnectionCallback** callback) override
    {
        if (!connection) return E_INVALIDARG;
        auto* wrapped = new (std::nothrow) Connection(connection, m_sink, m_refreshHz);
        if (!wrapped) return E_OUTOFMEMORY;
        HRESULT hr = m_outer->OnConnected(wrapped, settings, callback);
        wrapped->Release();
        return hr;
    }

private:
    IWRdsProtocolListenerCallback* m_outer;
    SessionSink& m_sink;
    ULONG m_refreshHz;
};

// ============================================================ listener
class Listener final : public RefCounted, public IWRdsProtocolListener
{
public:
    Listener(IWRdsProtocolListener* inner, SessionSink& sink, ULONG refreshHz)
        : m_inner(inner), m_sink(sink), m_refreshHz(refreshHz) { m_inner->AddRef(); }
    ~Listener() override { m_inner->Release(); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IWRdsProtocolListener))
        {
            *ppv = static_cast<IWRdsProtocolListener*>(this);
            AddRef();
            return S_OK;
        }
        return m_inner->QueryInterface(riid, ppv);
    }
    OSMS_IUNKNOWN

    HRESULT STDMETHODCALLTYPE GetSettings(WRDS_LISTENER_SETTING_LEVEL level, PWRDS_LISTENER_SETTINGS settings) override
    {
        return m_inner->GetSettings(level, settings);
    }
    HRESULT STDMETHODCALLTYPE StartListen(IWRdsProtocolListenerCallback* callback) override
    {
        if (!callback) return E_INVALIDARG;
        auto* wrapped = new (std::nothrow) ListenerCallback(callback, m_sink, m_refreshHz);
        if (!wrapped) return E_OUTOFMEMORY;
        HRESULT hr = m_inner->StartListen(wrapped);
        wrapped->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE StopListen() override { return m_inner->StopListen(); }

private:
    IWRdsProtocolListener* m_inner;
    SessionSink& m_sink;
    ULONG m_refreshHz;
};

// ============================================================ gestor
class ProtocolManager final : public RefCounted, public IWRdsProtocolManager
{
public:
    ProtocolManager(IWRdsProtocolManager* inner, SessionSink& sink) : m_inner(inner), m_sink(sink) { m_inner->AddRef(); }
    ~ProtocolManager() override { m_inner->Release(); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IWRdsProtocolManager))
        {
            *ppv = static_cast<IWRdsProtocolManager*>(this);
            AddRef();
            return S_OK;
        }
        return m_inner->QueryInterface(riid, ppv);
    }
    OSMS_IUNKNOWN

    HRESULT STDMETHODCALLTYPE Initialize(IWRdsProtocolSettings* s, PWRDS_SETTINGS w) override { return m_inner->Initialize(s, w); }
    HRESULT STDMETHODCALLTYPE CreateListener(WCHAR* name, IWRdsProtocolListener** listener) override
    {
        if (!listener) return E_POINTER;
        *listener = nullptr;
        IWRdsProtocolListener* inner = nullptr;
        HRESULT hr = m_inner->CreateListener(name, &inner);
        if (FAILED(hr)) return hr;
        auto* wrapped = new (std::nothrow) Listener(inner, m_sink, ReadListenerRefreshHz(name));
        inner->Release();
        if (!wrapped) return E_OUTOFMEMORY;
        *listener = wrapped;
        Log(L"OsmsRds: listener %ls\n", name ? name : L"?");
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE NotifyServiceStateChange(WRDS_SERVICE_STATE* s) override { return m_inner->NotifyServiceStateChange(s); }
    HRESULT STDMETHODCALLTYPE NotifySessionOfServiceStart(WRDS_SESSION_ID* s) override { return m_inner->NotifySessionOfServiceStart(s); }
    HRESULT STDMETHODCALLTYPE NotifySessionOfServiceStop(WRDS_SESSION_ID* s) override { return m_inner->NotifySessionOfServiceStop(s); }
    HRESULT STDMETHODCALLTYPE NotifySessionStateChange(WRDS_SESSION_ID* s, ULONG e) override { return m_inner->NotifySessionStateChange(s, e); }
    HRESULT STDMETHODCALLTYPE NotifySettingsChange(PWRDS_SETTINGS w) override { return m_inner->NotifySettingsChange(w); }
    HRESULT STDMETHODCALLTYPE Uninitialize() override { return m_inner->Uninitialize(); }

private:
    IWRdsProtocolManager* m_inner;
    SessionSink& m_sink;
};

// ============================================================ sesión -> driver
static HRESULT SendSession(HANDLE device, const REMOTEIDD_SET_SESSION& seat)
{
    REMOTEIDD_SET_SESSION in = seat;
    DWORD bytes = 0;
    if (!DeviceIoControl(device, IOCTL_REMOTEIDD_SET_SESSION, &in, sizeof(in), nullptr, 0, &bytes, nullptr))
    {
        DWORD err = GetLastError();
        Log(L"OsmsRds: IOCTL de sesión falló (%lu)\n", err);
        return HRESULT_FROM_WIN32(err);
    }
    return S_OK;
}

class IoctlSessionSink final : public SessionSink
{
public:
    HRESULT ToDriverHandle(const REMOTEIDD_SET_SESSION& seat, HANDLE driver) override
    {
        if (!driver || driver == INVALID_HANDLE_VALUE) return E_HANDLE;
        return SendSession(driver, seat);
    }

    HRESULT ToDeviceInstance(const REMOTEIDD_SET_SESSION& seat, LPCWSTR instance) override
    {
        if (!instance) return E_INVALIDARG;
        // Interfaz REMOTEIDD_INTERFACE_GUID de esa instancia concreta.
        ULONG len = 0;
        GUID guid = REMOTEIDD_INTERFACE_GUID;
        if (CM_Get_Device_Interface_List_SizeW(&len, &guid, const_cast<DEVINSTID_W>(instance),
                                               CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || len <= 1)
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        std::vector<wchar_t> list(len);
        if (CM_Get_Device_Interface_ListW(&guid, const_cast<DEVINSTID_W>(instance), list.data(), len,
                                          CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS)
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);

        HANDLE device = CreateFileW(list.data(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (device == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        HRESULT hr = SendSession(device, seat);
        CloseHandle(device);
        return hr;
    }
};

ULONG ReadListenerRefreshHz(LPCWSTR listenerName)
{
    if (!listenerName || !*listenerName) return 0;
    wchar_t key[160];
    swprintf_s(key, L"SYSTEM\\CurrentControlSet\\Control\\Terminal Server\\WinStations\\%ls", listenerName);
    DWORD hz = 0, size = sizeof(hz);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, key, L"OsmsRefreshHz", RRF_RT_REG_DWORD, nullptr, &hz, &size) != ERROR_SUCCESS)
        return 0;
    return hz;
}

SessionSink& DefaultSessionSink()
{
    static IoctlSessionSink sink;
    return sink;
}

HRESULT CreateProtocolManager(IWRdsProtocolManager* inner, SessionSink& sink, REFIID riid, void** ppv)
{
    if (!inner || !ppv) return E_POINTER;
    auto* mgr = new (std::nothrow) ProtocolManager(inner, sink);
    if (!mgr) return E_OUTOFMEMORY;
    HRESULT hr = mgr->QueryInterface(riid, ppv);
    mgr->Release();
    return hr;
}

HRESULT CreateInnerProtocolManager(IWRdsProtocolManager** inner)
{
    // El protocolo de RDP-Tcp, salvo que alguien haya puesto este proveedor ahí (no se
    // permite: se envolvería a sí mismo).
    CLSID clsid = CLSID_RdpCoreTsProtocolManager;
    wchar_t value[64] = {};
    DWORD size = sizeof(value);
    if (RegGetValueW(HKEY_LOCAL_MACHINE,
                     L"SYSTEM\\CurrentControlSet\\Control\\Terminal Server\\WinStations\\RDP-Tcp",
                     L"LoadableProtocol_Object", RRF_RT_REG_SZ, nullptr, value, &size) == ERROR_SUCCESS)
    {
        CLSID fromReg;
        if (SUCCEEDED(CLSIDFromString(value, &fromReg)) && fromReg != CLSID_OsmsRdsProtocolManager)
            clsid = fromReg;
    }
    return CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, __uuidof(IWRdsProtocolManager),
                            reinterpret_cast<void**>(inner));
}

// ============================================================ fábrica COM
static std::atomic<long> g_objects{ 0 };

class ClassFactory final : public IClassFactory
{
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }     // objeto estático
    ULONG STDMETHODCALLTYPE Release() override { return 1; }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        IWRdsProtocolManager* inner = nullptr;
        HRESULT hr = CreateInnerProtocolManager(&inner);
        if (FAILED(hr))
        {
            Log(L"OsmsRds: no se pudo crear el protocolo de Microsoft (0x%08X)\n", hr);
            return hr;
        }
        hr = CreateProtocolManager(inner, DefaultSessionSink(), riid, ppv);
        inner->Release();
        if (SUCCEEDED(hr)) ++g_objects;
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    {
        lock ? ++g_objects : --g_objects;
        return S_OK;
    }
};

} // namespace osms::rds

// ============================================================ exportaciones del DLL
static HMODULE g_module = nullptr;

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* ppv)
{
    static osms::rds::ClassFactory factory;
    if (clsid != osms::rds::CLSID_OsmsRdsProtocolManager) return CLASS_E_CLASSNOTAVAILABLE;
    return factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    // Terminal Services mantiene el gestor vivo mientras exista el listener.
    return S_FALSE;
}

// Solo registra la clase COM; el listener de puestos lo crea install.ps1 aparte.
STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());
    wchar_t clsid[64];
    StringFromGUID2(osms::rds::CLSID_OsmsRdsProtocolManager, clsid, ARRAYSIZE(clsid));
    wchar_t key[128];
    swprintf_s(key, L"SOFTWARE\\Classes\\CLSID\\%ls\\InprocServer32", clsid);
    HKEY h = nullptr;
    LSTATUS st = RegCreateKeyExW(HKEY_LOCAL_MACHINE, key, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &h, nullptr);
    if (st != ERROR_SUCCESS) return HRESULT_FROM_WIN32(st);
    st = RegSetValueExW(h, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(path),
                        static_cast<DWORD>((wcslen(path) + 1) * sizeof(wchar_t)));
    const wchar_t model[] = L"Both";
    if (st == ERROR_SUCCESS)
        st = RegSetValueExW(h, L"ThreadingModel", 0, REG_SZ, reinterpret_cast<const BYTE*>(model), sizeof(model));
    RegCloseKey(h);
    return HRESULT_FROM_WIN32(st);
}

STDAPI DllUnregisterServer()
{
    wchar_t clsid[64];
    StringFromGUID2(osms::rds::CLSID_OsmsRdsProtocolManager, clsid, ARRAYSIZE(clsid));
    wchar_t key[128];
    swprintf_s(key, L"SOFTWARE\\Classes\\CLSID\\%ls", clsid);
    LSTATUS st = RegDeleteTreeW(HKEY_LOCAL_MACHINE, key);
    return (st == ERROR_SUCCESS || st == ERROR_FILE_NOT_FOUND) ? S_OK : HRESULT_FROM_WIN32(st);
}
