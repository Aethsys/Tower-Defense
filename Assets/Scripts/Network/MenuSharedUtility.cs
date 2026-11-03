using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Utilidades compartidas por el menú principal y el lobby.
/// </summary>
public static class MenuSharedUtility
{
    /// <summary>
    /// Deja un único AudioListener activo (prioriza el de la cámara indicada o el taggeado MainCamera).
    /// </summary>
    public static void CleanupDuplicateAudioListeners(Camera preferredCamera, bool destroyExtras)
    {
        AudioListener[] listeners = Object.FindObjectsByType<AudioListener>();
        if (listeners.Length <= 1) return;

        AudioListener preferred = listeners[0];
        for (int i = 0; i < listeners.Length; i++)
        {
            AudioListener l = listeners[i];
            if ((preferredCamera != null && l.gameObject == preferredCamera.gameObject) || l.CompareTag("MainCamera"))
            {
                preferred = l;
                break;
            }
        }

        for (int i = 0; i < listeners.Length; i++)
        {
            AudioListener l = listeners[i];
            if (l == preferred) continue;

            if (destroyExtras) Object.Destroy(l);
            else l.enabled = false;
        }
    }

    /// <summary>
    /// Envuelve una fase en radianes a [0, 2π) (soporta frecuencias negativas).
    /// </summary>
    public static float WrapPhase(float phase)
    {
        const float TwoPi = Mathf.PI * 2f;
        return (phase >= TwoPi || phase < 0f) ? Mathf.Repeat(phase, TwoPi) : phase;
    }

    /// <summary>
    /// Busca un GameObject por nombre y devuelve su componente T (o null).
    /// </summary>
    public static T FindByName<T>(string objectName) where T : Component
    {
        GameObject obj = GameObject.Find(objectName);
        return obj != null ? obj.GetComponent<T>() : null;
    }

    /// <summary>
    /// True si la aplicación está cerrándose o ya no está en Play.
    /// </summary>
    public static bool IsShuttingDown(bool quittingFlag)
    {
        return quittingFlag || !Application.isPlaying || Application.exitCancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// Código de la sesión actual (o cadena vacía si aún no existe).
    /// </summary>
    public static string GetCurrentSessionCode()
    {
        string code = MenuManagerNetwork.CurrentSession?.Code;
        return string.IsNullOrEmpty(code) ? MenuManagerNetwork.SessionCode ?? string.Empty : code;
    }

    /// <summary>
    /// Apaga NetworkManager si está activo.
    /// </summary>
    public static void ShutdownNetworkIfActive()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && (nm.IsListening || nm.IsConnectedClient || nm.IsServer))
        {
            nm.Shutdown();
        }
    }
}
