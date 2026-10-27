// SharedFrameTest.cpp — test mínimo del contrato SharedFrame.h.
//
// Ejecutable HOY, sin WDK ni driver: valida en user mode la pieza del diseño que es
// independiente de IddCx y del protocolo remoto (memoria compartida + eventos entre
// productor y consumidor). Un solo proceso con varios hilos; los objetos con nombre
// funcionan igual dentro del proceso. Usa Local\ en vez de Global\ para no necesitar
// SeCreateGlobalPrivilege (un usuario normal no lo tiene).
//
// Build:   cl /EHsc /std:c++17 test\SharedFrameTest.cpp
// Run:     SharedFrameTest.exe        (código de salida 0 = OK, 1 = fallo)
//
// Comprueba:
//   1) Creación/apertura por nombre del slot y los eventos.
//   2) Round-trip Ready/Done.
//   3) FrameId monótono creciente en lo que ve el consumidor.
//   4) Limitador por llegada: con un consumidor LENTO, el productor NO se bloquea
//      (termina en ~N*ritmo_productor, no en ~N*trabajo_consumidor) y se pierden
//      frames intermedios en vez de frenar la "sesión".
//   5) Carrera (C2): un productor escribiendo sin pausa y un lector leyendo sin pausa.
//      Leyendo campo a campo aparecen descriptores rasgados; con RemoteIddSlotRead
//      (seqlock) no aparece ninguno.

#define REMOTEIDD_NS L"Local\\"
#include "../SharedFrame.h"
#include <atomic>
#include <thread>
#include <chrono>
#include <cstdio>

using namespace std::chrono;

