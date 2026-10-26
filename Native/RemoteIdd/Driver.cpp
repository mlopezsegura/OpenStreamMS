// Driver.cpp — RemoteIdd, driver IddCx de sesión remota (spike).
//
// ⚠️ NO FUNCIONAL POR SÍ SOLO. IddCxAdapterInitAsync falla si el adaptador no lo creó
// la pila de escritorio remoto que atiende la sesión. Sin un proveedor de protocolo RDS
// propio (equivalente a DuoRds.dll) este driver no llega a inicializarse. Ver README.md.
//
// Estructura basada en el sample oficial Windows-driver-samples/video/IndirectDisplay,
// adaptada a sesión remota + entrega de frames por memoria compartida (SharedFrame.h).
//
// Mínima invasión: no escribe registro ni ficheros, no engancha nada de Windows. Todo lo
// que crea (objetos con nombre, textura compartida) muere con el WUDFHost del driver.

#include "Driver.h"
#include <atomic>
#include <cstdio>
#include <new>
#include <utility>

using namespace Microsoft::WRL;
namespace osms::ridd {

// ------------------------------------------------------------------ util
static void Log(const wchar_t* fmt, ...)
{
    wchar_t buf[256];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(buf, _TRUNCATE, fmt, args);
    va_end(args);
    OutputDebugStringW(buf);
}

// B2: sesión del puesto. El WUDFHost corre en la sesión 0, así que no se puede deducir:
// la envía el proveedor de protocolo con IOCTL_REMOTEIDD_SET_SESSION al cargar el driver.
// 0 = aún desconocida (la sesión 0 nunca es un puesto). Hay un WUDFHost por dispositivo
// (ProcessSharingDisabled) y un dispositivo por sesión, así que basta con un global.
static std::atomic<UINT32> g_seatSession{ 0 };

// ------------------------------------------------------------------ modos (B3)
// Sin EDID: el monitor se crea con descripción vacía e IddCx pide los modos por
// EvtMonitorGetDefaultModes. Con USE_SMALLEST_MODE Windows arranca en el más pequeño y
// el modo real lo fija IddCxAdapterDisplayConfigUpdate (resolución del cliente).
struct ModeDef { DWORD Width, Height, VSync; };
static const ModeDef kModes[] = {
    { 3840, 2160,  60 }, { 3840, 2160, 120 },
    { 2560, 1440,  60 }, { 2560, 1440, 120 }, { 2560, 1440, 144 },
    { 1920, 1080,  60 }, { 1920, 1080, 120 }, { 1920, 1080, 144 }, { 1920, 1080, 240 },
    { 1600,  900,  60 },
    { 1280,  720,  60 }, { 1280,  720, 120 },
    { 1024,  768,  60 },
    {  800,  600,  60 },
};
constexpr UINT kModeCount = ARRAYSIZE(kModes);
constexpr ModeDef kDefaultConfig = { 1920, 1080, 60 };

// Modo del puesto: el que pide el cliente RDP (IOCTL v2) o kDefaultConfig. Si no está en
// kModes se añade al final de la lista. g_adapter/g_monitor permiten aplicarlo aunque el
// IOCTL llegue con el monitor ya creado. Todo bajo g_modeLock.
static SRWLOCK g_modeLock = SRWLOCK_INIT;
static ModeDef g_seatMode = kDefaultConfig;
static IDDCX_ADAPTER g_adapter = nullptr;
static IDDCX_MONITOR g_monitor = nullptr;

static bool SameMode(const ModeDef& a, const ModeDef& b)
{
    return a.Width == b.Width && a.Height == b.Height && a.VSync == b.VSync;
}

// Lista de modos (kModes + el del puesto si falta). Devuelve cuántos y cuál es el preferido.
static UINT BuildModes(ModeDef (&out)[kModeCount + 1], UINT* preferred)
{
    AcquireSRWLockShared(&g_modeLock);
    ModeDef seat = g_seatMode;
    ReleaseSRWLockShared(&g_modeLock);

    UINT n = 0;
    *preferred = kModeCount;
    for (const auto& m : kModes)
    {
        if (SameMode(m, seat)) *preferred = n;
        out[n++] = m;
    }
    if (*preferred == kModeCount)
        out[n++] = seat;            // preferred ya apunta a esa última posición
    return n;
}

static void FillSignalInfo(DISPLAYCONFIG_VIDEO_SIGNAL_INFO& mode, const ModeDef& m, bool monitorMode)
{
    mode.totalSize.cx = mode.activeSize.cx = m.Width;
    mode.totalSize.cy = mode.activeSize.cy = m.Height;
    // Monitor: divisor 0; destino: 1 (igual que el sample de Microsoft).
    mode.AdditionalSignalInfo.vSyncFreqDivider = monitorMode ? 0 : 1;
    mode.AdditionalSignalInfo.videoStandard = 255;
    mode.vSyncFreq.Numerator = m.VSync;
    mode.vSyncFreq.Denominator = 1;
    mode.hSyncFreq.Numerator = m.VSync * m.Height;
    mode.hSyncFreq.Denominator = 1;
    mode.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
    mode.pixelRate = static_cast<UINT64>(m.VSync) * m.Width * m.Height;
}

static IDDCX_MONITOR_MODE MonitorMode(const ModeDef& m, IDDCX_MONITOR_MODE_ORIGIN origin)
{
    IDDCX_MONITOR_MODE mode = {};
    mode.Size = sizeof(mode);
    mode.Origin = origin;
    FillSignalInfo(mode.MonitorVideoSignalInfo, m, true);
    return mode;
}

static IDDCX_TARGET_MODE TargetMode(const ModeDef& m)
{
    IDDCX_TARGET_MODE mode = {};
    mode.Size = sizeof(mode);
    FillSignalInfo(mode.TargetVideoSignalInfo.targetVideoSignalInfo, m, false);
    return mode;
}

// Lleva el modo del puesto a Windows: actualiza la lista de modos del monitor y fija la
// resolución de la sesión. Sin monitor todavía no hace nada (lo hará EvtAdapterInitFinished).
static NTSTATUS ApplySeatMode()
{
    AcquireSRWLockShared(&g_modeLock);
    IDDCX_ADAPTER adapter = g_adapter;
    IDDCX_MONITOR monitor = g_monitor;
    ModeDef seat = g_seatMode;
    ReleaseSRWLockShared(&g_modeLock);
    if (!adapter || !monitor) return STATUS_SUCCESS;

    ModeDef modes[kModeCount + 1];
    UINT preferred = 0;
    UINT n = BuildModes(modes, &preferred);
    IDDCX_TARGET_MODE targets[kModeCount + 1];
    for (UINT i = 0; i < n; ++i) targets[i] = TargetMode(modes[i]);

    IDARG_IN_UPDATEMODES um = {};
    um.Reason = IDDCX_UPDATE_REASON_OTHER;
    um.TargetModeCount = n;
    um.pTargetModes = targets;
    NTSTATUS st = IddCxMonitorUpdateModes(monitor, &um);
    if (!NT_SUCCESS(st))
    {
        Log(L"RemoteIdd: IddCxMonitorUpdateModes 0x%08X\n", st);
        return st;
    }

    // En un IDD remoto la resolución de la sesión la fija el driver con este update.
    IDDCX_DISPLAYCONFIGPATH path = {};
    path.Size = sizeof(path);
    path.MonitorObject = monitor;
    path.Position = { 0, 0 };
    path.Resolution = { seat.Width, seat.Height };
    path.Rotation = DISPLAYCONFIG_ROTATION_IDENTITY;
    path.RefreshRate = { seat.VSync, 1 };
    path.VSyncFreqDivider = 1;
    path.MonitorScaleFactor = 100;

    IDARG_IN_ADAPTERDISPLAYCONFIGUPDATE upd = {};
    upd.PathCount = 1;
    upd.pPaths = &path;
    st = IddCxAdapterDisplayConfigUpdate(adapter, &upd);
    Log(L"RemoteIdd: modo %lux%lu@%lu (0x%08X)\n", seat.Width, seat.Height, seat.VSync, st);
    return st;
}

// ============================================================ SwapChainProcessor
SwapChainProcessor::SwapChainProcessor(IDDCX_SWAPCHAIN swapChain, ComPtr<ID3D11Device> device,
                                       ComPtr<ID3D11DeviceContext> context,
                                       HANDLE newFrameEvent)
    : m_swapChain(swapChain), m_device(device), m_newFrameEvent(newFrameEvent),
      m_publisher(device, std::move(context), 0)
{
    m_terminate = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    m_thread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
}

SwapChainProcessor::~SwapChainProcessor()
{
    if (m_terminate) SetEvent(m_terminate);
    if (m_thread) { WaitForSingleObject(m_thread, INFINITE); CloseHandle(m_thread); }
    if (m_terminate) CloseHandle(m_terminate);
}

DWORD CALLBACK SwapChainProcessor::ThreadProc(LPVOID ctx)
{
    // Prioridad multimedia para el hilo de distribución de frames (como el sample).
    DWORD taskIndex = 0;
    HANDLE avrt = AvSetMmThreadCharacteristicsW(L"Distribution", &taskIndex);
    reinterpret_cast<SwapChainProcessor*>(ctx)->Run();
    if (avrt) AvRevertMmThreadCharacteristics(avrt);
    return 0;
}

void SwapChainProcessor::Run()
{
    // B4: no se reintenta aquí. Si el swapchain se pierde (DXGI_ERROR_ACCESS_LOST, cambio
    // de adaptador de render...), al borrarlo IddCx llama a Unassign y vuelve a llamar a
    // Assign con un swapchain y un dispositivo nuevos. Reintentar sobre el viejo no sirve.
    Exit why = RunCore();
    if (why == Exit::SwapChainLost)
        Log(L"RemoteIdd: swapchain perdido; IddCx reasignará uno nuevo\n");

    WdfObjectDelete(reinterpret_cast<WDFOBJECT>(m_swapChain));
    m_swapChain = nullptr;
}

SwapChainProcessor::Exit SwapChainProcessor::RunCore()
{
    ComPtr<IDXGIDevice> dxgiDevice;
    if (FAILED(m_device.As(&dxgiDevice))) return Exit::SwapChainLost;

    IDARG_IN_SWAPCHAINSETDEVICE setDevice = {};
    setDevice.pDevice = dxgiDevice.Get();
    if (FAILED(IddCxSwapChainSetDevice(m_swapChain, &setDevice)))
        return Exit::SwapChainLost;

    HANDLE waits[] = { m_newFrameEvent, m_terminate };
    for (;;)
    {
        IDARG_OUT_RELEASEANDACQUIREBUFFER buffer = {};
        HRESULT hr = IddCxSwapChainReleaseAndAcquireBuffer(m_swapChain, &buffer);

        if (hr == E_PENDING)
        {
            // Sin frame nuevo: espera al evento o a terminar. Timeout defensivo 16 ms.
            DWORD w = WaitForMultipleObjects(ARRAYSIZE(waits), waits, FALSE, 16);
            if (w == WAIT_OBJECT_0 + 1) return Exit::Terminate;
            if (w != WAIT_OBJECT_0 && w != WAIT_TIMEOUT) return Exit::SwapChainLost;
            continue;
        }
        if (FAILED(hr))
            return Exit::SwapChainLost;     // DXGI_ERROR_ACCESS_LOST incluido

        // ---- Hay un frame nuevo en buffer.MetaData.pSurface ----
        const auto& md = buffer.MetaData;
        if (md.pSurface && !md.HwProtectedSurface)    // contenido protegido: no se copia
        {
            // Sin sesión no se publica: con nombres OsmsRidd0* dos puestos se pisarían.
            UINT32 session = g_seatSession.load(std::memory_order_relaxed);
            ComPtr<ID3D11Texture2D> src;
            if (session && SUCCEEDED(md.pSurface->QueryInterface(IID_PPV_ARGS(&src))))
            {
                m_publisher.SetSession(session);
                m_publisher.Publish(src.Get(), md.PresentDisplayQPCTime);
            }
        }

        // Limitador por llegada: NO esperamos a que Sunshine termine; soltamos el buffer
        // de vuelta a IddCx de inmediato para no frenar la composición de la sesión.
        hr = IddCxSwapChainFinishedProcessingFrame(m_swapChain);
        if (FAILED(hr)) return Exit::SwapChainLost;

        if (WaitForSingleObject(m_terminate, 0) == WAIT_OBJECT_0) return Exit::Terminate;
    }
}

// ============================================================ IddCx: adaptador
NTSTATUS EvtAdapterInitFinished(IDDCX_ADAPTER adapter, const IDARG_IN_ADAPTER_INIT_FINISHED* args)
{
    if (!NT_SUCCESS(args->AdapterInitStatus)) return args->AdapterInitStatus;

    auto* ac = WdfObjectGet_AdapterContext(adapter);

    // Sin EDID (descripción vacía): IddCx usará EvtMonitorGetDefaultModes.
    IDDCX_MONITOR_INFO info = {};
    info.Size = sizeof(info);
    info.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED;
    info.ConnectorIndex = 0;
    info.MonitorDescription.Size = sizeof(info.MonitorDescription);
    info.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    info.MonitorDescription.DataSize = 0;
    info.MonitorDescription.pData = nullptr;
    // Container id fijo: el puesto es siempre el mismo "monitor" para Windows.
    info.MonitorContainerId = { 0x6f1c2a4e, 0x8b3d, 0x4f5a, { 0x9e, 0x21, 0x7c, 0x44, 0xd0, 0x1b, 0x5a, 0x83 } };

    WDF_OBJECT_ATTRIBUTES attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attr, MonitorContext);
    attr.EvtCleanupCallback = EvtMonitorCleanup;

