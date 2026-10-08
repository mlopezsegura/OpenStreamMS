// OpenStreamMS SteamHook — se inyecta en steam.exe (x64) dentro de las sesiones aisladas.
//
// steam.exe detecta "otra instancia" con objetos de ámbito máquina:
//   - el evento Global\Valve_SteamIPC_Class (visible desde todas las sesiones), y
//   - valores de HKLM\SOFTWARE\WOW6432Node\Valve\Steam (SteamPID, TempAppCmdLine...).
// Si existe, escribe su línea de órdenes en TempAppCmdLine y despierta al Steam ya abierto,
// aunque esté en otra sesión de Windows: así steam://close/bigpicture lanzado en la tele
// cerraba el Steam del escritorio. Este hook vuelve esos objetos locales a la sesión/usuario:
// el evento pasa a Local\ y esos valores se leen/escriben en HKCU del usuario aislado.
// Solo se parchea la IAT de steam.exe (es quien importa esas funciones).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>

#define OVERLAY_KEY L"Software\\OpenStreamMS\\SteamInstance"

static HMODULE g_self;

// ── Originales ────────────────────────────────────────────────────────────────
typedef HANDLE (WINAPI *CreateEventA_t)(LPSECURITY_ATTRIBUTES, BOOL, BOOL, LPCSTR);
typedef HANDLE (WINAPI *OpenEventA_t)(DWORD, BOOL, LPCSTR);
typedef LSTATUS (WINAPI *RegQueryValueExA_t)(HKEY, LPCSTR, LPDWORD, LPDWORD, LPBYTE, LPDWORD);
typedef LSTATUS (WINAPI *RegQueryValueExW_t)(HKEY, LPCWSTR, LPDWORD, LPDWORD, LPBYTE, LPDWORD);
typedef LSTATUS (WINAPI *RegSetValueExA_t)(HKEY, LPCSTR, DWORD, DWORD, const BYTE*, DWORD);
typedef LSTATUS (WINAPI *RegSetValueExW_t)(HKEY, LPCWSTR, DWORD, DWORD, const BYTE*, DWORD);
typedef LSTATUS (WINAPI *RegDeleteValueA_t)(HKEY, LPCSTR);
typedef BOOL (WINAPI *CreateProcessW_t)(LPCWSTR, LPWSTR, LPSECURITY_ATTRIBUTES, LPSECURITY_ATTRIBUTES,
                                        BOOL, DWORD, LPVOID, LPCWSTR, LPSTARTUPINFOW, LPPROCESS_INFORMATION);
typedef LONG (NTAPI *NtQueryKey_t)(HANDLE, int, PVOID, ULONG, PULONG);

static CreateEventA_t     o_CreateEventA;
static OpenEventA_t       o_OpenEventA;
static RegQueryValueExA_t o_RegQueryValueExA;
static RegQueryValueExW_t o_RegQueryValueExW;
static RegSetValueExA_t   o_RegSetValueExA;
static RegSetValueExW_t   o_RegSetValueExW;
static RegDeleteValueA_t  o_RegDeleteValueA;
static CreateProcessW_t   o_CreateProcessW;
static NtQueryKey_t       p_NtQueryKey;

// ── Evento global → local ─────────────────────────────────────────────────────
static LPCSTR LocalizeEventName(LPCSTR name)
{
    if (name && _stricmp(name, "Global\\Valve_SteamIPC_Class") == 0)
        return "Local\\Valve_SteamIPC_Class";
    return name;
}

static HANDLE WINAPI h_CreateEventA(LPSECURITY_ATTRIBUTES sa, BOOL manual, BOOL initial, LPCSTR name)
{
    return o_CreateEventA(sa, manual, initial, LocalizeEventName(name));
}

static HANDLE WINAPI h_OpenEventA(DWORD access, BOOL inherit, LPCSTR name)
{
    return o_OpenEventA(access, inherit, LocalizeEventName(name));
}

// ── Valores de HKLM\...\Valve\Steam → HKCU del usuario ────────────────────────
static const char* const kRedirected[] = { "SteamPID", "TempAppCmdLine", "RestartOnExitCmdLine", "ReLaunchCmdLine" };

static BOOL IsRedirectedNameA(LPCSTR name)
{
    if (!name) return FALSE;
    for (int i = 0; i < ARRAYSIZE(kRedirected); i++)
        if (_stricmp(name, kRedirected[i]) == 0) return TRUE;
    return FALSE;
}

static BOOL IsRedirectedNameW(LPCWSTR name)
{
    char buf[64];
    if (!name || WideCharToMultiByte(CP_ACP, 0, name, -1, buf, sizeof buf, NULL, NULL) == 0) return FALSE;
    return IsRedirectedNameA(buf);
}

