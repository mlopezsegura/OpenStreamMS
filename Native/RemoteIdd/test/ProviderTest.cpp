// ProviderTest.cpp — test del proveedor de protocolo (Provider/RdsProvider.cpp).
//
// Sin registrar nada: objetos falsos hacen de rdpcorets (lo envuelto) y de Terminal
// Services (quien llama). Comprueba que:
//   1) todas las llamadas de IWRdsProtocol* llegan a rdpcorets sin cambios;
//   2) en un puesto (cliente 127.0.0.2) GetHardwareId pide RemoteIdd y OnDriverLoad (las dos
//      versiones) entrega sesión y resolución al driver;
//   3) en cualquier otra conexión todo, IDD incluido, sigue siendo de Microsoft (RdpIdd);
//   4) las interfaces del IDD solo se exponen si rdpcorets las tiene;
//   5) EnableWddmIdd se notifica a rdpcorets;
//   6) no quedan referencias colgando al soltarlo todo.
//
// Build/Run: ver build.cmd (RemoteIddProviderTest.exe; código 0 = OK).

#include "../Provider/RdsProvider.h"
#include <cstdio>
#include <string>

using namespace osms::rds;

namespace {

int g_fail = 0;
void Check(bool ok, const char* what)
{
    std::printf("[%s] %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) g_fail = 1;
}

// Recuento visible para comprobar fugas.
template <class I>
class Fake : public I
{
public:
    long refs = 1;
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }   // vive en la pila del test
};

class FakeConnection final : public Fake<IWRdsProtocolConnection>, public IWRdsWddmIddProps,
                             public IWRdsWddmIddProps1
{
public:
    FakeConnection(const wchar_t* clientAddress, bool hasIdd = true)
        : address(clientAddress), idd(hasIdd) {}

    const wchar_t* address;
    bool idd;
    int accepted = 0, closed = 0, monitorData = 0, enableCalls = 0, qiForwarded = 0;
    int driverLoads = 0, clientDataCalls = 0;
    BOOL enabled = FALSE;

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IWRdsProtocolConnection))
            *ppv = static_cast<IWRdsProtocolConnection*>(this);
        else if (idd && riid == __uuidof(IWRdsWddmIddProps))
            *ppv = static_cast<IWRdsWddmIddProps*>(this);
        else if (idd && riid == __uuidof(IWRdsWddmIddProps1))
            *ppv = static_cast<IWRdsWddmIddProps1*>(this);
        else { ++qiForwarded; *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }

    HRESULT STDMETHODCALLTYPE GetLogonErrorRedirector(IWRdsProtocolLogonErrorRedirector**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE AcceptConnection() override { ++accepted; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetClientData(WRDS_CLIENT_DATA* d) override
    {
        ++clientDataCalls;
        d->HRes = 2560; d->VRes = 1440;
        wcscpy_s(d->ClientAddress, address);
        wcscpy_s(d->Password, L"no-debe-salir");   // el proveedor no debe copiarla
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetClientMonitorData(UINT* n, UINT* p) override { ++monitorData; *n = 1; *p = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetUserCredentials(WRDS_USER_CREDENTIAL*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetLicenseConnection(IWRdsProtocolLicenseConnection**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE AuthenticateClientToSession(WRDS_SESSION_ID*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE NotifySessionId(WRDS_SESSION_ID*, HANDLE_PTR) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetInputHandles(HANDLE_PTR*, HANDLE_PTR*, HANDLE_PTR*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetVideoHandle(HANDLE_PTR*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE ConnectNotify(ULONG) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE IsUserAllowedToLogon(ULONG, HANDLE_PTR, WCHAR*, WCHAR*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SessionArbitrationEnumeration(HANDLE_PTR, BOOL, ULONG*, ULONG*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE LogonNotify(HANDLE_PTR, WCHAR*, WCHAR*, WRDS_SESSION_ID*, PWRDS_CONNECTION_SETTINGS) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE PreDisconnect(ULONG) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE DisconnectNotify() override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Close() override { ++closed; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetProtocolStatus(WRDS_PROTOCOL_STATUS*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetLastInputTime(ULONG64*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SetErrorInfo(ULONG) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE CreateVirtualChannel(CHAR*, BOOL, ULONG, ULONG_PTR*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE QueryProperty(GUID, ULONG, ULONG, PWRDS_PROPERTY_VALUE, PWRDS_PROPERTY_VALUE) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetShadowConnection(IWRdsProtocolShadowConnection**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE NotifyCommandProcessCreated(ULONG) override { return E_NOTIMPL; }

    // rdpcorets pide su propio IDD.
    HRESULT STDMETHODCALLTYPE GetHardwareId(WCHAR* id, DWORD count) override
    {
        if (count < ARRAYSIZE(L"RdpIdd_IndirectDisplay")) return E_INVALIDARG;
        wcscpy_s(id, count, L"RdpIdd_IndirectDisplay");
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnDriverLoad(ULONG, HANDLE_PTR) override { ++driverLoads; return S_FALSE; }
    HRESULT STDMETHODCALLTYPE OnDriverLoad(ULONG, LPCWSTR) override { ++driverLoads; return S_FALSE; }
    HRESULT STDMETHODCALLTYPE OnDriverUnload(ULONG) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE EnableWddmIdd(BOOL e) override { ++enableCalls; enabled = e; return S_OK; }
};

class FakeListener final : public Fake<IWRdsProtocolListener>
{
public:
    IWRdsProtocolListenerCallback* callback = nullptr;
    int stops = 0;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** ppv) override { *ppv = nullptr; return E_NOINTERFACE; }
    HRESULT STDMETHODCALLTYPE GetSettings(WRDS_LISTENER_SETTING_LEVEL, PWRDS_LISTENER_SETTINGS) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE StartListen(IWRdsProtocolListenerCallback* cb) override { callback = cb; cb->AddRef(); return S_OK; }
    HRESULT STDMETHODCALLTYPE StopListen() override
    {
        ++stops;
        if (callback) { callback->Release(); callback = nullptr; }
        return S_OK;
    }
};

class FakeManager final : public Fake<IWRdsProtocolManager>
{
public:
    FakeListener listener;
    int initialized = 0, uninitialized = 0, settings = 0;
    std::wstring listenerName;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** ppv) override { *ppv = nullptr; return E_NOINTERFACE; }
    HRESULT STDMETHODCALLTYPE Initialize(IWRdsProtocolSettings*, PWRDS_SETTINGS) override { ++initialized; return S_OK; }
    HRESULT STDMETHODCALLTYPE CreateListener(WCHAR* name, IWRdsProtocolListener** out) override
    {
        listenerName = name;
        listener.AddRef();
        *out = &listener;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE NotifyServiceStateChange(WRDS_SERVICE_STATE*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE NotifySessionOfServiceStart(WRDS_SESSION_ID*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE NotifySessionOfServiceStop(WRDS_SESSION_ID*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE NotifySessionStateChange(WRDS_SESSION_ID*, ULONG) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE NotifySettingsChange(PWRDS_SETTINGS) override { ++settings; return S_OK; }
    HRESULT STDMETHODCALLTYPE Uninitialize() override { ++uninitialized; return S_OK; }
};

// Terminal Services: recibe la conexión ya envuelta.
class FakeTermService final : public Fake<IWRdsProtocolListenerCallback>
{
public:
    IWRdsProtocolConnection* received = nullptr;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** ppv) override { *ppv = nullptr; return E_NOINTERFACE; }
    HRESULT STDMETHODCALLTYPE OnConnected(IWRdsProtocolConnection* c, PWRDS_CONNECTION_SETTINGS, IWRdsProtocolConnectionCallback** cb) override
    {
        received = c;
        c->AddRef();
        if (cb) *cb = nullptr;
        return S_OK;
    }
};

class RecordingSink final : public SessionSink
{
public:
    REMOTEIDD_SET_SESSION handleSeat = {}, instanceSeat = {};
    HANDLE handle = nullptr;
    std::wstring instance;
    HRESULT ToDriverHandle(const REMOTEIDD_SET_SESSION& s, HANDLE h) override { handleSeat = s; handle = h; return S_OK; }
    HRESULT ToDeviceInstance(const REMOTEIDD_SET_SESSION& s, LPCWSTR i) override { instanceSeat = s; instance = i; return S_OK; }
};

} // namespace

int main()
{
    std::printf("RemoteIdd ProviderTest: envoltorio de rdpcorets con objetos falsos\n");

    FakeManager inner;
    FakeConnection rdpConnection(REMOTEIDD_SEAT_CLIENT_ADDRESS);   // puesto de OpenStreamMS
    FakeConnection lanConnection(L"192.168.1.50");                 // RDP normal desde la red
    FakeConnection noIddConnection(L"192.168.1.51", false);        // rdpcorets sin interfaces de IDD
    FakeTermService termsrv;
    RecordingSink sink;

    {
        IWRdsProtocolManager* mgr = nullptr;
        Check(SUCCEEDED(CreateProtocolManager(&inner, sink, __uuidof(IWRdsProtocolManager), reinterpret_cast<void**>(&mgr))) && mgr,
              "se crea el gestor envolviendo al de Microsoft");
        mgr->Initialize(nullptr, nullptr);
        mgr->NotifySettingsChange(nullptr);
        Check(inner.initialized == 1 && inner.settings == 1, "Initialize y NotifySettingsChange llegan a rdpcorets");

        wchar_t name[] = L"RDP-Tcp";
        IWRdsProtocolListener* listener = nullptr;
        Check(SUCCEEDED(mgr->CreateListener(name, &listener)) && listener && listener != &inner.listener,
              "CreateListener devuelve un listener envuelto");
        Check(inner.listenerName == L"RDP-Tcp", "el nombre del listener llega sin cambios");

        Check(SUCCEEDED(listener->StartListen(&termsrv)) && inner.listener.callback && inner.listener.callback != &termsrv,
              "StartListen pasa a rdpcorets un callback envuelto");

        // rdpcorets acepta una conexión TCP y avisa.
        inner.listener.callback->OnConnected(&rdpConnection, nullptr, nullptr);
        IWRdsProtocolConnection* conn = termsrv.received;
        Check(conn && conn != &rdpConnection, "Terminal Services recibe la conexión envuelta");

        if (conn)
        {
            conn->AcceptConnection();
            UINT n = 0, p = 0;
            conn->GetClientMonitorData(&n, &p);
            Check(rdpConnection.accepted == 1 && rdpConnection.monitorData == 1 && n == 1,
                  "las llamadas de la conexión llegan a rdpcorets con sus resultados");

            IWRdsWddmIddProps* props = nullptr;
            Check(SUCCEEDED(conn->QueryInterface(__uuidof(IWRdsWddmIddProps), reinterpret_cast<void**>(&props))) && props,
                  "la conexión expone IWRdsWddmIddProps");
            // Antes de GetClientData: el proveedor pide él mismo los datos para decidir.
            wchar_t hwid[128] = {};
            props->GetHardwareId(hwid, ARRAYSIZE(hwid));
            Check(wcscmp(hwid, REMOTEIDD_HARDWARE_ID) == 0, "en un puesto, GetHardwareId pide RemoteIdd, no RdpIdd");
            Check(rdpConnection.clientDataCalls == 1, "y para saberlo consulta una vez los datos del cliente");
            Check(props->GetHardwareId(hwid, 4) != S_OK, "GetHardwareId rechaza un búfer corto");

            // Terminal Services pide los datos del cliente antes de cargar el driver.
            auto* clientData = new WRDS_CLIENT_DATA();
            Check(SUCCEEDED(conn->GetClientData(clientData)) && clientData->HRes == 2560,
                  "GetClientData llega a rdpcorets y devuelve sus datos intactos");
            SecureZeroMemory(clientData, sizeof(*clientData));
            delete clientData;

            props->OnDriverLoad(7, reinterpret_cast<HANDLE_PTR>(reinterpret_cast<HANDLE>(0x1234)));
            Check(sink.handleSeat.SessionId == 7 && sink.handle == reinterpret_cast<HANDLE>(0x1234) &&
                  rdpConnection.driverLoads == 0,
                  "OnDriverLoad(handle) entrega la sesión al driver, no a rdpcorets");
            Check(sink.handleSeat.Version == REMOTEIDD_SET_SESSION_VERSION &&
                  sink.handleSeat.Width == 2560 && sink.handleSeat.Height == 1440,
                  "y con ella la resolución que pidió el cliente");

            props->EnableWddmIdd(TRUE);
            Check(rdpConnection.enableCalls == 1 && rdpConnection.enabled == TRUE,
                  "EnableWddmIdd también se notifica a rdpcorets");
            props->Release();

            IWRdsWddmIddProps1* props1 = nullptr;
            Check(SUCCEEDED(conn->QueryInterface(__uuidof(IWRdsWddmIddProps1), reinterpret_cast<void**>(&props1))) && props1,
                  "la conexión expone IWRdsWddmIddProps1");
            if (props1)
            {
                props1->OnDriverLoad(8, L"SWD\\RemoteIdd\\0001");
                Check(sink.instanceSeat.SessionId == 8 && sink.instance == L"SWD\\RemoteIdd\\0001",
                      "OnDriverLoad(instancia) entrega la sesión al driver");
                props1->Release();
            }

            IUnknown* other = nullptr;
            conn->QueryInterface(__uuidof(IWRdsProtocolConnectionSettings), reinterpret_cast<void**>(&other));
            Check(rdpConnection.qiForwarded == 1, "las demás interfaces se piden a rdpcorets");

            conn->Close();
            Check(rdpConnection.closed == 1, "Close llega a rdpcorets");
            termsrv.received = nullptr;
            conn->Release();
        }

        // Una conexión RDP normal (no de puesto): el IDD sigue siendo el de Microsoft.
        inner.listener.callback->OnConnected(&lanConnection, nullptr, nullptr);
        if (IWRdsProtocolConnection* lan = termsrv.received)
        {
            IWRdsWddmIddProps* props = nullptr;
            lan->QueryInterface(__uuidof(IWRdsWddmIddProps), reinterpret_cast<void**>(&props));
            wchar_t hwid[128] = {};
            if (props) props->GetHardwareId(hwid, ARRAYSIZE(hwid));
            Check(wcscmp(hwid, L"RdpIdd_IndirectDisplay") == 0, "fuera de los puestos, GetHardwareId es el de Microsoft (RdpIdd)");
            ULONG before = sink.handleSeat.SessionId;
            HRESULT hr = props ? props->OnDriverLoad(11, 0) : E_FAIL;
            Check(hr == S_FALSE && lanConnection.driverLoads == 1 && sink.handleSeat.SessionId == before,
                  "fuera de los puestos, OnDriverLoad va a rdpcorets y no al driver propio");
            if (props) props->Release();

            IWRdsWddmIddProps1* props1 = nullptr;
            lan->QueryInterface(__uuidof(IWRdsWddmIddProps1), reinterpret_cast<void**>(&props1));
            std::wstring instanceBefore = sink.instance;
            if (props1) { props1->OnDriverLoad(12, L"SWD\\RdpIdd\\0001"); props1->Release(); }
            Check(lanConnection.driverLoads == 2 && sink.instance == instanceBefore,
                  "y también OnDriverLoad por instancia");
            termsrv.received = nullptr;
            lan->Release();
        }
        else
            Check(false, "la conexión normal llega a Terminal Services");

        // Si rdpcorets no tiene interfaces de IDD, el envoltorio tampoco las ofrece.
        inner.listener.callback->OnConnected(&noIddConnection, nullptr, nullptr);
        if (IWRdsProtocolConnection* c = termsrv.received)
        {
            IUnknown* props = nullptr;
            Check(FAILED(c->QueryInterface(__uuidof(IWRdsWddmIddProps), reinterpret_cast<void**>(&props))) && !props,
                  "sin IDD en rdpcorets, el envoltorio no expone IWRdsWddmIddProps");
            termsrv.received = nullptr;
            c->Release();
        }

        listener->StopListen();
        Check(inner.listener.stops == 1, "StopListen llega a rdpcorets");
        listener->Release();
        mgr->Uninitialize();
        Check(inner.uninitialized == 1, "Uninitialize llega a rdpcorets");
        mgr->Release();
    }

    Check(inner.refs == 1 && inner.listener.refs == 1 && rdpConnection.refs == 1 && termsrv.refs == 1 &&
          lanConnection.refs == 1 && noIddConnection.refs == 1,
          "sin referencias colgando al soltarlo todo");

    // El sink real con un handle inválido falla limpio (sin driver no hay a quién avisar).
    REMOTEIDD_SET_SESSION seat = { REMOTEIDD_SET_SESSION_VERSION, 9 };
    Check(FAILED(DefaultSessionSink().ToDriverHandle(seat, INVALID_HANDLE_VALUE)), "el IOCTL real rechaza un handle inválido");
    Check(FAILED(DefaultSessionSink().ToDeviceInstance(seat, L"SWD\\NoExiste\\0")), "sin dispositivo, la ruta por instancia falla limpio");
    Check(ReadListenerRefreshHz(L"NoExisteOsms") == 0, "sin OsmsRefreshHz en el listener, Hz del driver (0)");

    std::printf("\nRESULTADO: %s\n", g_fail ? "FALLO" : "OK");
    return g_fail;
}