    IDARG_IN_MONITORCREATE mcIn = {};
    mcIn.ObjectAttributes = &attr;
    mcIn.pMonitorInfo = &info;

    IDARG_OUT_MONITORCREATE mcOut = {};
    NTSTATUS st = IddCxMonitorCreate(adapter, &mcIn, &mcOut);
    if (!NT_SUCCESS(st)) return st;

    auto* mc = WdfObjectGet_MonitorContext(mcOut.MonitorObject);
    mc->Monitor = mcOut.MonitorObject;
    mc->Owner = ac;

    IDARG_OUT_MONITORARRIVAL arrival = {};
    st = IddCxMonitorArrival(mcOut.MonitorObject, &arrival);
    if (!NT_SUCCESS(st)) return st;

    AcquireSRWLockExclusive(&g_modeLock);
    g_adapter = adapter;
    g_monitor = mcOut.MonitorObject;
    ReleaseSRWLockExclusive(&g_modeLock);

    // Modo del cliente si el proveedor ya lo envió; si no, kDefaultConfig hasta el IOCTL.
    return ApplySeatMode();
}

NTSTATUS EvtAdapterCommitModes(IDDCX_ADAPTER, const IDARG_IN_COMMITMODES*)
{
    // Aceptamos la configuración de modos propuesta por el SO sin cambios.
    return STATUS_SUCCESS;
}

