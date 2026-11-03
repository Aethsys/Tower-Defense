using UnityEngine;

/// <summary>
/// Mantiene vivo el GameObject del NetworkManager entre cargas de escena evitando duplicados.
/// </summary>
[DisallowMultipleComponent]
public class NetworkManagerPersistence : MonoBehaviour
{
    private static NetworkManagerPersistence instance;

    public static NetworkManagerPersistence Instance => instance;

    private void Awake()
    {
        useGUILayout = false;

        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (instance == this)
        {
            instance = null;
        }
    }
}
