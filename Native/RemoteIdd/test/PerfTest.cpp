// PerfTest.cpp — test de rendimiento de la entrega de frames RemoteIdd -> Sunshine.
//
// Dos procesos y la GPU real, con el mismo FramePublisher que usa el driver y el
// FrameConsumer que usará Sunshine. Solo cambia de dónde sale el frame: aquí el
// productor pinta texturas propias a ritmo fijo en vez de recibirlas del swapchain.
// Usa Local\ y seguridad por defecto: no necesita privilegios ni toca el sistema.
//
// Cada frame se pinta de un color que codifica su FrameId; el consumidor lee el píxel
// central y comprueba que el contenido corresponde al descriptor (nunca más viejo).
//
// Build:   ver build.cmd
// Run:     RemoteIddPerf.exe            (código de salida 0 = todos los escenarios OK)
//          RemoteIddPerf.exe consumer <sesión> <holdMs> <workMs> <duraciónMs>   (interno)
//          RemoteIddPerf.exe watch <sesión> [segundos]   (puesto real: lee el driver en Global\,
//                                                        como administrador)

#define NOMINMAX
#include "../FramePublisher.h"
#include "../FrameConsumer.h"

#include <dxgi1_2.h>
#include <avrt.h>
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <memory>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
using namespace osms::ridd;

namespace {

const wchar_t* kNs = L"Local\\";
bool g_watch = false;       // leyendo el driver real: el contenido no codifica el FrameId

// ------------------------------------------------------------------ util
double QpcMs(LONGLONG ticks)
{
    static LARGE_INTEGER f = [] { LARGE_INTEGER x; QueryPerformanceFrequency(&x); return x; }();
    return ticks * 1000.0 / static_cast<double>(f.QuadPart);
}

LONGLONG Qpc()
{
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

// Espera precisa sin cambiar la resolución global del temporizador (timeBeginPeriod).
void SleepUntil(LONGLONG deadline)
{
    static thread_local HANDLE timer = CreateWaitableTimerExW(
        nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    for (;;)
    {
        double left = QpcMs(deadline - Qpc());
        if (left <= 0) return;
        if (left > 0.5)
        {
            LARGE_INTEGER due;
            due.QuadPart = -static_cast<LONGLONG>((left - 0.3) * 10000.0);
            SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE);
            WaitForSingleObject(timer, INFINITE);
        }
        else
            YieldProcessor();
    }
}

void SleepMs(double ms) { SleepUntil(Qpc() + static_cast<LONGLONG>(ms / QpcMs(1))); }

struct Stats { double p50 = 0, p99 = 0, max = 0; };

Stats Percentiles(std::vector<double> v)
{
    Stats s;
    if (v.empty()) return s;
    std::sort(v.begin(), v.end());
    s.p50 = v[v.size() / 2];
    s.p99 = v[std::min(v.size() - 1, static_cast<size_t>(v.size() * 0.99))];
    s.max = v.back();
    return s;
}

bool CreateDevice(ComPtr<ID3D11Device>& dev, ComPtr<ID3D11DeviceContext>& ctx)
{
    // Adaptador 0, el mismo para los dos procesos (en el driver sería RenderAdapterLuid).
    ComPtr<IDXGIFactory1> factory;
    ComPtr<IDXGIAdapter1> adapter;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) || FAILED(factory->EnumAdapters1(0, &adapter)))
        return false;
    return SUCCEEDED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                                       D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
                                       D3D11_SDK_VERSION, &dev, nullptr, &ctx));
}

// FrameId <-> color (B8G8R8A8_UNORM: n/255 vuelve exacto como el byte n).
void ColorFor(UINT64 id, float rgba[4])
{
    rgba[0] = static_cast<float>(id & 0xFF) / 255.0f;
    rgba[1] = static_cast<float>((id >> 8) & 0xFF) / 255.0f;
    rgba[2] = static_cast<float>((id >> 16) & 0xFF) / 255.0f;
    rgba[3] = 1.0f;
}