static BOOL IsSteamMachineKey(HKEY key)
{
    if (!p_NtQueryKey || ((ULONG_PTR)key & 0x80000000)) return FALSE;   // claves predefinidas (HKLM...)
    struct { ULONG len; WCHAR name[260]; } info;
    ULONG got = 0;
    if (p_NtQueryKey(key, 3 /* KeyNameInformation */, &info, sizeof info - sizeof(WCHAR), &got) < 0) return FALSE;
    info.name[info.len / sizeof(WCHAR)] = 0;
    return _wcsicmp(info.name, L"\\REGISTRY\\MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam") == 0
        || _wcsicmp(info.name, L"\\REGISTRY\\MACHINE\\SOFTWARE\\Valve\\Steam") == 0;
}

static HKEY OverlayKey(void)
{
    static HKEY cached;
    if (!cached)
    {
        HKEY k;
        if (RegCreateKeyExW(HKEY_CURRENT_USER, OVERLAY_KEY, 0, NULL, 0, KEY_READ | KEY_WRITE, NULL, &k, NULL) == ERROR_SUCCESS)
            if (InterlockedCompareExchangePointer((PVOID*)&cached, k, NULL) != NULL) RegCloseKey(k);
    }
    return cached;
}

static HKEY Redirect(HKEY key, BOOL redirectedName)
{
    HKEY overlay;
    if (redirectedName && IsSteamMachineKey(key) && (overlay = OverlayKey()) != NULL) return overlay;
    return key;
}

static LSTATUS WINAPI h_RegQueryValueExA(HKEY k, LPCSTR n, LPDWORD r, LPDWORD t, LPBYTE d, LPDWORD cb)
{
    return o_RegQueryValueExA(Redirect(k, IsRedirectedNameA(n)), n, r, t, d, cb);
}

static LSTATUS WINAPI h_RegQueryValueExW(HKEY k, LPCWSTR n, LPDWORD r, LPDWORD t, LPBYTE d, LPDWORD cb)
{
    return o_RegQueryValueExW(Redirect(k, IsRedirectedNameW(n)), n, r, t, d, cb);
}

static LSTATUS WINAPI h_RegSetValueExA(HKEY k, LPCSTR n, DWORD r, DWORD t, const BYTE* d, DWORD cb)
{
    return o_RegSetValueExA(Redirect(k, IsRedirectedNameA(n)), n, r, t, d, cb);
}

static LSTATUS WINAPI h_RegSetValueExW(HKEY k, LPCWSTR n, DWORD r, DWORD t, const BYTE* d, DWORD cb)
{
    return o_RegSetValueExW(Redirect(k, IsRedirectedNameW(n)), n, r, t, d, cb);
}

static LSTATUS WINAPI h_RegDeleteValueA(HKEY k, LPCSTR n)
{
    return o_RegDeleteValueA(Redirect(k, IsRedirectedNameA(n)), n);
}

// ── Propagación a steam.exe hijos (reinicios tras actualizar, etc.) ───────────
#pragma comment(linker, "/EXPORT:OsmsInject")
BOOL WINAPI OsmsInject(HANDLE process, LPCWSTR dllPath)
{
    SIZE_T size = (wcslen(dllPath) + 1) * sizeof(WCHAR);
    LPVOID remote = VirtualAllocEx(process, NULL, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) return FALSE;

    BOOL ok = FALSE;
    if (WriteProcessMemory(process, remote, dllPath, size, NULL))
    {
        LPTHREAD_START_ROUTINE loadLibrary =
            (LPTHREAD_START_ROUTINE)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
        HANDLE thread = CreateRemoteThread(process, NULL, 0, loadLibrary, remote, 0, NULL);
        if (thread)
        {
            DWORD code = 0;
            ok = WaitForSingleObject(thread, 15000) == WAIT_OBJECT_0 && GetExitCodeThread(thread, &code) && code != 0;
            CloseHandle(thread);
        }
    }
    if (ok) VirtualFreeEx(process, remote, 0, MEM_RELEASE);   // si el hilo sigue vivo, no liberar
    return ok;
}

static BOOL TargetsSteamExe(LPCWSTR app, LPCWSTR cmd)
{
    WCHAR buf[MAX_PATH * 2];
    LPCWSTR src = app ? app : cmd;
    if (!src) return FALSE;
    wcsncpy_s(buf, ARRAYSIZE(buf), src, _TRUNCATE);
    if (!app)
    {   // primer token de la línea de órdenes
        WCHAR* p = buf;
        if (*p == L'"') { p++; WCHAR* e = wcschr(p, L'"'); if (e) *e = 0; }
        else { WCHAR* e = wcschr(p, L' '); if (e) *e = 0; }
        memmove(buf, p, (wcslen(p) + 1) * sizeof(WCHAR));
    }
    WCHAR* slash = wcsrchr(buf, L'\\');
    return _wcsicmp(slash ? slash + 1 : buf, L"steam.exe") == 0;
}