// ============================================================ IddCx: monitor / modos
NTSTATUS EvtParseMonitorDescription(const IDARG_IN_PARSEMONITORDESCRIPTION* in,
                                    IDARG_OUT_PARSEMONITORDESCRIPTION* out)
{
    // No se entrega EDID, pero si el SO llegara a pedirlo se responde con la misma lista.
    ModeDef modes[kModeCount + 1];
    UINT preferred = 0;
    UINT n = BuildModes(modes, &preferred);
    out->MonitorModeBufferOutputCount = n;
    out->PreferredMonitorModeIdx = preferred;
    if (in->MonitorModeBufferInputCount < n)
        return in->MonitorModeBufferInputCount > 0 ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;

    for (UINT i = 0; i < n; ++i)
        in->pMonitorModes[i] = MonitorMode(modes[i], IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR);
    return STATUS_SUCCESS;
}

NTSTATUS EvtMonitorGetDefaultModes(IDDCX_MONITOR, const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* in,
                                   IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* out)
{
    ModeDef modes[kModeCount + 1];
    UINT preferred = 0;
    UINT n = BuildModes(modes, &preferred);
    out->DefaultMonitorModeBufferOutputCount = n;
    out->PreferredMonitorModeIdx = preferred;
    if (in->DefaultMonitorModeBufferInputCount < n)
        return in->DefaultMonitorModeBufferInputCount > 0 ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;

    for (UINT i = 0; i < n; ++i)
        in->pDefaultMonitorModes[i] = MonitorMode(modes[i], IDDCX_MONITOR_MODE_ORIGIN_DRIVER);
    return STATUS_SUCCESS;
}

