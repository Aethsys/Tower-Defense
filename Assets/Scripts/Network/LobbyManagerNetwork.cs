using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class LobbyManagerNetwork : MonoBehaviour
{
    [Header("Lobby UI")]
    [SerializeField] private TMP_Text codeText;
    [SerializeField] private Button startButton;
    [SerializeField] private string mainMenuSceneName = "Main Menu";
    [SerializeField] private string gameSceneName = "Prototype";
    [SerializeField] private GameObject blackScreenPanel;
    [SerializeField] private float fadeToBlackDuration = 0.2f;

    [Header("Lobby Camera")]
    [SerializeField] private Camera lobbyCamera;

    [Header("Platform Spawn Points")]
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private NetworkObject playerPrefab;

    private static readonly Comparison<NetworkObject> CompareByOwnerClientId =
        (a, b) => a.OwnerClientId.CompareTo(b.OwnerClientId);

    private static readonly Comparison<Transform> CompareTransformByName =
        (a, b) => string.CompareOrdinal(a.name, b.name);

    private readonly List<NetworkObject> playerObjectsBuffer = new List<NetworkObject>(4);
    private readonly HashSet<ulong> configuredPlayerObjectIds = new HashSet<ulong>();

    private AudioListener lobbyAudioListener;
    private GameObject runtimeBlackScreenCanvas;
    private Image runtimeOverlayImage;
    private string lastDisplayedCode;
    private bool isLeaving;
    private bool isStarting;
    private bool isApplicationQuitting;

    private void OnApplicationQuit()
    {
        isApplicationQuitting = true;
    }

    private void Awake()
    {
        useGUILayout = false;

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

        if (lobbyCamera != null)
        {
            lobbyAudioListener = lobbyCamera.GetComponent<AudioListener>();
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
        List<Transform> found = new List<Transform>(4);

        for (int i = 0; i < allTransforms.Length; i++)
        {
            Transform t = allTransforms[i];
            if (t != null && t.name.StartsWith("spawn", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(t);
            }
        }

        found.Sort(CompareTransformByName);
        spawnPoints = found.ToArray();
    }

    private void UpdateSessionCodeUI()
    {
        if (codeText == null) return;

        string code = MenuManagerNetwork.CurrentSession?.Code;
        if (string.IsNullOrEmpty(code))
        {
            code = MenuManagerNetwork.SessionCode;
        }

        if (!string.IsNullOrEmpty(code) && code != lastDisplayedCode)
        {
            lastDisplayedCode = code;
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
        configuredPlayerObjectIds.Clear();

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

        IReadOnlyList<ulong> clientIds = NetworkManager.Singleton.ConnectedClientsIds;
        for (int i = 0; i < clientIds.Count; i++)
        {
            EnsurePlayerSpawned(clientIds[i]);
        }
    }

    private void EnsurePlayerSpawned(ulong clientId)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out NetworkClient networkClient))
        {
            return;
        }

        if (networkClient.PlayerObject != null)
        {
            return;
        }

        GameObject prefabToSpawn = playerPrefab != null
            ? playerPrefab.gameObject
            : NetworkManager.Singleton.NetworkConfig.PlayerPrefab;

        if (prefabToSpawn == null)
        {
            Debug.LogWarning("No PlayerPrefab assigned in LobbyManagerNetwork or NetworkManager.");
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

            if (lobbyAudioListener != null && !lobbyAudioListener.enabled)
            {
                lobbyAudioListener.enabled = true;
            }
        }

        if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null)
        {
            return;
        }

        playerObjectsBuffer.Clear();
        var spawnedList = NetworkManager.Singleton.SpawnManager.SpawnedObjectsList;
        foreach (NetworkObject obj in spawnedList)
        {
            if (obj != null && obj.IsPlayerObject)
            {
                playerObjectsBuffer.Add(obj);
            }
        }

        if (playerObjectsBuffer.Count > 1)
        {
            playerObjectsBuffer.Sort(CompareByOwnerClientId);
        }

        for (int i = 0; i < playerObjectsBuffer.Count; i++)
        {
            NetworkObject playerObj = playerObjectsBuffer[i];

            if (configuredPlayerObjectIds.Add(playerObj.NetworkObjectId))
            {
                DisablePlayerInternalCameraAndListener(playerObj);
            }

            Transform spawn = GetSpawnPoint(i);
            if (spawn != null)
            {
                Transform playerTransform = playerObj.transform;
                if ((playerTransform.position - spawn.position).sqrMagnitude > 0.001f ||
                    Quaternion.Angle(playerTransform.rotation, spawn.rotation) > 0.1f)
                {
                    if (playerObj.TryGetComponent(out CharacterController cc))
                    {
                        cc.enabled = false;
                        playerTransform.SetPositionAndRotation(spawn.position, spawn.rotation);
                        cc.enabled = true;
                    }
                    else
                    {
                        playerTransform.SetPositionAndRotation(spawn.position, spawn.rotation);
                    }
                }
            }
        }
    }

    private void DisablePlayerInternalCameraAndListener(NetworkObject playerObj)
    {
        Camera[] playerCameras = playerObj.GetComponentsInChildren<Camera>(true);
        for (int i = 0; i < playerCameras.Length; i++)
        {
            Camera cam = playerCameras[i];
            if (cam != lobbyCamera && cam.enabled)
            {
                cam.enabled = false;
            }
        }

        AudioListener[] playerListeners = playerObj.GetComponentsInChildren<AudioListener>(true);
        for (int i = 0; i < playerListeners.Length; i++)
        {
            AudioListener listener = playerListeners[i];
            if (lobbyCamera == null || listener.gameObject != lobbyCamera.gameObject)
            {
                if (listener.enabled)
                {
                    listener.enabled = false;
                }
            }
        }

        Canvas[] playerCanvases = playerObj.GetComponentsInChildren<Canvas>(true);
        for (int i = 0; i < playerCanvases.Length; i++)
        {
            if (playerCanvases[i].enabled)
            {
                playerCanvases[i].enabled = false;
            }
        }
    }

    private int GetPlayerIndex(ulong clientId)
    {
        if (NetworkManager.Singleton == null) return 0;

        int index = 0;
        IReadOnlyList<ulong> connectedIds = NetworkManager.Singleton.ConnectedClientsIds;
        for (int i = 0; i < connectedIds.Count; i++)
        {
            if (connectedIds[i] < clientId)
            {
                index++;
            }
        }

        return index;
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
                Debug.LogWarning($"Warning while leaving session: {e.Message}");
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
            if (runtimeBlackScreenCanvas == null)
            {
                runtimeBlackScreenCanvas = new GameObject("BlackScreenOverlayCanvas");
                Canvas overlayCanvas = runtimeBlackScreenCanvas.AddComponent<Canvas>();
                overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                overlayCanvas.sortingOrder = 999;
                runtimeBlackScreenCanvas.AddComponent<CanvasScaler>();
                runtimeBlackScreenCanvas.AddComponent<GraphicRaycaster>();

                GameObject imageObj = new GameObject("BlackScreenImage");
                imageObj.transform.SetParent(runtimeBlackScreenCanvas.transform, false);

                RectTransform rect = imageObj.AddComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;

                runtimeOverlayImage = imageObj.AddComponent<Image>();
                runtimeOverlayImage.raycastTarget = true;
            }
            else
            {
                runtimeBlackScreenCanvas.SetActive(true);
            }

            overlayImage = runtimeOverlayImage;
            if (overlayImage != null)
            {
                overlayImage.color = new Color(0f, 0f, 0f, fadeToBlackDuration > 0f ? 0f : 1f);
            }
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

        await Task.Yield();
    }

    private void HideBlackScreen()
    {
        if (blackScreenPanel != null)
        {
            blackScreenPanel.SetActive(false);
        }

        if (runtimeBlackScreenCanvas != null)
        {
            runtimeBlackScreenCanvas.SetActive(false);
        }
    }

    public async void StartGame()
    {
        if (isStarting || isLeaving || isApplicationQuitting || !Application.isPlaying)
        {
            return;
        }

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost)
        {
            Debug.LogWarning("Only the Host can start the match.");
            return;
        }

        isStarting = true;

        try
        {
            if (startButton != null)
            {
                startButton.interactable = false;
            }

            await ShowBlackScreenAsync();

            if (isApplicationQuitting || !Application.isPlaying || Application.exitCancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (MenuManagerNetwork.CurrentSession is IHostSession hostSession &&
                hostSession.State == SessionState.Connected)
            {
                hostSession.IsLocked = true;
                await hostSession.SavePropertiesAsync();
            }

            NetworkManager.Singleton.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
        }
        catch (Exception e)
        {
            isStarting = false;
            HideBlackScreen();

            if (startButton != null)
            {
                startButton.interactable = true;
            }

            Debug.LogError($"Failed to start the match: {e.Message}");
        }
    }
}