UINT64 IdFromBgra(const BYTE* px) { return px[2] | (px[1] << 8) | (px[0] << 16); }

// ------------------------------------------------------------------ consumidor (proceso hijo)
// El handle de la textura es del WUDFHost (LocalService): un administrador solo puede
// duplicarlo con SeDebugPrivilege. Sunshine corre como SYSTEM y no lo necesita.
void EnableDebugPrivilege()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES, &token)) return;
    TOKEN_PRIVILEGES tp = { 1 };
    if (LookupPrivilegeValueW(nullptr, SE_DEBUG_NAME, &tp.Privileges[0].Luid))
    {
        tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
        AdjustTokenPrivileges(token, FALSE, &tp, sizeof(tp), nullptr, nullptr);
    }
    CloseHandle(token);
}

int RunConsumer(UINT32 session, double holdMs, double workMs, double durationMs)
{
    DWORD task = 0;
    HANDLE avrt = AvSetMmThreadCharacteristicsW(L"Capture", &task);

    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    if (!CreateDevice(dev, ctx)) { std::printf("RESULT error=device\n"); return 2; }

    FrameConsumer consumer(dev, session, kNs);
    LONGLONG openDeadline = Qpc() + static_cast<LONGLONG>(5000 / QpcMs(1));
    while (!consumer.Open())
    {
        if (Qpc() > openDeadline) { std::printf("RESULT error=open\n"); return 2; }
        Sleep(1);
    }

    D3D11_TEXTURE2D_DESC sd = {};
    sd.Width = sd.Height = 1;
    sd.MipLevels = sd.ArraySize = 1;
    sd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    sd.SampleDesc.Count = 1;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;
    if (FAILED(dev->CreateTexture2D(&sd, nullptr, &staging))) { std::printf("RESULT error=staging\n"); return 2; }

    std::vector<double> latSignal, latGpu;
    long long frames = 0, stale = 0, regress = 0, acquireFail = 0, gaps = 0;
    UINT64 lastContent = 0, lastId = 0;
    UINT32 lastW = 0, lastH = 0, lastFmt = 0;
    const int warmup = 20;
    int seen = 0;

    LONGLONG end = Qpc() + static_cast<LONGLONG>((durationMs + 300) / QpcMs(1));
    while (Qpc() < end)
    {
        auto w = consumer.WaitFrame(200);
        if (w == FrameConsumer::Wait::Timeout) continue;
        if (w == FrameConsumer::Wait::Error) break;

        ComPtr<ID3D11Texture2D> tex;
        RemoteIddFrameInfo info;
        if (!consumer.Acquire(100, &tex, &info)) { ++acquireFail; continue; }
        LONGLONG tAcq = Qpc();

        D3D11_BOX box = { info.Width / 2, info.Height / 2, 0, info.Width / 2 + 1, info.Height / 2 + 1, 1 };
        ctx->CopySubresourceRegion(staging.Get(), 0, 0, 0, 0, tex.Get(), 0, &box);
        if (holdMs > 0) SleepMs(holdMs);           // encoder copiando con la textura tomada
        consumer.Release();

        D3D11_MAPPED_SUBRESOURCE m;
        if (FAILED(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &m))) { ++acquireFail; continue; }
        LONGLONG tGpu = Qpc();
        UINT64 content = IdFromBgra(static_cast<const BYTE*>(m.pData));
        ctx->Unmap(staging.Get(), 0);

        if (++seen > warmup)
        {
            ++frames;
            latSignal.push_back(QpcMs(tAcq - static_cast<LONGLONG>(info.QpcPresent)));
            latGpu.push_back(QpcMs(tGpu - static_cast<LONGLONG>(info.QpcPresent)));
            // El contenido puede ser más nuevo que el descriptor (el productor publicó
            // entre la lectura del slot y AcquireSync), nunca más viejo.
            if (!g_watch && content < (info.FrameId & 0xFFFFFF)) ++stale;
            if (!g_watch && content < lastContent) ++regress;
            if (lastId && info.FrameId > lastId + 1) gaps += static_cast<long long>(info.FrameId - lastId - 1);
        }
        lastContent = content;
        lastId = info.FrameId;
        lastW = info.Width; lastH = info.Height; lastFmt = info.DxgiFormat;

        if (workMs > 0) SleepMs(workMs);           // codificación fuera del keyed mutex
    }

    Stats s = Percentiles(latSignal), g = Percentiles(latGpu);
    std::printf("INFO %ux%u fmt %u, %.1f fps recibidos\n", lastW, lastH, lastFmt,
                seen / (durationMs / 1000.0));
    std::printf("RESULT received=%d frames=%lld stale=%lld regress=%lld acqfail=%lld gaps=%lld "
                "sig50=%.3f sig99=%.3f gpu50=%.3f gpu99=%.3f gpumax=%.3f\n",
                seen, frames, stale, regress, acquireFail, gaps, s.p50, s.p99, g.p50, g.p99, g.max);
    std::fflush(stdout);
    if (avrt) AvRevertMmThreadCharacteristics(avrt);
    return 0;
}