NTSTATUS EvtMonitorQueryTargetModes(IDDCX_MONITOR, const IDARG_IN_QUERYTARGETMODES* in,
                                    IDARG_OUT_QUERYTARGETMODES* out)
{
    ModeDef modes[kModeCount + 1];
    UINT preferred = 0;
    UINT n = BuildModes(modes, &preferred);
    out->TargetModeBufferOutputCount = n;
    if (in->TargetModeBufferInputCount < n)
        return in->TargetModeBufferInputCount > 0 ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;

    for (UINT i = 0; i < n; ++i)
        in->pTargetModes[i] = TargetMode(modes[i]);
    return STATUS_SUCCESS;
}

NTSTATUS EvtMonitorAssignSwapChain(IDDCX_MONITOR monitor, const IDARG_IN_SETSWAPCHAIN* in)
{
    auto* mc = WdfObjectGet_MonitorContext(monitor);

    // Abrimos el adaptador de render que indica IddCx y creamos el dispositivo D3D11.
    ComPtr<IDXGIFactory5> factory;
    if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)))) return STATUS_UNSUCCESSFUL;

    ComPtr<IDXGIAdapter1> adapter;
    if (FAILED(factory->EnumAdapterByLuid(in->RenderAdapterLuid, IID_PPV_ARGS(&adapter))))
        return STATUS_UNSUCCESSFUL;

    ComPtr<ID3D11Device> d3d; ComPtr<ID3D11DeviceContext> ctx;
    if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                                 D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
                                 D3D11_SDK_VERSION, &d3d, nullptr, &ctx)))
        return STATUS_UNSUCCESSFUL;

    // A1: un solo procesador por monitor; Unassign o EvtMonitorCleanup lo liberan.
    delete mc->Processor;
    mc->Processor = new (std::nothrow) SwapChainProcessor(
        in->hSwapChain, d3d, ctx, in->hNextSurfaceAvailable);
    return mc->Processor ? STATUS_SUCCESS : STATUS_NO_MEMORY;
}

