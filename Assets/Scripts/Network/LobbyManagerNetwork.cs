using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

#pragma warning disable CS0649
[DefaultExecutionOrder(-500)]
[DisallowMultipleComponent]
public class LobbyManagerNetwork : MonoBehaviour
{
    [Header("Lobby UI")]
    [Tooltip("Texto donde se muestra el código de la sesión.")]
    [SerializeField] private TMP_Text codeText;
    [Tooltip("Texto para mostrar el aviso de código copiado (opcional, asignable en el Inspector).")]
    [SerializeField] private TMP_Text codeCopiedText;
    [Tooltip("Botón opcional para copiar el código al portapapeles.")]
    [SerializeField] private Button copyCodeButton;
    [SerializeField] private Button startButton;
    [SerializeField] private Button leaveButton;
    [SerializeField] private string mainMenuSceneName = "Main Menu";
    [SerializeField] private string gameSceneName = "SpaceShip";

    [Header("Transiciones y Fade")]
    [Tooltip("Panel opcional de pantalla negra para el fade. Si no se asigna, se crea automáticamente.")]
    [SerializeField] private GameObject blackScreenPanel;
    [Tooltip("Duración del fade out a negro al salir del lobby o iniciar partida.")]
    [SerializeField] private float fadeToBlackDuration = 0.35f;
    [Tooltip("Duración del fade in desde negro al entrar al lobby.")]
    [SerializeField] private float fadeInDuration = 0.4f;

    [Header("Lobby Camera")]
    [SerializeField] private Camera lobbyCamera;

    [Header("Platform Spawn Points")]
    [Tooltip("Contenedor padre opcional con los puntos de spawn de los jugadores.")]
    [SerializeField] private Transform spawnPointsRoot;
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private NetworkObject playerPrefab;

    [Header("Session Code Settings")]
    [Tooltip("Duración en segundos del aviso de código copiado.")]
    [SerializeField] private float copyFeedbackDuration = 1.4f;

    [Tooltip("Intervalo en segundos para consultar si el código de sesión ya está disponible si aún no se ha recibido.")]
    [SerializeField] private float sessionCodeCheckInterval = 0.5f;

    private readonly List<NetworkObject> playerObjectsBuffer = new List<NetworkObject>(4);
    private readonly HashSet<ulong> configuredPlayerObjectIds = new HashSet<ulong>();

    private AudioListener lobbyAudioListener;
    private string lastDisplayedCode;
    private bool isLeaving;
    private bool isStarting;
    private bool isApplicationQuitting;
    private Coroutine copyFeedbackCoroutine;
    private WaitForSecondsRealtime copyFeedbackWait;
    private float cachedCopyFeedbackDuration = -1f;
    private Func<bool> isRevealCancelled;

    // Control reactivo por eventos (elimina polling destructivo en Update)
    private bool needsPlayerRefresh = true;
    private float codeCheckTimer;

    private void OnApplicationQuit()
    {
        isApplicationQuitting = true;
    }

