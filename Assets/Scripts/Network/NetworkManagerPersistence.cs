using UnityEngine;

public class NetworkManagerPersistence : MonoBehaviour
{
    private static NetworkManagerPersistence instance;

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
}