NTSTATUS EvtMonitorUnassignSwapChain(IDDCX_MONITOR monitor)
{
    // El destructor para el hilo, borra el swapchain y cierra los objetos compartidos.
    auto* mc = WdfObjectGet_MonitorContext(monitor);
    delete mc->Processor;
    mc->Processor = nullptr;
    return STATUS_SUCCESS;
}

void EvtMonitorCleanup(WDFOBJECT object)
{
    auto* mc = WdfObjectGet_MonitorContext(object);
    delete mc->Processor;
    mc->Processor = nullptr;

    AcquireSRWLockExclusive(&g_modeLock);
    if (g_monitor == mc->Monitor) g_monitor = nullptr;
    ReleaseSRWLockExclusive(&g_modeLock);
}

// ============================================================ WDF: device add
NTSTATUS EvtDeviceAdd(WDFDRIVER, PWDFDEVICE_INIT deviceInit)
{
    // A2: IddCxCheckOsFeatureSupport no existe hasta IddCx 1.11; con la base 1.4 no aplica.

    // --- Inicializar el device para IddCx ---
    IDD_CX_CLIENT_CONFIG cfg;
    IDD_CX_CLIENT_CONFIG_INIT(&cfg);
    cfg.EvtIddCxAdapterInitFinished              = EvtAdapterInitFinished;
    cfg.EvtIddCxAdapterCommitModes               = EvtAdapterCommitModes;
    cfg.EvtIddCxParseMonitorDescription          = EvtParseMonitorDescription;
    cfg.EvtIddCxMonitorGetDefaultDescriptionModes= EvtMonitorGetDefaultModes;
    cfg.EvtIddCxMonitorQueryTargetModes          = EvtMonitorQueryTargetModes;
    cfg.EvtIddCxMonitorAssignSwapChain           = EvtMonitorAssignSwapChain;
    cfg.EvtIddCxMonitorUnassignSwapChain         = EvtMonitorUnassignSwapChain;
    cfg.EvtIddCxDeviceIoControl                  = EvtDeviceIoControl;

    NTSTATUS st = IddCxDeviceInitConfig(deviceInit, &cfg);
    if (!NT_SUCCESS(st)) return st;

    WDF_PNPPOWER_EVENT_CALLBACKS pnp;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
    pnp.EvtDeviceD0Entry = EvtDeviceD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(deviceInit, &pnp);

    WDF_OBJECT_ATTRIBUTES devAttr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&devAttr, DeviceContext);

    WDFDEVICE device = nullptr;
    st = WdfDeviceCreate(&deviceInit, &devAttr, &device);
    if (!NT_SUCCESS(st)) return st;

    // Interfaz para que el proveedor encuentre el dispositivo por su id de instancia
    // (IWRdsWddmIddProps1) y le mande la sesión.
    st = WdfDeviceCreateDeviceInterface(device, &REMOTEIDD_INTERFACE_GUID, nullptr);
    if (!NT_SUCCESS(st)) return st;

    // El adaptador se crea en D0Entry, como en el sample de Microsoft.
    return IddCxDeviceInitialize(device);
}