// ------------------------------------------------------------------ productor (este proceso)
struct ProducerResult
{
    long long attempted = 0, published = 0, dropped = 0, failed = 0;
    double rate = 0;
    Stats publishCost;
};

ProducerResult RunProducer(UINT32 session, UINT w, UINT h, UINT fps, double durationMs)
{
    ProducerResult r;
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    if (!CreateDevice(dev, ctx)) return r;

    // Tres buffers, como un swapchain de IddCx.
    D3D11_TEXTURE2D_DESC d = {};
    d.Width = w; d.Height = h;
    d.MipLevels = d.ArraySize = 1;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> bufs[3];
    ComPtr<ID3D11RenderTargetView> rtvs[3];
    for (int i = 0; i < 3; ++i)
        if (FAILED(dev->CreateTexture2D(&d, nullptr, &bufs[i])) ||
            FAILED(dev->CreateRenderTargetView(bufs[i].Get(), nullptr, &rtvs[i])))
            return r;

    FramePublisher pub(dev, ctx, session, kNs, nullptr);
    pub.SetRefresh(fps, 1);

    DWORD task = 0;
    HANDLE avrt = AvSetMmThreadCharacteristicsW(L"Distribution", &task);

    std::vector<double> cost;
    cost.reserve(static_cast<size_t>(fps * durationMs / 1000 + 16));
    const LONGLONG interval = static_cast<LONGLONG>(1000.0 / fps / QpcMs(1));
    const LONGLONG start = Qpc();
    const LONGLONG end = start + static_cast<LONGLONG>(durationMs / QpcMs(1));
    LONGLONG next = start;
    for (int i = 0; Qpc() < end; ++i)
    {
        SleepUntil(next);
        next += interval;

        // "Composición" del frame: el color codifica el FrameId que va a recibir.
        float color[4];
        ColorFor(pub.LastFrameId() + 1, color);
        ctx->ClearRenderTargetView(rtvs[i % 3].Get(), color);

        LONGLONG t0 = Qpc();
        auto res = pub.Publish(bufs[i % 3].Get(), static_cast<UINT64>(t0));
        cost.push_back(QpcMs(Qpc() - t0));

        ++r.attempted;
        if (res == FramePublisher::Result::Published) ++r.published;
        else if (res == FramePublisher::Result::Dropped) ++r.dropped;
        else ++r.failed;
    }
    r.rate = r.attempted / (QpcMs(Qpc() - start) / 1000.0);
    r.publishCost = Percentiles(cost);
    // Deja vivos los objetos un poco para que el consumidor procese el último frame.
    Sleep(150);
    if (avrt) AvRevertMmThreadCharacteristics(avrt);
    return r;
}

