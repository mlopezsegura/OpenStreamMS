// Driver.h — RemoteIdd, driver IddCx de sesión remota (spike, no funcional solo).
//
// Se compila contra IddCx 1.4 (primera versión con IDD remotos) y UMDF 2.33, la base
// más baja posible: así no exige un Windows más nuevo del necesario.
#pragma once

#include <windows.h>
#include <bugcodes.h>
#include <wudfwdm.h>
#include <wdf.h>
#include <iddcx.h>

#include <dxgi1_5.h>
#include <d3d11_2.h>
#include <avrt.h>
#include <wrl.h>

#include "FramePublisher.h"
#include "RemoteIddIoctl.h"

namespace osms::ridd
{
    class SwapChainProcessor;

    // Contexto del dispositivo WDF. El adaptador se crea una sola vez (primer D0Entry).
    struct DeviceContext
    {
        IDDCX_ADAPTER  Adapter = nullptr;
    };
    WDF_DECLARE_CONTEXT_TYPE(DeviceContext);

    // Contexto del adaptador IddCx.
    struct AdapterContext
    {
        IDDCX_ADAPTER  Adapter = nullptr;
    };
    WDF_DECLARE_CONTEXT_TYPE(AdapterContext);

    // Contexto del monitor IddCx. WDF lo da a cero; Processor se libera en Unassign y,
    // si el monitor desaparece antes, en EvtMonitorCleanup.
    struct MonitorContext
    {
        IDDCX_MONITOR       Monitor = nullptr;
        AdapterContext*     Owner = nullptr;
        SwapChainProcessor* Processor = nullptr;
    };
    WDF_DECLARE_CONTEXT_TYPE(MonitorContext);

    // Hilo que consume el swapchain asignado a un monitor y publica cada frame por
    // el contrato de SharedFrame.h. Un hilo AVRT ("Distribution") por swapchain.
    class SwapChainProcessor
    {
    public:
        SwapChainProcessor(IDDCX_SWAPCHAIN swapChain,
                           Microsoft::WRL::ComPtr<ID3D11Device> device,
                           Microsoft::WRL::ComPtr<ID3D11DeviceContext> context,
                           HANDLE newFrameEvent);
        ~SwapChainProcessor();

        SwapChainProcessor(const SwapChainProcessor&) = delete;
        SwapChainProcessor& operator=(const SwapChainProcessor&) = delete;

    private:
        enum class Exit { Terminate, SwapChainLost };

        static DWORD CALLBACK ThreadProc(LPVOID ctx);
        void Run();
        Exit RunCore();

        IDDCX_SWAPCHAIN m_swapChain;
        Microsoft::WRL::ComPtr<ID3D11Device> m_device;
        HANDLE m_newFrameEvent = nullptr;   // lo señala IddCx cuando hay buffer nuevo
        HANDLE m_thread = nullptr;
        HANDLE m_terminate = nullptr;       // manual-reset, para parar el hilo

        // Copia cada frame a la textura compartida y lo publica (SharedFrame.h).
        FramePublisher m_publisher;
    };

    // === Callbacks del framework / IddCx (implementados en Driver.cpp) ===
    EVT_WDF_DRIVER_DEVICE_ADD           EvtDeviceAdd;
    EVT_WDF_DEVICE_D0_ENTRY             EvtDeviceD0Entry;
    EVT_IDD_CX_DEVICE_IO_CONTROL        EvtDeviceIoControl;
    EVT_WDF_OBJECT_CONTEXT_CLEANUP      EvtMonitorCleanup;

    EVT_IDD_CX_ADAPTER_INIT_FINISHED            EvtAdapterInitFinished;
    EVT_IDD_CX_ADAPTER_COMMIT_MODES             EvtAdapterCommitModes;
    EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION        EvtParseMonitorDescription;
    EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES EvtMonitorGetDefaultModes;
    EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES       EvtMonitorQueryTargetModes;
    EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN         EvtMonitorAssignSwapChain;
    EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN       EvtMonitorUnassignSwapChain;
}
