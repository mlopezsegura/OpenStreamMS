// RemoteIddIoctl.h — canal entre el proveedor de protocolo (Provider/) y el driver.
//
// Terminal Services carga el driver para una sesión y avisa al proveedor con
// IWRdsWddmIddProps::OnDriverLoad(SessionId, DriverHandle) o, en la versión 1 de la
// interfaz, con el id de instancia del dispositivo. El proveedor le pasa la sesión al
// driver con este IOCTL; así los objetos de SharedFrame.h llevan la sesión del puesto
// (B2) sin que el driver, que corre en la sesión 0, tenga que adivinarla.
#pragma once
#include <windows.h>
#include <winioctl.h>

// Hardware id que el proveedor devuelve en GetHardwareId y con el que casa RemoteIdd.inf.
// Mismo formato que el de Microsoft (RdpIdd_IndirectDisplay).
#define REMOTEIDD_HARDWARE_ID L"OsmsRemoteIdd_IndirectDisplay"

// Interfaz de dispositivo que registra el driver para recibir el IOCTL cuando el
// proveedor solo conoce el id de instancia (IWRdsWddmIddProps1).
// {3f1d5a7e-6c2b-4e8a-9b41-0d7e2c5a9f13}
static const GUID REMOTEIDD_INTERFACE_GUID =
    { 0x3f1d5a7e, 0x6c2b, 0x4e8a, { 0x9b, 0x41, 0x0d, 0x7e, 0x2c, 0x5a, 0x9f, 0x13 } };

#define IOCTL_REMOTEIDD_SET_SESSION \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x901, METHOD_BUFFERED, FILE_WRITE_ACCESS)

// v1: solo SessionId. v2 añade el modo que pide el cliente (0 = usar el del driver).
#define REMOTEIDD_SET_SESSION_VERSION 2

typedef struct _REMOTEIDD_SET_SESSION
{
    ULONG Version;      // 1 o 2
    ULONG SessionId;    // sesión de TS del puesto
    ULONG Width;        // v2: resolución del cliente RDP (WRDS_CLIENT_DATA::HRes)
    ULONG Height;       // v2: WRDS_CLIENT_DATA::VRes
    ULONG RefreshHz;    // v2: OsmsRefreshHz del listener (RDP no transmite los Hz)
} REMOTEIDD_SET_SESSION;

#define REMOTEIDD_SET_SESSION_V1_SIZE (2 * sizeof(ULONG))