// ------------------------------------------------------------------ orquestador
struct Scenario
{
    const char* name;
    UINT w, h, fps;
    double holdMs, workMs;
    double minDelivered;      // frames consumidos / publicados (si el consumidor es rápido)
    double maxGpuP99;         // ms desde el present hasta tener el píxel en CPU
};

double ParseKey(const std::string& line, const char* key)
{
    std::string k = std::string(key) + "=";
    size_t p = line.find(" " + k);
    if (p == std::string::npos) p = line.find(k);
    else ++p;
    return p == std::string::npos ? -1 : std::atof(line.c_str() + p + k.size());
}

bool SpawnConsumer(UINT32 session, const Scenario& sc, double durationMs, PROCESS_INFORMATION& pi, HANDLE& readPipe)
{
    SECURITY_ATTRIBUTES sa = { sizeof(sa), nullptr, TRUE };
    HANDLE writePipe = nullptr;
    if (!CreatePipe(&readPipe, &writePipe, &sa, 0)) return false;
    SetHandleInformation(readPipe, HANDLE_FLAG_INHERIT, 0);

    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    wchar_t cmd[512];
    swprintf_s(cmd, L"\"%ls\" consumer %u %.2f %.2f %.0f", exe, session, sc.holdMs, sc.workMs, durationMs);

    STARTUPINFOW si = { sizeof(si) };
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = writePipe;
    si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    BOOL ok = CreateProcessW(nullptr, cmd, nullptr, nullptr, TRUE, 0, nullptr, nullptr, &si, &pi);
    CloseHandle(writePipe);
    return ok != FALSE;
}

std::string ReadAll(HANDLE pipe)
{
    std::string out;
    char buf[512];
    DWORD n = 0;
    while (ReadFile(pipe, buf, sizeof(buf), &n, nullptr) && n) out.append(buf, n);
    return out;
}

int g_fail = 0;
void Check(bool ok, const char* what, const char* detail)
{
    std::printf("  [%s] %s (%s)\n", ok ? "PASS" : "FAIL", what, detail);
    if (!ok) g_fail = 1;
}

