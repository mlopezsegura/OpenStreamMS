// RdsProvider.h — proveedor de protocolo RDS de los puestos (D1).
//
// No implementa un protocolo: envuelve al de Microsoft (rdpcorets.dll, el mismo que usa
// RDP-Tcp) y le delega todas las llamadas de las interfaces documentadas IWRdsProtocol*.
// Solo responde por su cuenta a IWRdsWddmIddProps / IWRdsWddmIddProps1:
//   - GetHardwareId: devuelve REMOTEIDD_HARDWARE_ID, así Terminal Services carga RemoteIdd
//     para la sesión en vez de RdpIdd.
//   - OnDriverLoad: pasa la sesión al driver por IOCTL (B2).
// Sin hooks ni parches: todo es COM documentado en wtsprotocol.h. Se registra solo en un
// RDP-Tcp, el único listener que levanta Windows cliente (ver install.ps1), y solo actúa en
// las conexiones de puesto (cliente REMOTEIDD_SEAT_CLIENT_ADDRESS); en el resto delega todo.
#pragma once

#include <windows.h>
#include <wtsprotocol.h>
#include <atomic>

#include "../RemoteIddIoctl.h"

namespace osms::rds
{
    // {8b0f3c51-2d7a-4f6e-a1c9-5e4d2b7f0a36}
    static const CLSID CLSID_OsmsRdsProtocolManager =
        { 0x8b0f3c51, 0x2d7a, 0x4f6e, { 0xa1, 0xc9, 0x5e, 0x4d, 0x2b, 0x7f, 0x0a, 0x36 } };

    // Protocolo de Microsoft al que se delega (valor de RDP-Tcp\LoadableProtocol_Object).
    // {5828227c-20cf-4408-b73f-73ab70b8849f} = rdpcorets.dll
    static const CLSID CLSID_RdpCoreTsProtocolManager =
        { 0x5828227c, 0x20cf, 0x4408, { 0xb7, 0x3f, 0x73, 0xab, 0x70, 0xb8, 0x84, 0x9f } };

    // Cómo llegan sesión y modo al driver. Por defecto, IOCTL_REMOTEIDD_SET_SESSION; los
    // tests lo sustituyen para no necesitar el driver.
    struct SessionSink
    {
        virtual HRESULT ToDriverHandle(const REMOTEIDD_SET_SESSION& seat, HANDLE driver) = 0;
        virtual HRESULT ToDeviceInstance(const REMOTEIDD_SET_SESSION& seat, LPCWSTR deviceInstance) = 0;
    };

    // Ajustes del listener, valores DWORD opcionales en WinStations\<listener>:
    //   OsmsRefreshHz: Hz de la pantalla RemoteIdd (RDP no transmite la frecuencia del
    //                  cliente); 0 = la del driver (60).
    //   OsmsRemoteIddAllConnections: 1 = todas las conexiones RDP reciben RemoteIdd, no solo
    //                  las del propio equipo (puestos).
    struct ListenerSettings
    {
        ULONG RefreshHz = 0;
        bool  AllConnections = false;
    };
    ListenerSettings ReadListenerSettings(LPCWSTR listenerName);
    SessionSink& DefaultSessionSink();

    // Crea el gestor que envuelve a `inner`. Usado por la fábrica COM y por los tests.
    // `fixed` sustituye a los ajustes del registro (tests); nullptr = leerlos del listener.
    HRESULT CreateProtocolManager(IWRdsProtocolManager* inner, SessionSink& sink,
                                  REFIID riid, void** ppv, const ListenerSettings* fixed = nullptr);
    // Crea el gestor de Microsoft (CLSID leído de RDP-Tcp, o rdpcorets por defecto).
    HRESULT CreateInnerProtocolManager(IWRdsProtocolManager** inner);

    // Base de recuento de referencias para los objetos COM del proveedor.
    class RefCounted
    {
    public:
        virtual ~RefCounted() = default;
        ULONG AddRefImpl() { return ++m_refs; }
        ULONG ReleaseImpl() { ULONG n = --m_refs; if (!n) delete this; return n; }
    private:
        std::atomic<ULONG> m_refs{ 1 };
    };
}