void EvtDeviceIoControl(WDFDEVICE, WDFREQUEST request, size_t, size_t inputLength, ULONG code)
{
    NTSTATUS st = STATUS_INVALID_DEVICE_REQUEST;
    if (code == IOCTL_REMOTEIDD_SET_SESSION)
    {
        REMOTEIDD_SET_SESSION* in = nullptr;
        size_t length = 0;
        st = STATUS_INVALID_PARAMETER;
        if (inputLength >= REMOTEIDD_SET_SESSION_V1_SIZE &&
            NT_SUCCESS(WdfRequestRetrieveInputBuffer(request, REMOTEIDD_SET_SESSION_V1_SIZE,
                                                     reinterpret_cast<PVOID*>(&in), &length)) &&
            (in->Version == 1 || in->Version == 2) && in->SessionId != 0)
        {
            g_seatSession.store(in->SessionId, std::memory_order_relaxed);
            Log(L"RemoteIdd: sesión del puesto %lu\n", in->SessionId);

            // v2: modo del cliente. Fuera de rango o 0 = se mantiene el valor actual.
            if (in->Version >= 2 && length >= sizeof(REMOTEIDD_SET_SESSION))
            {
                AcquireSRWLockExclusive(&g_modeLock);
                if (in->Width >= 640 && in->Width <= 7680 && in->Height >= 480 && in->Height <= 4320)
                {
                    g_seatMode.Width = in->Width;
                    g_seatMode.Height = in->Height;
                }
                if (in->RefreshHz >= 24 && in->RefreshHz <= 500)
                    g_seatMode.VSync = in->RefreshHz;
                ReleaseSRWLockExclusive(&g_modeLock);
            }
            st = STATUS_SUCCESS;
        }
    }
    WdfRequestComplete(request, st);
    if (NT_SUCCESS(st)) ApplySeatMode();
}