namespace {
constexpr UINT32 kSession   = 4242;   // id de sesión de prueba
constexpr int    kFrames    = 100;    // frames que publica el productor
constexpr int    kProducerMs= 2;      // ritmo del productor (compone rápido)
constexpr int    kConsumerMs= 20;     // consumidor lento a propósito (10x)
constexpr int    kRaceMs    = 300;    // duración del caso de carrera

int    g_fail = 0;
void check(bool ok, const char* what) {
    std::printf("[%s] %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) g_fail = 1;
}

std::atomic<bool>   g_consumerReady{false};
std::atomic<int>    g_published{0};
std::atomic<UINT64> g_lastSeen{0};
std::atomic<int>    g_consumed{0};
std::atomic<bool>   g_monotonic{true};
std::atomic<bool>   g_objectsOk{true};

// sleep_for redondea a la resolución del temporizador (~15,6 ms) y falsearía el ritmo
// del productor. Temporizador de alta resolución: no cambia nada global del sistema.
void PreciseSleepMs(int ms) {
    static thread_local HANDLE t = CreateWaitableTimerExW(
        nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    LARGE_INTEGER due; due.QuadPart = -10000LL * ms;
    if (t && SetWaitableTimer(t, &due, 0, nullptr, nullptr, FALSE))
        WaitForSingleObject(t, INFINITE);
    else
        std::this_thread::sleep_for(milliseconds(ms));
}

void ProducerThread() {
    wchar_t name[128];
    swprintf_s(name, REMOTEIDD_NAME_SLOT, kSession);
    HANDLE map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                                    sizeof(RemoteIddSharedSlot), name);
    auto* slot = map ? reinterpret_cast<RemoteIddSharedSlot*>(
        MapViewOfFile(map, FILE_MAP_WRITE, 0, 0, sizeof(RemoteIddSharedSlot))) : nullptr;
    swprintf_s(name, REMOTEIDD_NAME_READY, kSession);
    HANDLE ready = CreateEventW(nullptr, FALSE, FALSE, name);
    swprintf_s(name, REMOTEIDD_NAME_DONE, kSession);
    HANDLE done  = CreateEventW(nullptr, FALSE, FALSE, name);
    if (!slot || !ready || !done) {
        std::printf("   productor: no se pudieron crear los objetos (err %lu)\n", GetLastError());
        g_objectsOk.store(false);
        g_consumerReady.store(true);   // libera la espera del otro lado
        return;
    }

    slot->Version = REMOTEIDD_SHARED_VERSION;
    slot->SessionId = kSession;
    RemoteIddFrameInfo f = {};
    f.Width = 3840; f.Height = 2160;
    f.RefreshNumer = 60000; f.RefreshDenom = 1000;
    f.DxgiFormat = 87; // DXGI_FORMAT_B8G8R8A8_UNORM
    f.ProducerPid = GetCurrentProcessId();

    // Espera a que el consumidor esté escuchando para no "perder" todo al inicio.
    while (!g_consumerReady.load()) std::this_thread::yield();

    for (int i = 1; i <= kFrames; ++i) {
        LARGE_INTEGER qpc; QueryPerformanceCounter(&qpc);
        f.FrameId = static_cast<UINT64>(i);
        f.QpcPresent = static_cast<UINT64>(qpc.QuadPart);
        RemoteIddSlotPublish(slot, f);
        SetEvent(ready);
        g_published.store(i);
        // Limitador por llegada: NO esperamos a `done`. Ritmo del productor y seguimos.
        PreciseSleepMs(kProducerMs);
    }
    // Da margen a que el consumidor procese el último disponible.
    std::this_thread::sleep_for(milliseconds(kConsumerMs * 2));

    UnmapViewOfFile(slot); CloseHandle(map);
    CloseHandle(ready); CloseHandle(done);
}

void ConsumerThread() {
    wchar_t name[128];
    swprintf_s(name, REMOTEIDD_NAME_SLOT, kSession);
    HANDLE map = nullptr;
    for (int tries = 0; tries < 1000 && !map && g_objectsOk.load(); ++tries) { // espera a que exista
        map = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
        if (!map) std::this_thread::sleep_for(milliseconds(1));
    }
    auto* slot = map ? reinterpret_cast<RemoteIddSharedSlot*>(
        MapViewOfFile(map, FILE_MAP_READ, 0, 0, sizeof(RemoteIddSharedSlot))) : nullptr;
    swprintf_s(name, REMOTEIDD_NAME_READY, kSession);
    HANDLE ready = CreateEventW(nullptr, FALSE, FALSE, name);
    swprintf_s(name, REMOTEIDD_NAME_DONE, kSession);
    HANDLE done  = CreateEventW(nullptr, FALSE, FALSE, name);
    if (!slot || !ready || !done) {
        std::printf("   consumidor: no se pudieron abrir los objetos (err %lu)\n", GetLastError());
        g_objectsOk.store(false);
        g_consumerReady.store(true);
        return;
    }

    g_consumerReady.store(true);

    for (;;) {
        DWORD w = WaitForSingleObject(ready, 500);  // 500 ms sin frames => fin
        if (w != WAIT_OBJECT_0) break;

        RemoteIddFrameInfo f;
        if (!RemoteIddSlotRead(slot, &f)) continue;
        if (f.FrameId < g_lastSeen.load()) g_monotonic.store(false);   // nunca debe retroceder
        g_lastSeen.store(f.FrameId);
        g_consumed.fetch_add(1);

        PreciseSleepMs(kConsumerMs);                            // consumidor lento
        SetEvent(done);
    }
    UnmapViewOfFile(slot); CloseHandle(map);
    CloseHandle(ready); CloseHandle(done);
}

// Un descriptor es coherente si todos los campos que el productor escribe con el mismo
// valor coinciden. El productor de la carrera escribe i en todos.
bool Coherent(const RemoteIddFrameInfo& f) {
    return f.Width == f.FrameId && f.Height == f.FrameId &&
           f.SharedTextureHandle == f.FrameId && f.QpcPresent == f.FrameId;
}

// Caso C2. Devuelve los descriptores rasgados vistos por cada tipo de lectura.
void RaceTest(long long* tornRaw, long long* tornSeq, long long* readsSeq) {
    RemoteIddSharedSlot slot = {};
    std::atomic<bool> stop{false};

    std::thread writer([&] {
        RemoteIddFrameInfo f = {};
        for (UINT64 i = 1; !stop.load(std::memory_order_relaxed); ++i) {
            f.Width = f.Height = static_cast<uint32_t>(i);
            f.SharedTextureHandle = f.FrameId = f.QpcPresent = i;
            RemoteIddSlotPublish(&slot, f);
        }
    });

    // Lectura campo a campo sin seqlock (lo que haría un consumidor ingenuo).
    long long raw = 0;
    auto end = steady_clock::now() + milliseconds(kRaceMs / 2);
    while (steady_clock::now() < end) {
        volatile RemoteIddFrameInfo* v = &slot.Frame;
        RemoteIddFrameInfo f = {};
        f.FrameId = v->FrameId;
        f.Width = v->Width;
        f.Height = v->Height;
        f.SharedTextureHandle = v->SharedTextureHandle;
        f.QpcPresent = v->QpcPresent;
        if (f.FrameId && !Coherent(f)) ++raw;
    }

    // Lectura con seqlock.
    long long seq = 0, reads = 0;
    end = steady_clock::now() + milliseconds(kRaceMs / 2);
    while (steady_clock::now() < end) {
        RemoteIddFrameInfo f;
        if (!RemoteIddSlotRead(&slot, &f)) continue;
        ++reads;
        if (f.FrameId && !Coherent(f)) ++seq;
    }

    stop.store(true);
    writer.join();
    *tornRaw = raw; *tornSeq = seq; *readsSeq = reads;
}
} // namespace

int main() {
    std::printf("SharedFrameTest: %d frames, productor %d ms, consumidor %d ms\n",
                kFrames, kProducerMs, kConsumerMs);

    auto t0 = steady_clock::now();
    std::thread c(ConsumerThread);
    std::thread p(ProducerThread);
    p.join();
    auto producerElapsed = duration_cast<milliseconds>(steady_clock::now() - t0).count();
    c.join();

    // 0) Los objetos con nombre se crean y se abren.
    check(g_objectsOk.load(), "slot y eventos se crean y se abren por nombre");

    // 1) El productor publicó todos los frames.
    check(g_published.load() == kFrames, "el productor publica todos los frames");

    // 2) El consumidor vio algo y de forma monótona.
    check(g_consumed.load() > 0, "el consumidor recibe frames");
    check(g_monotonic.load(), "FrameId nunca retrocede en el consumidor");
    check(g_lastSeen.load() <= (UINT64)kFrames, "FrameId dentro de rango");

    // 3) Limitador por llegada: el productor NO se bloqueó con el consumidor lento.
    //    Si esperara `done`, tardaría ~kFrames*kConsumerMs (=~2000 ms). Debe tardar
    //    mucho menos (~kFrames*kProducerMs + colchón). Umbral holgado para no ser flaky.
    long long gatedMs   = (long long)kFrames * kConsumerMs;     // si estuviera mal
    long long ungatedMs = (long long)kFrames * kProducerMs;     // objetivo
    std::printf("   productor tardo %lld ms (objetivo ~%lld, bloqueado ~%lld)\n",
                producerElapsed, ungatedMs, gatedMs);
    check(producerElapsed < gatedMs / 2,
          "el productor no se bloquea con consumidor lento (limitador por llegada)");

    // 4) Con consumidor 10x lento, debe PERDER frames (no procesarlos todos).
    check(g_consumed.load() < kFrames,
          "se pierden frames intermedios en vez de frenar la sesion");

    // 5) Carrera: el seqlock nunca entrega un descriptor rasgado. La lectura ingenua
    //    solo se informa (que rasgue depende del planificador; no se exige).
    long long tornRaw = 0, tornSeq = 0, readsSeq = 0;
    RaceTest(&tornRaw, &tornSeq, &readsSeq);
    std::printf("   carrera: %lld rasgados sin seqlock; %lld de %lld con seqlock\n",
                tornRaw, tornSeq, readsSeq);
    check(readsSeq > 0, "el lector con seqlock consigue leer con un escritor continuo");
    check(tornSeq == 0, "con seqlock no hay descriptores rasgados");

    std::printf("\nRESULTADO: %s\n", g_fail ? "FALLO" : "OK");
    return g_fail;
}
