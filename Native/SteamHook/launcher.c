// OpenStreamMS SteamLauncher — sustituye a "steam://..." en las sesiones de stream y puestos.
//
// Uso: osms-steam.exe [argumentos de steam.exe]   (p. ej. steam://open/bigpicture)
// Fuera de la sesión de la consola (o con una cuenta aislada osms-*), arranca steam.exe
// suspendido, le inyecta osms-steamhook.dll y lo reanuda: así no se cruza con el Steam de la
// consola aunque sea el mismo usuario de Windows. Solo en las cuentas aisladas registra además
// el protocolo steam:// en su HKCU, para que cualquier otro steam:// de la sesión (p. ej. el
// "undo" de Sunshine) pase por aquí; con un usuario compartido ese HKCU es también el de la
// consola y no se toca. En la consola lanza steam.exe tal cual.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>

typedef BOOL (WINAPI *OsmsInject_t)(HANDLE, LPCWSTR);

static BOOL IsIsolatedUser(void)
{
    WCHAR user[257];
    DWORD len = ARRAYSIZE(user);
    return GetUserNameW(user, &len) && _wcsnicmp(user, L"osms-", 5) == 0;
}

static BOOL IsConsoleSession(void)
{
    DWORD session;
    return ProcessIdToSessionId(GetCurrentProcessId(), &session) && session == WTSGetActiveConsoleSessionId();
}

static BOOL FindSteamExe(WCHAR* out, DWORD cch)
{
    DWORD size = cch * sizeof(WCHAR);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamExe", RRF_RT_REG_SZ, NULL, out, &size) == ERROR_SUCCESS
        && GetFileAttributesW(out) != INVALID_FILE_ATTRIBUTES)
    {
        for (WCHAR* p = out; *p; p++) if (*p == L'/') *p = L'\\';
        return TRUE;
    }
    size = cch * sizeof(WCHAR);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Valve\\Steam", L"InstallPath", RRF_RT_REG_SZ | RRF_SUBKEY_WOW6432KEY,
                     NULL, out, &size) != ERROR_SUCCESS)
        return FALSE;
    return wcscat_s(out, cch, L"\\steam.exe") == 0 && GetFileAttributesW(out) != INVALID_FILE_ATTRIBUTES;
}

static void SetString(HKEY root, LPCWSTR sub, LPCWSTR name, LPCWSTR value)
{
    RegSetKeyValueW(root, sub, name, REG_SZ, value, (DWORD)((wcslen(value) + 1) * sizeof(WCHAR)));
}

static void RegisterSteamProtocol(LPCWSTR self)
{
    WCHAR command[MAX_PATH + 32];
    swprintf_s(command, ARRAYSIZE(command), L"\"%s\" -- \"%%1\"", self);
    SetString(HKEY_CURRENT_USER, L"Software\\Classes\\steam", NULL, L"URL:steam protocol");
    SetString(HKEY_CURRENT_USER, L"Software\\Classes\\steam", L"URL Protocol", L"");
    SetString(HKEY_CURRENT_USER, L"Software\\Classes\\steam\\Shell\\Open\\Command", NULL, command);
}

static LPWSTR ArgsTail(void)
{
    LPWSTR p = GetCommandLineW();
    if (*p == L'"') { p++; while (*p && *p != L'"') p++; if (*p) p++; }
    else while (*p && *p != L' ' && *p != L'\t') p++;
    while (*p == L' ' || *p == L'\t') p++;
    return p;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE prev, LPWSTR cmdLine, int show)
{
    (void)instance; (void)prev; (void)cmdLine; (void)show;

    WCHAR steamExe[MAX_PATH];
    if (!FindSteamExe(steamExe, MAX_PATH)) return 2;

    static WCHAR commandLine[32768];
    swprintf_s(commandLine, ARRAYSIZE(commandLine), L"\"%s\" %s", steamExe, ArgsTail());

    WCHAR steamDir[MAX_PATH];
    wcscpy_s(steamDir, MAX_PATH, steamExe);
    WCHAR* slash = wcsrchr(steamDir, L'\\');
    if (slash) *slash = 0;

    BOOL isolatedUser = IsIsolatedUser();
    BOOL hooked = isolatedUser || !IsConsoleSession();
    STARTUPINFOW si = { sizeof si };
    PROCESS_INFORMATION pi;
    if (!CreateProcessW(steamExe, commandLine, NULL, NULL, FALSE, hooked ? CREATE_SUSPENDED : 0, NULL, steamDir, &si, &pi))
        return 3;

    int rc = 0;
    if (hooked)
    {
        WCHAR self[MAX_PATH], dll[MAX_PATH];
        GetModuleFileNameW(NULL, self, MAX_PATH);
        wcscpy_s(dll, MAX_PATH, self);
        slash = wcsrchr(dll, L'\\');
        wcscpy_s(slash ? slash + 1 : dll, MAX_PATH - (slash ? (slash + 1 - dll) : 0), L"osms-steamhook.dll");

        if (isolatedUser) RegisterSteamProtocol(self);

        HMODULE hook = LoadLibraryW(dll);   // en este proceso no hace nada: solo exporta el inyector
        OsmsInject_t inject = hook ? (OsmsInject_t)GetProcAddress(hook, "OsmsInject") : NULL;
        if (inject && inject(pi.hProcess, dll))
            ResumeThread(pi.hThread);
        else
        {   // sin hook hablaría con el Steam de otra sesión (y podría cerrarlo)
            TerminateProcess(pi.hProcess, 1);
            rc = 4;
        }
    }
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return rc;
}