    private void Awake()
    {
        useGUILayout = false;
        isRevealCancelled = () => this == null || isLeaving;

        // Mantener el cursor desbloqueado y visible para interacción de UI en el lobby
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (blackScreenPanel != null)
        {
            blackScreenPanel.SetActive(false);
        }

        // Mantener la pantalla en negro persistente si la escena anterior venía con un fundido a negro activo
        if (SceneTransitionOverlay.Instance != null && (SceneTransitionOverlay.Instance.IsBlack || SceneTransitionOverlay.Instance.IsFading))
        {
            SceneTransitionOverlay.Instance.SetBlack();
        }

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

        MenuSharedUtility.CleanupDuplicateAudioListeners(lobbyCamera, destroyExtras: false);

        if (codeText == null)
        {
            GameObject codeObj = GameObject.Find("Code");
            if (codeObj != null)
            {
                codeText = codeObj.GetComponent<TMP_Text>();
            }
        }

        if (codeText != null)
        {
            SetupInteractiveCodeText();
        }

        if (codeCopiedText != null)
        {
            codeCopiedText.gameObject.SetActive(false);
            codeCopiedText.raycastTarget = false;
        }

        if (startButton == null)
        {
            GameObject startObj = GameObject.Find("Start");
            if (startObj != null)
            {
                startButton = startObj.GetComponent<Button>();
            }
        }
        OptimizeButtonTextRaycasts(startButton);

        if (leaveButton == null)
        {
            GameObject leaveObj = GameObject.Find("Leave");
            if (leaveObj != null)
            {
                leaveButton = leaveObj.GetComponent<Button>();
            }
        }
        OptimizeButtonTextRaycasts(leaveButton);

        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            spawnPoints = NetworkPlayerUtility.FindSpawnPoints(spawnPointsRoot, NetworkPlayerUtility.CompareByName);
        }
        NetworkPlayerUtility.SetActiveSceneSpawnPoints(spawnPoints);
    }

    private void OptimizeButtonTextRaycasts(Button btn)
    {
        if (btn == null) return;
        TMP_Text tmp = btn.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null) tmp.raycastTarget = false;
        Text txt = btn.GetComponentInChildren<Text>(true);
        if (txt != null) txt.raycastTarget = false;
    }

    private async void Start()
    {
        UpdateSessionCodeUI();
        UpdateStartButtonVisibility();

        NetworkPlayerUtility.OnAnyPlayerSpawned += OnAnyPlayerSpawned;

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;

            if (NetworkManager.Singleton.SceneManager != null)
            {
                NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += OnSceneLoadCompleted;
                NetworkManager.Singleton.SceneManager.OnSceneEvent += OnSceneEvent;
            }

            NetworkPlayerUtility.EnsureAllConnectedPlayersSpawned(playerPrefab, spawnPoints);
        }

        needsPlayerRefresh = true;
        RefreshLobbyPlayersAndCamera();

        // Esperar a que la escena se asiente y renderice antes de iniciar el fade in (2 frames reales)
        await Awaitable.NextFrameAsync();
        await Awaitable.NextFrameAsync();

        if (this == null || !Application.isPlaying || isLeaving) return;

        // Refrescar nuevamente tras el frame de asentamiento
        RefreshLobbyPlayersAndCamera();

        // Fade in suave desde negro únicamente si la pantalla venía con un fundido a negro activo
        SceneTransitionOverlay overlay = SceneTransitionOverlay.Instance;
        if (overlay != null)
        {
            await overlay.RevealSceneAsync(fadeInDuration, isRevealCancelled);
        }
    }

    private void OnSceneEvent(SceneEvent sceneEvent)
    {
        // Si el servidor inicia la carga de la escena de partida, los clientes funden a negro suavemente
        if (sceneEvent.SceneEventType == SceneEventType.Load && sceneEvent.SceneName == gameSceneName)
        {
            if (!isStarting && !isLeaving)
            {
                _ = FadeAsync(black: true, fadeToBlackDuration);
            }
        }
    }

    private void Update()
    {
        // Solo refrescar jugadores si hay cambios pendientes (reactivo por eventos, no por frame)
        if (needsPlayerRefresh)
        {
            RefreshLobbyPlayersAndCamera();
        }

        // Si el código de sesión aún no se obtuvo (esperando callback de Multiplayer Service), reintentar ocasionalmente
        if (string.IsNullOrEmpty(lastDisplayedCode))
        {
            codeCheckTimer += Time.unscaledDeltaTime;
            if (codeCheckTimer >= sessionCodeCheckInterval)
            {
                codeCheckTimer = 0f;
                UpdateSessionCodeUI();
            }
        }
    }

    private void OnDestroy()
    {
        NetworkPlayerUtility.OnAnyPlayerSpawned -= OnAnyPlayerSpawned;

        if (copyFeedbackCoroutine != null)
        {
            StopCoroutine(copyFeedbackCoroutine);
            copyFeedbackCoroutine = null;
            if (codeCopiedText != null) codeCopiedText.gameObject.SetActive(false);
            if (codeText != null) codeText.gameObject.SetActive(true);
        }

        if (copyCodeButton != null)
        {
            copyCodeButton.onClick.RemoveListener(CopySessionCodeToClipboard);
        }
        else if (codeText != null && codeText.TryGetComponent(out Button copyBtn))
        {
            copyBtn.onClick.RemoveListener(CopySessionCodeToClipboard);
        }

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;

            if (NetworkManager.Singleton.SceneManager != null)
            {
                NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= OnSceneLoadCompleted;
                NetworkManager.Singleton.SceneManager.OnSceneEvent -= OnSceneEvent;
            }
        }
    }

    private void SetupInteractiveCodeText()
    {
        if (copyCodeButton == null && codeText != null)
        {
            copyCodeButton = codeText.GetComponent<Button>();
        }

        if (copyCodeButton != null)
        {
            copyCodeButton.onClick.RemoveListener(CopySessionCodeToClipboard);
            copyCodeButton.onClick.AddListener(CopySessionCodeToClipboard);
        }
    }

    private void UpdateSessionCodeUI()
    {
        if (codeText == null) return;

        string code = MenuSharedUtility.GetCurrentSessionCode();

        if (!string.IsNullOrEmpty(code) && code != lastDisplayedCode)
        {
            lastDisplayedCode = code;
            RenderFormattedCode(code);
        }
    }

    private void RenderFormattedCode(string rawCode)
    {
        if (codeText == null) return;

        if (string.IsNullOrEmpty(rawCode))
        {
            codeText.text = "######";
            return;
        }

        // Espaciar cada carácter para eliminar cualquier ambigüedad en la fuente pixel KiwiSoda ('S' vs '5', 'B' vs '8')
        Span<char> spaced = stackalloc char[rawCode.Length * 3];
        int idx = 0;
        for (int i = 0; i < rawCode.Length; i++)
        {
            if (i > 0)
            {
                spaced[idx++] = ' ';
                spaced[idx++] = ' ';
            }
            spaced[idx++] = rawCode[i];
        }

        codeText.text = new string(spaced.Slice(0, idx));
    }

    public void CopySessionCodeToClipboard()
    {
        string rawCode = MenuSharedUtility.GetCurrentSessionCode();

        if (string.IsNullOrEmpty(rawCode)) return;

        string clean = MenuManagerNetwork.SanitizeLobbyCode(rawCode);
        GUIUtility.systemCopyBuffer = clean;
        Debug.Log($"[LobbyManagerNetwork] Código copiado al portapapeles: '{clean}'");

        if (copyFeedbackCoroutine != null)
        {
            StopCoroutine(copyFeedbackCoroutine);
        }
        copyFeedbackCoroutine = StartCoroutine(ShowCopiedFeedbackRoutine());
    }

    private IEnumerator ShowCopiedFeedbackRoutine()
    {
        if (codeText != null)
        {
            codeText.gameObject.SetActive(false);
        }

        if (codeCopiedText != null)
        {
            codeCopiedText.gameObject.SetActive(true);
        }

        if (copyFeedbackWait == null || !Mathf.Approximately(cachedCopyFeedbackDuration, copyFeedbackDuration))
        {
            cachedCopyFeedbackDuration = copyFeedbackDuration;
            copyFeedbackWait = new WaitForSecondsRealtime(copyFeedbackDuration);
        }

        yield return copyFeedbackWait;

        if (codeCopiedText != null)
        {
            codeCopiedText.gameObject.SetActive(false);
        }

        if (codeText != null)
        {
            codeText.gameObject.SetActive(true);
        }

        copyFeedbackCoroutine = null;
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
        NetworkPlayerUtility.EnsureAllConnectedPlayersSpawned(playerPrefab, spawnPoints);

        needsPlayerRefresh = true;
        UpdateStartButtonVisibility();
        UpdateSessionCodeUI();
        RefreshLobbyPlayersAndCamera();
    }

    private void OnClientConnected(ulong clientId)
    {
        NetworkPlayerUtility.EnsurePlayerSpawned(clientId, playerPrefab, spawnPoints);

        needsPlayerRefresh = true;
        UpdateStartButtonVisibility();
        RefreshLobbyPlayersAndCamera();
    }

    private void OnAnyPlayerSpawned()
    {
        needsPlayerRefresh = true;
        RefreshLobbyPlayersAndCamera();
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (isLeaving || MenuSharedUtility.IsShuttingDown(isApplicationQuitting))
        {
            return;
        }

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && !nm.IsServer &&
            (clientId == nm.LocalClientId || clientId == NetworkManager.ServerClientId))
        {
            LeaveLobby();
            return;
        }

        configuredPlayerObjectIds.Clear();
        needsPlayerRefresh = true;
        UpdateStartButtonVisibility();
        RefreshLobbyPlayersAndCamera();
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

        if (NetworkManager.Singleton == null)
        {
            return;
        }

        // En Netcode for GameObjects, SpawnManager.SpawnedObjectsList sincroniza en Host y Clientes por igual
        NetworkPlayerUtility.CollectSpawnedPlayers(playerObjectsBuffer);
        int playerCount = playerObjectsBuffer.Count;

        for (int i = 0; i < playerCount; i++)
        {
            NetworkObject playerObj = playerObjectsBuffer[i];

            if (configuredPlayerObjectIds.Add(playerObj.NetworkObjectId))
            {
                // En el lobby solo se ve la cámara del lobby: apagar cámara/audio/UI internos del jugador
                NetworkPlayerUtility.SetPlayerLocalComponentsActive(playerObj, false);
            }

            int spawnIndex = NetworkPlayerUtility.GetPlayerIndex(playerObj.OwnerClientId);
            Transform spawn = NetworkPlayerUtility.GetSpawnPoint(spawnPoints, spawnIndex);
            if (spawn == null) continue;

            Transform playerTransform = playerObj.transform;
            if ((playerTransform.position - spawn.position).sqrMagnitude > 0.001f ||
                Mathf.Abs(Quaternion.Dot(playerTransform.rotation, spawn.rotation)) < 0.99995f)
            {
                // CharacterController desactivado mientras está posado en el lobby para ahorrar física de PhysX
                NetworkPlayerUtility.PlaceAtSpawn(playerObj, spawn, reenableController: false);
            }
        }

        // Si ya están todos los jugadores esperados colocados y configurados, apagar el refresh.
        // (Sin spawn points no hay nada que reintentar, así que también se detiene.)
        int expectedPlayers = NetworkPlayerUtility.GetExpectedPlayerCount(playerCount);
        if (expectedPlayers > 0 && playerCount >= expectedPlayers)
        {
            needsPlayerRefresh = false;
        }

        playerObjectsBuffer.Clear();
    }

    public async void LeaveLobby()
    {
        if (isLeaving || MenuSharedUtility.IsShuttingDown(isApplicationQuitting))
        {
            return;
        }

        isLeaving = true;
        if (leaveButton != null) leaveButton.interactable = false;
        if (startButton != null) startButton.interactable = false;

        try
        {
            // Fade out suave a negro
            await FadeAsync(black: true, fadeToBlackDuration);

            ISession session = MenuManagerNetwork.CurrentSession;
            if (session != null && session.State == SessionState.Connected)
            {
                try
                {
                    await session.LeaveAsync();
                }
                catch (Exception sessionEx)
                {
                    if (!isApplicationQuitting && Application.isPlaying)
                    {
                        Debug.LogWarning($"[Lobby] Error leaving session: {sessionEx.Message}");
                    }
                }
            }

            MenuSharedUtility.ShutdownNetworkIfActive();
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

            if (!MenuSharedUtility.IsShuttingDown(isApplicationQuitting))
            {
                SceneManager.LoadScene(mainMenuSceneName, LoadSceneMode.Single);
            }
        }
    }

    private Task FadeAsync(bool black, float duration)
    {
        SceneTransitionOverlay overlay = SceneTransitionOverlay.Instance;
        if (overlay == null || MenuSharedUtility.IsShuttingDown(isApplicationQuitting)) return Task.CompletedTask;
        return black ? overlay.FadeOutAsync(duration) : overlay.FadeInAsync(duration);
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
            if (leaveButton != null)
            {
                leaveButton.interactable = false;
            }

            await FadeAsync(black: true, fadeToBlackDuration);

            if (MenuSharedUtility.IsShuttingDown(isApplicationQuitting))
            {
                return;
            }

            if (MenuManagerNetwork.CurrentSession is IHostSession hostSession &&
                hostSession.State == SessionState.Connected)
            {
                hostSession.IsLocked = true;
                await hostSession.SavePropertiesAsync();
            }

            string sceneToLoad = !string.IsNullOrEmpty(gameSceneName) ? gameSceneName : "SpaceShip";
            NetworkManager.Singleton.SceneManager.LoadScene(sceneToLoad, LoadSceneMode.Single);
        }
        catch (Exception e)
        {
            isStarting = false;
            if (SceneTransitionOverlay.Instance != null)
            {
                SceneTransitionOverlay.Instance.SetClear();
            }

            if (startButton != null)
            {
                startButton.interactable = true;
            }
            if (leaveButton != null)
            {
                leaveButton.interactable = true;
            }

            Debug.LogError($"Failed to start the match: {e.Message}");
        }
    }
}
