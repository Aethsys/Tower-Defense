using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class LobbyManagerNetwork : MonoBehaviour
{
    [Header("UI del Lobby")]
    [SerializeField] private TMP_Text codeText;
    [SerializeField] private Button startButton;
    [SerializeField] private string mainMenuSceneName = "Main Menu";
    [SerializeField] private string gameSceneName = "Prototype";
    [SerializeField] private GameObject blackScreenPanel;
    [SerializeField] private float fadeToBlackDuration = 0.2f;

    [Header("Cámara del Lobby")]
    [SerializeField] private Camera lobbyCamera;

    [Header("Spawns en Plataformas")]
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private NetworkObject playerPrefab;

    private bool isLeaving;
    private bool isStarting;
    private bool isApplicationQuitting;

    private void OnApplicationQuit()
    {
        isApplicationQuitting = true;
    }

    private void Awake()
    {
        if (lobbyCamera == null)
        {
            GameObject camObj = GameObject.Find("Main Camera");
            if (camObj != null)
            {
                lobbyCamera = camObj.GetComponent<Camera>();
            }

            if (lobbyCamera == null)
            {
                lobbyCamera = Camera.main;
            }
        }

        if (codeText == null)
        {
            GameObject codeObj = GameObject.Find("Code");
            if (codeObj != null)
            {
                codeText = codeObj.GetComponent<TMP_Text>();
            }
        }

        if (startButton == null)
        {
            GameObject startObj = GameObject.Find("Start");
            if (startObj != null)
            {
                startButton = startObj.GetComponent<Button>();
            }
        }

        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            AutoFindSpawnPoints();
        }
    }

    private void Start()
    {
        UpdateSessionCodeUI();
        UpdateStartButtonVisibility();

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;

            if (NetworkManager.Singleton.SceneManager != null)
            {
                NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += OnSceneLoadCompleted;
            }

            if (NetworkManager.Singleton.IsServer)
            {
                EnsureAllConnectedPlayersSpawned();
            }
        }

        RefreshLobbyPlayersAndCamera();
    }

    private void Update()
    {
        UpdateSessionCodeUI();
        UpdateStartButtonVisibility();
        RefreshLobbyPlayersAndCamera();
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;

            if (NetworkManager.Singleton.SceneManager != null)
            {
                NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= OnSceneLoadCompleted;
            }
        }
    }

    private void AutoFindSpawnPoints()
    {
        Transform[] allTransforms = FindObjectsByType<Transform>();
        spawnPoints = allTransforms
            .Where(t => t.name.StartsWith("spawn", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.name)
            .ToArray();
    }

    private void UpdateSessionCodeUI()
    {
        if (codeText == null) return;

        string code = MenuManagerNetwork.CurrentSession?.Code;
        if (string.IsNullOrEmpty(code))
        {
            code = MenuManagerNetwork.SessionCode;
        }

        if (!string.IsNullOrEmpty(code) && codeText.text != code)
        {
            codeText.text = code;
        }
    }

    private void UpdateStartButtonVisibility()
    {
        if (startButton == null || NetworkManager.Singleton == null) return;

        bool isHost = NetworkManager.Singleton.IsHost;
        if (startButton.gameObject.activeSelf != isHost)
        {
            startButton.gameObject.SetActive(isHost);
        }
    }

    private void OnSceneLoadCompleted(string sceneName, LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            EnsureAllConnectedPlayersSpawned();
        }

        RefreshLobbyPlayersAndCamera();
    }

    private void OnClientConnected(ulong clientId)
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            EnsurePlayerSpawned(clientId);
        }

        RefreshLobbyPlayersAndCamera();
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (isLeaving || isApplicationQuitting || !Application.isPlaying || Application.exitCancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Si se desconecta el Host (o el propio cliente pierde conexión durante la partida), volver al menú principal
        if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
        {
            if (clientId == NetworkManager.Singleton.LocalClientId || clientId == NetworkManager.ServerClientId)
            {
                LeaveLobby();
                return;
            }
        }

        RefreshLobbyPlayersAndCamera();
    }

    private void EnsureAllConnectedPlayersSpawned()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            EnsurePlayerSpawned(clientId);
        }
    }

    private void EnsurePlayerSpawned(ulong clientId)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out NetworkClient networkClient))
        {
            return;
        }

        // Si el NetworkManager ya instanció el PlayerPrefab automáticamente, solo lo posicionamos
        if (networkClient.PlayerObject != null)
        {
            return;
        }

        GameObject prefabToSpawn = playerPrefab != null
            ? playerPrefab.gameObject
            : NetworkManager.Singleton.NetworkConfig.PlayerPrefab;

        if (prefabToSpawn == null)
        {
            Debug.LogWarning("No se encontró un PlayerPrefab asignado en LobbyManagerNetwork ni en NetworkManager.");
            return;
        }

        int index = GetPlayerIndex(clientId);
        Transform spawn = GetSpawnPoint(index);
        Vector3 pos = spawn != null ? spawn.position : Vector3.zero;
        Quaternion rot = spawn != null ? spawn.rotation : Quaternion.identity;

        GameObject instance = Instantiate(prefabToSpawn, pos, rot);
        NetworkObject netObj = instance.GetComponent<NetworkObject>();
        if (netObj != null)
        {
            netObj.SpawnAsPlayerObject(clientId, true);
        }
    }

    private void RefreshLobbyPlayersAndCamera()
    {
        // Asegurar que la cámara del Lobby esté siempre activa
        if (lobbyCamera != null)
        {
            if (!lobbyCamera.gameObject.activeSelf)
            {
                lobbyCamera.gameObject.SetActive(true);
            }
            if (!lobbyCamera.enabled)
            {
                lobbyCamera.enabled = true;
            }

            AudioListener lobbyListener = lobbyCamera.GetComponent<AudioListener>();
            if (lobbyListener != null && !lobbyListener.enabled)
            {
                lobbyListener.enabled = true;
            }
        }

        if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null)
        {
            return;
        }

        // Obtener todos los objetos de jugador spawneados ordenados por OwnerClientId
        List<NetworkObject> playerObjects = NetworkManager.Singleton.SpawnManager.SpawnedObjectsList
            .Where(obj => obj != null && obj.IsPlayerObject)
            .OrderBy(obj => obj.OwnerClientId)
            .ToList();

        for (int i = 0; i < playerObjects.Count; i++)
        {
            NetworkObject playerObj = playerObjects[i];

            // Desactivar cámaras y AudioListeners internos de cada prefab de Player
            // para que todos los jugadores vean a través de la cámara única del Lobby
            Camera[] playerCameras = playerObj.GetComponentsInChildren<Camera>(true);
            foreach (Camera cam in playerCameras)
            {
                if (cam != lobbyCamera && cam.enabled)
                {
                    cam.enabled = false;
                }
            }

            AudioListener[] playerListeners = playerObj.GetComponentsInChildren<AudioListener>(true);
            foreach (AudioListener listener in playerListeners)
            {
                if (lobbyCamera == null || listener.gameObject != lobbyCamera.gameObject)
                {
                    if (listener.enabled)
                    {
                        listener.enabled = false;
                    }
                }
            }

            // Posicionar al jugador en su spawn correspondiente de las plataformas
            Transform spawn = GetSpawnPoint(i);
            if (spawn != null)
            {
                playerObj.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
            }
        }
    }

    private int GetPlayerIndex(ulong clientId)
    {
        if (NetworkManager.Singleton == null) return 0;

        List<ulong> sortedIds = NetworkManager.Singleton.ConnectedClientsIds
            .OrderBy(id => id)
            .ToList();

        int index = sortedIds.IndexOf(clientId);
        return index >= 0 ? index : 0;
    }

    private Transform GetSpawnPoint(int index)
    {
        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            return null;
        }

        int clampedIndex = Mathf.Clamp(index, 0, spawnPoints.Length - 1);
        return spawnPoints[clampedIndex];
    }

    public async void LeaveLobby()
    {
        if (isLeaving || isApplicationQuitting || !Application.isPlaying || Application.exitCancellationToken.IsCancellationRequested)
        {
            return;
        }

        isLeaving = true;

        try
        {
            // Poner la pantalla en negro antes de cerrar la sesión/despawnear a los jugadores
            await ShowBlackScreenAsync();

            ISession session = MenuManagerNetwork.CurrentSession;
            if (session != null && session.State == SessionState.Connected)
            {
                await session.LeaveAsync();
            }
        }
        catch (Exception e)
        {
            if (!isApplicationQuitting && Application.isPlaying)
            {
                Debug.LogWarning($"Aviso al salir de la sesión: {e.Message}");
            }
        }
        finally
        {
            MenuManagerNetwork.ClearCurrentSession();

            if (!isApplicationQuitting && Application.isPlaying && !Application.exitCancellationToken.IsCancellationRequested)
            {
                if (NetworkManager.Singleton != null &&
                    (NetworkManager.Singleton.IsListening || NetworkManager.Singleton.IsConnectedClient || NetworkManager.Singleton.IsServer))
                {
                    NetworkManager.Singleton.Shutdown();
                }

                SceneManager.LoadScene(mainMenuSceneName, LoadSceneMode.Single);
            }
        }
    }

    private async Task ShowBlackScreenAsync()
    {
        if (isApplicationQuitting || !Application.isPlaying)
        {
            return;
        }

        Image overlayImage = null;

        if (blackScreenPanel != null)
        {
            blackScreenPanel.SetActive(true);
            blackScreenPanel.transform.SetAsLastSibling();
            overlayImage = blackScreenPanel.GetComponent<Image>();
        }
        else
        {
            // Si no se asignó un panel en el Inspector, creamos un overlay negro de pantalla completa automáticamente
            GameObject canvasObj = new GameObject("BlackScreenOverlayCanvas");
            Canvas overlayCanvas = canvasObj.AddComponent<Canvas>();
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            overlayCanvas.sortingOrder = 999;
            canvasObj.AddComponent<CanvasScaler>();
            canvasObj.AddComponent<GraphicRaycaster>();

            GameObject imageObj = new GameObject("BlackScreenImage");
            imageObj.transform.SetParent(canvasObj.transform, false);

            RectTransform rect = imageObj.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            overlayImage = imageObj.AddComponent<Image>();
            overlayImage.color = new Color(0f, 0f, 0f, fadeToBlackDuration > 0f ? 0f : 1f);
            overlayImage.raycastTarget = true;
        }

        if (overlayImage != null && fadeToBlackDuration > 0f)
        {
            float elapsed = 0f;
            Color baseColor = overlayImage.color;
            while (elapsed < fadeToBlackDuration)
            {
                if (isApplicationQuitting || !Application.isPlaying || Application.exitCancellationToken.IsCancellationRequested)
                {
                    return;
                }

                elapsed += Time.unscaledDeltaTime;
                float alpha = Mathf.Clamp01(elapsed / fadeToBlackDuration);
                overlayImage.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
                await Task.Yield();
            }

            overlayImage.color = new Color(baseColor.r, baseColor.g, baseColor.b, 1f);
        }

        // Esperar un frame adicional con la pantalla 100% negra para asegurar que se haya renderizado antes del despawn
        await Task.Yield();
    }

    public async void StartGame()
    {
        if (isStarting || isLeaving || isApplicationQuitting || !Application.isPlaying)
        {
            return;
        }

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost)
        {
            Debug.LogWarning("Solo el Host puede iniciar la partida.");
            return;
        }

        isStarting = true;

        try
        {
            if (startButton != null)
            {
                startButton.interactable = false;
            }

            // Bloquear la sesión para que no entren más jugadores una vez iniciada la partida
            if (MenuManagerNetwork.CurrentSession is IHostSession hostSession &&
                hostSession.State == SessionState.Connected)
            {
                hostSession.IsLocked = true;
                await hostSession.SavePropertiesAsync();
            }

            // Cargar la escena del mapa para el Host y todos los Clientes conectados
            NetworkManager.Singleton.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
        }
        catch (Exception e)
        {
            isStarting = false;
            if (startButton != null)
            {
                startButton.interactable = true;
            }

            Debug.LogError($"Error al iniciar la partida: {e.Message}");
        }
    }
}