void RunScenario(UINT32 session, const Scenario& sc)
{
    const double durationMs = 4000;
    std::printf("\n== %s: %ux%u @ %u Hz, consumidor hold %.0f ms + trabajo %.0f ms\n",
                sc.name, sc.w, sc.h, sc.fps, sc.holdMs, sc.workMs);

    PROCESS_INFORMATION pi = {};
    HANDLE pipe = nullptr;
    if (!SpawnConsumer(session, sc, durationMs, pi, pipe)) { Check(false, "lanzar consumidor", "CreateProcess"); return; }

    ProducerResult p = RunProducer(session, sc.w, sc.h, sc.fps, durationMs);
    std::string out = ReadAll(pipe);
    WaitForSingleObject(pi.hProcess, 10000);
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess); CloseHandle(pipe);

    size_t pos = out.find("RESULT");
    std::string line = pos == std::string::npos ? std::string() : out.substr(pos);
    if (line.empty() || line.find("error=") != std::string::npos)
    {
        Check(false, "consumidor", line.empty() ? "sin resultado" : line.c_str());
        return;
    }

    double received = ParseKey(line, "received"), gaps = ParseKey(line, "gaps");
    double frames = ParseKey(line, "frames"), stale = ParseKey(line, "stale"),
           regress = ParseKey(line, "regress"), gpu50 = ParseKey(line, "gpu50"),
           gpu99 = ParseKey(line, "gpu99"), gpumax = ParseKey(line, "gpumax"),
           sig50 = ParseKey(line, "sig50"), sig99 = ParseKey(line, "sig99");

    std::printf("  productor: %.1f fps (objetivo %u), publicados %lld, perdidos %lld, fallos %lld\n",
                p.rate, sc.fps, p.published, p.dropped, p.failed);
    std::printf("  Publish(): p50 %.3f ms, p99 %.3f ms, max %.3f ms\n",
                p.publishCost.p50, p.publishCost.p99, p.publishCost.max);
    std::printf("  consumidor: %.0f frames medidos; latencia señal p50 %.3f / p99 %.3f ms; "
                "hasta píxel en CPU p50 %.3f / p99 %.3f / max %.3f ms\n",
                frames, sig50, sig99, gpu50, gpu99, gpumax);

    char d[160];
    sprintf_s(d, "%.1f de %u", p.rate, sc.fps);
    Check(p.rate >= sc.fps * 0.97, "el productor mantiene el ritmo (>=97%)", d);
    sprintf_s(d, "p99 %.3f ms", p.publishCost.p99);
    Check(p.publishCost.p99 <= 1.0, "Publish() nunca bloquea (p99 <= 1 ms)", d);
    Check(p.failed == 0, "sin fallos de publicación", "");
    sprintf_s(d, "%.0f viejos, %.0f retrocesos", stale, regress);
    Check(stale == 0 && regress == 0, "contenido correcto: nunca más viejo que el descriptor", d);
    if (sc.minDelivered > 0)
    {
        // Todo lo recibido (calentamiento incluido) frente a lo publicado.
        double delivered = p.published ? received / static_cast<double>(p.published) : 0;
        sprintf_s(d, "%.1f%%, %.0f saltos", delivered * 100, gaps);
        char what[96];
        sprintf_s(what, "entrega >= %.0f%% de lo publicado", sc.minDelivered * 100);
        Check(delivered >= sc.minDelivered, what, d);
    }
    else
    {
        sprintf_s(d, "%lld perdidos", p.dropped);
        Check(true, "con consumidor lento se pierden frames en vez de frenar", d);
    }
    sprintf_s(d, "p99 %.3f ms", gpu99);
    char what[96];
    sprintf_s(what, "latencia present -> píxel p99 <= %.1f ms", sc.maxGpuP99);
    Check(gpu99 >= 0 && gpu99 <= sc.maxGpuP99, what, d);
}

// Color del píxel (0,0) de una textura B8G8R8A8, como FrameId (IdFromBgra).
UINT64 ReadId(ID3D11Device* dev, ID3D11DeviceContext* ctx, ID3D11Texture2D* tex)
{
    D3D11_TEXTURE2D_DESC desc;
    tex->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.MiscFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;
    if (FAILED(dev->CreateTexture2D(&desc, nullptr, &staging))) return 0;
    ctx->CopyResource(staging.Get(), tex);
    D3D11_MAPPED_SUBRESOURCE map;
    if (FAILED(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &map))) return 0;
    UINT64 id = IdFromBgra(static_cast<const BYTE*>(map.pData));
    ctx->Unmap(staging.Get(), 0);
    return id;
}

// Lo que el driver crea por cada swapchain que le asigna IddCx: dispositivo y publicador
// nuevos. Publish() publica un frame de color `id`.
struct SwapChainSim
{
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    ComPtr<ID3D11Texture2D> src;
    ComPtr<ID3D11RenderTargetView> rtv;
    std::unique_ptr<FramePublisher> pub;

    bool Init(UINT32 session)
    {
        if (!CreateDevice(dev, ctx)) return false;
        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = 64; desc.Height = 64; desc.MipLevels = 1; desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        if (FAILED(dev->CreateTexture2D(&desc, nullptr, &src)) ||
            FAILED(dev->CreateRenderTargetView(src.Get(), nullptr, &rtv)))
            return false;
        pub = std::make_unique<FramePublisher>(dev, ctx, session, kNs, nullptr);
        return true;
    }

    bool Publish(UINT64 id)
    {
        float rgba[4];
        ColorFor(id, rgba);
        ctx->ClearRenderTargetView(rtv.Get(), rgba);
        bool ok = pub->Publish(src.Get(), static_cast<UINT64>(Qpc())) == FramePublisher::Result::Published;
        ctx->Flush();
        return ok;
    }
};