NTSTATUS EvtDeviceD0Entry(WDFDEVICE device, WDF_POWER_DEVICE_STATE)
{
    auto* dc = WdfObjectGet_DeviceContext(device);
    if (dc->Adapter) return STATUS_SUCCESS;     // ya creado en un D0Entry anterior

    // --- Crear el adaptador IddCx como DRIVER DE SESIÓN REMOTA ---
    WDF_OBJECT_ATTRIBUTES adAttr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&adAttr, AdapterContext);

    IDDCX_ENDPOINT_VERSION version = {};
    version.Size = sizeof(version);
    version.MajorVer = 0;
    version.MinorVer = 1;

    IDDCX_ADAPTER_CAPS caps = {};
    caps.Size = sizeof(caps);

    // ⭐ Diferencia clave del caso remoto (ambos flags obligatorios):
    caps.Flags = static_cast<IDDCX_ADAPTER_FLAGS>(
                 IDDCX_ADAPTER_FLAGS_REMOTE_SESSION_DRIVER   // es un driver de sesión remota
               | IDDCX_ADAPTER_FLAGS_USE_SMALLEST_MODE);     // obligatorio en IDD remotos

    caps.MaxMonitorsSupported = 1;
    caps.EndPointDiagnostics.Size = sizeof(caps.EndPointDiagnostics);
    caps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    caps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_NETWORK_OTHER;
    caps.EndPointDiagnostics.pEndPointFriendlyName = L"OpenStreamMS Remote Seat";
    caps.EndPointDiagnostics.pEndPointModelName = L"RemoteIdd";
    caps.EndPointDiagnostics.pEndPointManufacturerName = L"OpenStreamMS";
    caps.EndPointDiagnostics.pHardwareVersion = &version;
    caps.EndPointDiagnostics.pFirmwareVersion = &version;

    IDARG_IN_ADAPTER_INIT initIn = {};
    initIn.WdfDevice = device;
    initIn.pCaps = &caps;
    initIn.ObjectAttributes = &adAttr;

    IDARG_OUT_ADAPTER_INIT initOut = {};

    // ⚠️⚠️ AQUÍ ES DONDE EL SPIKE SE DETIENE EN LA PRÁCTICA ⚠️⚠️
    // Esta llamada FALLA si el dispositivo no lo creó la pila de escritorio remoto. Con
    // el RDP estándar de Windows nunca se cumple, porque el que atiende la sesión es el
    // RdpIdd de Microsoft, no este driver. Requiere un proveedor de protocolo RDS propio
    // (equivalente a DuoRds.dll) que haga que Windows cargue ESTE IddCx para la sesión.
    NTSTATUS st = IddCxAdapterInitAsync(&initIn, &initOut);
    if (!NT_SUCCESS(st))
    {
        Log(L"RemoteIdd: IddCxAdapterInitAsync 0x%08X (esperado sin proveedor RDS)\n", st);
        return st;
    }

    auto* ac = WdfObjectGet_AdapterContext(initOut.AdapterObject);
    ac->Adapter = initOut.AdapterObject;
    dc->Adapter = initOut.AdapterObject;
    return STATUS_SUCCESS;
}

} // namespace osms::ridd

// ============================================================ DriverEntry
extern "C" DRIVER_INITIALIZE DriverEntry;

extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT driverObject, PUNICODE_STRING registryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, osms::ridd::EvtDeviceAdd);

    WDF_OBJECT_ATTRIBUTES attr;
    WDF_OBJECT_ATTRIBUTES_INIT(&attr);

    return WdfDriverCreate(driverObject, registryPath, &attr, &config, WDF_NO_HANDLE);
}
