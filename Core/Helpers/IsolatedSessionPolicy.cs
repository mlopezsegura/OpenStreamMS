using Microsoft.Win32;
using OpenStreamMS.Services;
using System.Security.Principal;

namespace OpenStreamMS.Core.Helpers;

/// <summary>
/// Directivas del usuario aislado (osms-*) de una sesión. Si la usa un puesto exclusivo, no
/// puede apagar ni reiniciar el equipo: NoClose quita Apagar, Reiniciar, Suspender e Hibernar
/// del menú Inicio, de Ctrl+Alt+Supr y del diálogo de Alt+F4 (el derecho de apagar lo quita
/// <see cref="ShutdownRight"/>). Una sesión remota o un puesto no exclusivo los conserva. Es una
/// directiva de usuario, así que no afecta a la consola ni a otros usuarios. Se escribe en cada
/// arranque (el hive del usuario solo está cargado mientras tiene sesión).
/// </summary>
internal static class IsolatedSessionPolicy
{
    private const string ExplorerPolicies = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    public static void Apply(string username, Guid sessionId, bool canShutdown)
    {
        try
        {
            var sid = new NTAccount(username).Translate(typeof(SecurityIdentifier)).Value;
            using var key = Registry.Users.CreateSubKey($@"{sid}\{ExplorerPolicies}", writable: true)
                ?? throw new InvalidOperationException("el registro del usuario no está cargado");
            if (canShutdown) key.DeleteValue("NoClose", throwOnMissingValue: false);
            else key.SetValue("NoClose", 1, RegistryValueKind.DWord);
            Logger.Log($"[IsolatedUser] '{username}': apagar y reiniciar {(canShutdown ? "disponibles" : "quitados del menú (puesto exclusivo)")}.", sessionId);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[IsolatedUser] No se pudo quitar apagar/reiniciar a '{username}': {ex.Message}", sessionId);
        }
    }
}