// Lo que pasa con el escritorio seguro del UAC: IddCx quita el swapchain y asigna otro, y el
// driver publica con un FramePublisher nuevo en el mismo proceso. El consumidor tiene que
// pasar a la textura nueva, no quedarse con la del publicador anterior.
void RunSwapChainChange(UINT32 session)
{
    std::printf("\n== cambio de swapchain (escritorio seguro del UAC)\n");
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    if (!CreateDevice(dev, ctx)) { Check(false, "dispositivo del consumidor", "D3D11CreateDevice"); return; }

    // Mantiene vivos el slot y los eventos entre publicadores, como KeepSharedObjects.
    FramePublisher keeper(nullptr, nullptr, session, kNs, nullptr);
    if (!keeper.Prepare()) { Check(false, "objetos compartidos", "Prepare"); return; }

    UINT64 got[2] = {};
    FrameConsumer consumer(dev, session, kNs);
    std::unique_ptr<SwapChainSim> swapChain;
    for (int i = 0; i < 2; ++i)
    {
        // El swapchain anterior muere antes de que IddCx asigne el siguiente.
        swapChain.reset();
        swapChain = std::make_unique<SwapChainSim>();
        if (!swapChain->Init(session) || !swapChain->Publish(0x10 + i) || !consumer.Open() ||
            consumer.WaitFrame(1000) != FrameConsumer::Wait::Frame)
            break;
        ComPtr<ID3D11Texture2D> tex;
        RemoteIddFrameInfo info;
        if (!consumer.Acquire(1000, &tex, &info)) break;
        got[i] = ReadId(dev.Get(), ctx.Get(), tex.Get());
        consumer.Release();
    }

    char d[96];
    sprintf_s(d, "leído 0x%llx y 0x%llx, esperado 0x10 y 0x11", got[0], got[1]);
    Check(got[0] == 0x10 && got[1] == 0x11, "tras un swapchain nuevo se lee la textura nueva", d);
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc >= 3 && wcscmp(argv[1], L"watch") == 0)
    {
        // Contenido real del escritorio: no se comprueba el color, solo ritmo y latencia.
        kNs = REMOTEIDD_NS;
        g_watch = true;
        EnableDebugPrivilege();
        double seconds = argc >= 4 ? _wtof(argv[3]) : 10;
        return RunConsumer(static_cast<UINT32>(_wtoi(argv[2])), 0, 0, seconds * 1000);
    }
    if (argc >= 6 && wcscmp(argv[1], L"consumer") == 0)
        return RunConsumer(static_cast<UINT32>(_wtoi(argv[2])), _wtof(argv[3]), _wtof(argv[4]), _wtof(argv[5]));

    std::printf("RemoteIdd PerfTest: FramePublisher -> FrameConsumer entre procesos, GPU real\n");

    // Umbral de latencia de los escenarios con consumidor lento: el frame más reciente
    // tiene como mucho un intervalo de antigüedad, más el margen de los rápidos.
    const Scenario scenarios[] = {
        { "1080p60",            1920, 1080,  60,  0,  0, 0.98, 4.0 },
        { "4K60",               3840, 2160,  60,  0,  0, 0.98, 4.0 },
        { "4K120",              3840, 2160, 120,  0,  0, 0.97, 4.0 },
        { "4K240",              3840, 2160, 240,  0,  0, 0.95, 4.0 },
        { "4K120 encoder lento",3840, 2160, 120,  1, 25, 0.0, 1000.0 / 120 + 4.0 },
        { "4K120 consumidor retiene 12 ms", 3840, 2160, 120, 12, 0, 0.0, 12 + 1000.0 / 120 + 4.0 },
    };

    UINT32 session = 7000 + (GetCurrentProcessId() % 1000) * 10;
    RunSwapChainChange(session++);
    for (const auto& sc : scenarios) RunScenario(session++, sc);

    std::printf("\nRESULTADO: %s\n", g_fail ? "FALLO" : "OK");
    return g_fail;
}