static BOOL WINAPI h_CreateProcessW(LPCWSTR app, LPWSTR cmd, LPSECURITY_ATTRIBUTES pa, LPSECURITY_ATTRIBUTES ta,
                                    BOOL inherit, DWORD flags, LPVOID env, LPCWSTR dir,
                                    LPSTARTUPINFOW si, LPPROCESS_INFORMATION pi)
{
    if (!TargetsSteamExe(app, cmd))
        return o_CreateProcessW(app, cmd, pa, ta, inherit, flags, env, dir, si, pi);

    if (!o_CreateProcessW(app, cmd, pa, ta, inherit, flags | CREATE_SUSPENDED, env, dir, si, pi))
        return FALSE;

    WCHAR self[MAX_PATH];
    GetModuleFileNameW(g_self, self, MAX_PATH);
    if (!OsmsInject(pi->hProcess, self))
    {   // sin hook ese steam.exe hablaría con el Steam de otra sesión: mejor no arrancarlo
        TerminateProcess(pi->hProcess, 1);
        CloseHandle(pi->hThread);
        CloseHandle(pi->hProcess);
        SetLastError(ERROR_ACCESS_DENIED);
        return FALSE;
    }
    if (!(flags & CREATE_SUSPENDED)) ResumeThread(pi->hThread);
    return TRUE;
}

// ── Parcheo de la IAT de steam.exe ────────────────────────────────────────────
typedef struct { const char* name; void* hook; void** original; } Hook;

static void PatchImports(HMODULE module, Hook* hooks, int count)
{
    BYTE* base = (BYTE*)module;
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return;

    for (IMAGE_IMPORT_DESCRIPTOR* imp = (IMAGE_IMPORT_DESCRIPTOR*)(base + dir.VirtualAddress); imp->Name; imp++)
    {
        if (!imp->OriginalFirstThunk) continue;
        IMAGE_THUNK_DATA* names = (IMAGE_THUNK_DATA*)(base + imp->OriginalFirstThunk);
        IMAGE_THUNK_DATA* iat   = (IMAGE_THUNK_DATA*)(base + imp->FirstThunk);
        for (; names->u1.AddressOfData; names++, iat++)
        {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            const char* fn = (const char*)((IMAGE_IMPORT_BY_NAME*)(base + names->u1.AddressOfData))->Name;
            for (int i = 0; i < count; i++)
            {
                if (strcmp(fn, hooks[i].name) != 0) continue;
                if (!*hooks[i].original) *hooks[i].original = (void*)iat->u1.Function;
                DWORD old;
                if (VirtualProtect(&iat->u1.Function, sizeof(void*), PAGE_READWRITE, &old))
                {
                    iat->u1.Function = (ULONG_PTR)hooks[i].hook;
                    VirtualProtect(&iat->u1.Function, sizeof(void*), old, &old);
                }
            }
        }
    }
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;

    g_self = instance;
    WCHAR exe[MAX_PATH];
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    WCHAR* slash = wcsrchr(exe, L'\\');
    if (_wcsicmp(slash ? slash + 1 : exe, L"steam.exe") != 0) return TRUE;   // solo actúa dentro de steam.exe

    DisableThreadLibraryCalls(instance);
    p_NtQueryKey = (NtQueryKey_t)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryKey");

    Hook hooks[] = {
        { "CreateEventA",     (void*)h_CreateEventA,     (void**)&o_CreateEventA },
        { "OpenEventA",       (void*)h_OpenEventA,       (void**)&o_OpenEventA },
        { "RegQueryValueExA", (void*)h_RegQueryValueExA, (void**)&o_RegQueryValueExA },
        { "RegQueryValueExW", (void*)h_RegQueryValueExW, (void**)&o_RegQueryValueExW },
        { "RegSetValueExA",   (void*)h_RegSetValueExA,   (void**)&o_RegSetValueExA },
        { "RegSetValueExW",   (void*)h_RegSetValueExW,   (void**)&o_RegSetValueExW },
        { "RegDeleteValueA",  (void*)h_RegDeleteValueA,  (void**)&o_RegDeleteValueA },
        { "CreateProcessW",   (void*)h_CreateProcessW,   (void**)&o_CreateProcessW },
    };
    PatchImports(GetModuleHandleW(NULL), hooks, ARRAYSIZE(hooks));
    return TRUE;
}
