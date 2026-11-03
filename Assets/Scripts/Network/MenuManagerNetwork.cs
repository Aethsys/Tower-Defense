using System;
using System.Collections;
using System.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using Unity.Services.Qos;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

#pragma warning disable CS0649
[DefaultExecutionOrder(-1000)]
[DisallowMultipleComponent]
public class MenuManagerNetwork : MonoBehaviour
{
    public static ISession CurrentSession { get; private set; }
    public static string SessionCode { get; private set; }

    [Header("Session Settings")]
    [SerializeField] private int maxPlayers = 4;
    [SerializeField] private string lobbySceneName = "Lobby";
    [Tooltip("Longitud exacta requerida para el código de sesión.")]
    [SerializeField] private int codeLength = 6;

    [Header("UI References")]
    [SerializeField] private TMP_InputField joinCodeInput;
    [Tooltip("Panel con los botones del menú (Host, Join, etc.) para ocultarlos durante la carga (opcional).")]
    [SerializeField] private GameObject menuButtonsPanel;
    [Tooltip("Texto para mostrar mensajes de estado o errores de conexión (opcional, se autogenera si está vacío).")]
    [SerializeField] private TMP_Text statusErrorText;

    [Header("UI Optimization References (Optional)")]
    [Tooltip("Gráfico de fondo opcional para desactivar raycastTarget automáticamente.")]
    [SerializeField] private Graphic backgroundGraphic;
    [Tooltip("Texto de título opcional para desactivar raycastTarget automáticamente.")]
    [SerializeField] private TMP_Text titleText;

    [Header("Status & Error Display")]
    [Tooltip("Tiempo en segundos que se muestra un mensaje de estado o error en pantalla.")]
    [SerializeField] private float statusErrorDuration = 4.5f;
    [Tooltip("Color del texto de error generado automáticamente.")]
    [SerializeField] private Color statusErrorColor = new Color(1f, 0.4f, 0.4f, 1f);
    [Tooltip("Tamaño de fuente del texto de error.")]
    [SerializeField] private float statusErrorFontSize = 24f;
    [Tooltip("Desplazamiento vertical en Y respecto al campo de código.")]
    [SerializeField] private float statusErrorOffsetY = -70f;

    [Header("Transición de Carga y Zoom")]
    [Tooltip("Objeto o texto de 'Loading' que se activará en pantalla al pulsar Host o Join.")]
    [SerializeField] private GameObject loadingText;

    [Tooltip("Transform de la nave espacial hacia la que la cámara hará zoom.")]
    [SerializeField] private Transform shipTransform;

    [Tooltip("Cámara que realizará el zoom (si se deja vacío, busca Camera.main automáticamente).")]
    [SerializeField] private Camera menuCamera;

    [Tooltip("Velocidad del zoom hacia la nave (mayor = más rápido).")]
    [SerializeField] private float zoomSpeed = 1.2f;

    [Tooltip("Porcentaje de distancia que la cámara se acerca a la nave (ej. 0.75 = se acerca el 75% del camino).")]
    [Range(0.2f, 0.95f)]
    [SerializeField] private float zoomCloseRatio = 0.75f;

    [Tooltip("Multiplicador del campo de visión (FOV) para zoom óptico real (ej. 0.4 reduce el FOV al 40% aumentando mucho el tamaño de la nave).")]
    [Range(0.2f, 0.9f)]
    [SerializeField] private float fovZoomMultiplier = 0.45f;

    [Header("Fade de Cambio de Escena")]
    [Tooltip("Panel opcional de pantalla negra para el fade. Si no se asigna, se crea automáticamente.")]
    [SerializeField] private GameObject blackScreenPanel;

    [Tooltip("Duración del fade out a negro antes de cambiar de escena en segundos.")]
    [SerializeField] private float fadeDuration = 0.5f;

    [Tooltip("Duración del fade in desde negro al entrar al menú principal.")]
    [SerializeField] private float fadeInDuration = 0.4f;

    [Tooltip("Duración del fade in tras cancelar o reiniciar una transición.")]
    [SerializeField] private float resetFadeDuration = 0.2f;

    [Tooltip("Si es true, hace un fade in suave desde negro al cargar la escena.")]
    [SerializeField] private bool fadeInOnStart = true;

    private bool isBusy;
    private bool isUpdatingCodeInput;
    private bool isApplicationQuitting;
    private MenuCameraController camController;
    private Coroutine statusErrorCoroutine;
    private WaitForSecondsRealtime statusErrorWait;
    private float cachedStatusErrorDuration = -1f;

    private static string cachedBestRegion;
    private static Task<string> warmupTask;

    private void Awake()
    {
        useGUILayout = false;

        // Limpiar cualquier estado de sesión previa al entrar al menú principal
        ClearCurrentSession();
        MenuSharedUtility.ShutdownNetworkIfActive();

        MenuSharedUtility.CleanupDuplicateAudioListeners(menuCamera, destroyExtras: true);

        if (loadingText != null)
        {
            loadingText.SetActive(false);
            if (loadingText.TryGetComponent(out TMP_Text loadTmp))
            {
                loadTmp.raycastTarget = false;
            }
        }

        if (menuButtonsPanel != null)
        {
            menuButtonsPanel.SetActive(true);
        }

        // Evitar raycast innecesario en elementos decorativos
        if (backgroundGraphic == null) backgroundGraphic = MenuSharedUtility.FindByName<Graphic>("Background");
        if (backgroundGraphic != null) backgroundGraphic.raycastTarget = false;

        if (titleText == null) titleText = MenuSharedUtility.FindByName<TMP_Text>("Title");
        if (titleText != null) titleText.raycastTarget = false;

        // Garantizar cursor libre y visible en el menú principal
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (joinCodeInput == null)
        {
            joinCodeInput = FindAnyObjectByType<TMP_InputField>();
        }

        if (menuButtonsPanel != null)
        {
            OptimizeMenuButtonTexts(menuButtonsPanel);
        }

        SetupJoinCodeInput();

        // Si se asignó un panel manual, ocultarlo para dar prioridad al overlay persistente
        if (blackScreenPanel != null)
        {
            blackScreenPanel.SetActive(false);
        }

        // Preparar overlay: negro si arranca con fade in, transparente si no
        SceneTransitionOverlay overlay = SceneTransitionOverlay.Instance;
        if (overlay != null)
        {
            if (fadeInOnStart) overlay.SetBlack();
            else overlay.SetClear();
        }
    }

    private void OptimizeMenuButtonTexts(GameObject panel)
    {
        TMP_Text[] texts = panel.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            // Solo desactivar si no es el placeholder o texto de un InputField interactuable
            if (joinCodeInput != null && texts[i].transform.IsChildOf(joinCodeInput.transform))
            {
                continue;
            }
            texts[i].raycastTarget = false;
        }
    }

    private void OnApplicationQuit()
    {
        isApplicationQuitting = true;
    }

    public static void ClearCurrentSession()
    {
        CurrentSession = null;
        SessionCode = null;
    }

    private async void Start()
    {
        ResolveCameraReferences();

        if (camController != null)
        {
            camController.ResetZoom();
        }

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnectedInMenu;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnectedInMenu;
        }

        if (fadeInOnStart)
        {
            await FadeAsync(black: false, fadeInDuration);
        }

        // Iniciar pre-calentamiento de QoS y autenticación en segundo plano sin bloquear la UI ni animaciones
        StartWarmupInBackground();
    }

    private void OnClientDisconnectedInMenu(ulong clientId)
    {
        if (isApplicationQuitting || !Application.isPlaying) return;

        if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
        {
            if (clientId == NetworkManager.Singleton.LocalClientId || clientId == NetworkManager.ServerClientId)
            {
                _ = HandleClientDisconnectInMenu();
            }
        }
    }

    private async Task HandleClientDisconnectInMenu()
    {
        await ResetLoadingTransitionAsync();
        if (joinCodeInput != null) joinCodeInput.interactable = true;
        isBusy = false;
        ShowMenuStatusError("Disconnected from host.");
        ClearCurrentSession();
    }

    private Task FadeAsync(bool black, float duration)
    {
        SceneTransitionOverlay overlay = SceneTransitionOverlay.Instance;
        if (overlay == null || MenuSharedUtility.IsShuttingDown(isApplicationQuitting)) return Task.CompletedTask;
        return black ? overlay.FadeOutAsync(duration) : overlay.FadeInAsync(duration);
    }

    private void ResolveCameraReferences()
    {
        if (menuCamera == null)
        {
            menuCamera = Camera.main;
            if (menuCamera == null)
            {
                menuCamera = FindAnyObjectByType<Camera>();
            }
        }

        if (menuCamera != null && camController == null)
        {
            camController = menuCamera.GetComponent<MenuCameraController>();
        }

        if (shipTransform == null)
        {
            MenuSpaceship ship = FindAnyObjectByType<MenuSpaceship>();
            if (ship != null)
            {
                shipTransform = ship.transform;
            }
        }
    }

    private static void StartWarmupInBackground()
    {
        // Ya hay región calculada o un pre-calentamiento en curso: nada que hacer.
        // Si el anterior terminó sin resultado (error de red, QoS vacío...), se reintenta.
        if (!string.IsNullOrEmpty(cachedBestRegion) || (warmupTask != null && !warmupTask.IsCompleted))
        {
            return;
        }

        warmupTask = WarmupNetworkServicesAsync();
    }

    private static async Task<string> WarmupNetworkServicesAsync()
    {
        try
        {
            await EnsureServicesInitializedAsync();

            // Consultar regiones Relay y medir latencia con QoS en segundo plano antes de que el usuario pulse Host
            if (QosService.Instance != null)
            {
                var qosResults = await QosService.Instance.GetSortedRelayQosResultsAsync(null);
                if (qosResults != null && qosResults.Count > 0)
                {
                    cachedBestRegion = qosResults[0].Region;
                    Debug.Log($"[NetworkWarmup] Pre-cálculo QoS listo en segundo plano. Mejor región: {cachedBestRegion} (Ping: {qosResults[0].AverageLatencyMs}ms)");
                    return cachedBestRegion;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[NetworkWarmup] Pre-calentamiento en segundo plano completado con aviso: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Devuelve la mejor región Relay conocida, esperando al pre-calentamiento si aún está en curso.
    /// </summary>
    private static async Task<string> GetBestRegionAsync()
    {
        if (!string.IsNullOrEmpty(cachedBestRegion) || warmupTask == null)
        {
            return cachedBestRegion;
        }

        try
        {
            return await warmupTask;
        }
        catch
        {
            return null;
        }
    }

    private static async Task EnsureServicesInitializedAsync()
    {
        if (UnityServices.State == ServicesInitializationState.Uninitialized)
        {
            await UnityServices.InitializeAsync();
        }

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }
    }

    public async void CreateSession()
    {
        if (isBusy || isApplicationQuitting || !Application.isPlaying) return;
        isBusy = true;
        if (joinCodeInput != null) joinCodeInput.interactable = false;

        try
        {
            StartVisualTransition();

            string region = await GetBestRegionAsync();
            await EnsureServicesInitializedAsync();
            if (this == null || MenuSharedUtility.IsShuttingDown(isApplicationQuitting)) return;

            var options = new SessionOptions
            {
                MaxPlayers = maxPlayers
            };

            // Pasar la región ya pre-calculada evita que UGS ejecute QosJob durante la animación
            if (!string.IsNullOrEmpty(region))
            {
                options.WithRelayNetwork(region);
            }
            else
            {
                options.WithRelayNetwork();
            }

            CurrentSession = await MultiplayerService.Instance.CreateSessionAsync(options);
            SessionCode = CurrentSession.Code;

            if (this == null || MenuSharedUtility.IsShuttingDown(isApplicationQuitting)) return;

            await FadeAsync(black: true, fadeDuration);

            if (this == null || MenuSharedUtility.IsShuttingDown(isApplicationQuitting)) return;

            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost)
            {
                NetworkManager.Singleton.SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single);
            }
            else
            {
                throw new InvalidOperationException("NetworkManager failed to start as Host.");
            }
        }
        catch (Exception e)
        {
            if (joinCodeInput != null) joinCodeInput.interactable = true;
            isBusy = false;

            if (!isApplicationQuitting && Application.isPlaying)
            {
                await ResetLoadingTransitionAsync();
                Debug.LogError($"Failed to create session: {e.Message}");
                ShowMenuStatusError("Failed to create session.");
            }
        }
    }

    private void SetupJoinCodeInput()
    {
        if (joinCodeInput == null) return;

        joinCodeInput.characterLimit = codeLength;
        joinCodeInput.onValueChanged.RemoveListener(OnJoinCodeInputChanged);
        joinCodeInput.onValueChanged.AddListener(OnJoinCodeInputChanged);
        joinCodeInput.onSubmit.RemoveListener(OnJoinCodeInputSubmit);
        joinCodeInput.onSubmit.AddListener(OnJoinCodeInputSubmit);
    }

    private void OnJoinCodeInputSubmit(string _)
    {
        JoinSessionByCode();
    }

    private void OnJoinCodeInputChanged(string val)
    {
        if (isUpdatingCodeInput || string.IsNullOrEmpty(val)) return;

        string sanitized = SanitizeLobbyCode(val, codeLength);
        if (sanitized != val)
        {
            isUpdatingCodeInput = true;
            int oldCaret = joinCodeInput.caretPosition;
            joinCodeInput.text = sanitized;
            joinCodeInput.caretPosition = Mathf.Clamp(oldCaret, 0, sanitized.Length);
            isUpdatingCodeInput = false;
        }
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnectedInMenu;
        }

        if (joinCodeInput != null)
        {
            joinCodeInput.onValueChanged.RemoveListener(OnJoinCodeInputChanged);
            joinCodeInput.onSubmit.RemoveListener(OnJoinCodeInputSubmit);
        }

        if (statusErrorCoroutine != null)
        {
            StopCoroutine(statusErrorCoroutine);
            statusErrorCoroutine = null;
        }
    }

    /// <summary>
    /// Sanea el código de lobby eliminando espacios en blanco, convirtiendo a mayúsculas y sustituyendo '5' por 'S' (0 allocations).
    /// </summary>
    public static string SanitizeLobbyCode(string input, int maxLen = 6)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        int limit = maxLen > 0 ? maxLen : 6;
        int bufferSize = Math.Min(input.Length, Math.Max(limit, 16));
        Span<char> buffer = stackalloc char[bufferSize];
        int count = 0;
        for (int i = 0; i < input.Length && count < limit; i++)
        {
            char c = input[i];
            if (char.IsWhiteSpace(c) || c == '\u200B' || c == '\uFEFF')
            {
                continue;
            }

            if (c == '5')
            {
                c = 'S';
            }
            else
            {
                c = char.ToUpperInvariant(c);
            }

            buffer[count++] = c;
        }

        return new string(buffer.Slice(0, count));
    }

    public async void JoinSessionByCode()
    {
        string code = joinCodeInput != null ? joinCodeInput.text : string.Empty;
        await JoinSessionByCodeAsync(code);
    }

    public async void JoinSessionByCode(string sessionCode)
    {
        await JoinSessionByCodeAsync(sessionCode);
    }

    private async Task JoinSessionByCodeAsync(string sessionCode)
    {
        if (isBusy || isApplicationQuitting || !Application.isPlaying) return;

        string cleanCode = SanitizeLobbyCode(sessionCode, codeLength);

        if (string.IsNullOrWhiteSpace(cleanCode))
        {
            ShowMenuStatusError("Please enter a session code.");
            return;
        }

        if (cleanCode.Length < codeLength)
        {
            ShowMenuStatusError($"Code must be {codeLength} characters.");
            return;
        }

        isBusy = true;
        if (joinCodeInput != null) joinCodeInput.interactable = false;

        try
        {
            // 1. Transición visual: ocultar botones, mostrar 'Loading...' e iniciar zoom hacia la nave
            StartVisualTransition();

            // 2. Pre-calentamiento: si la inicialización o cálculo QoS en segundo plano aún están en progreso, esperarlos
            await GetBestRegionAsync();
            await EnsureServicesInitializedAsync();
            if (this == null || MenuSharedUtility.IsShuttingDown(isApplicationQuitting)) return;

            SessionCode = cleanCode;

            // 3. Conexión a la sala multijugador mientras la animación de zoom y 'Loading...' continúan activos
            CurrentSession = await MultiplayerService.Instance.JoinSessionByCodeAsync(cleanCode);
            if (!string.IsNullOrEmpty(CurrentSession?.Code))
            {
                SessionCode = CurrentSession.Code;
            }

            if (this == null || MenuSharedUtility.IsShuttingDown(isApplicationQuitting)) return;

            // Fundido a negro suave para que la transición del cliente al Lobby sea idéntica a la del Host
            _ = FadeAsync(black: true, fadeDuration);

            // Netcode for GameObjects se encarga directamente de sincronizar y cargar la escena del Lobby
        }
        catch (Exception e)
        {
            SessionCode = null;
            if (joinCodeInput != null) joinCodeInput.interactable = true;
            isBusy = false;

            if (!isApplicationQuitting && Application.isPlaying)
            {
                await ResetLoadingTransitionAsync();
                Debug.LogError($"Failed to join session: {e.Message}");
                ShowMenuStatusError(GetFriendlyJoinError(e.Message));
            }
        }
    }

    /// <summary>
    /// Traduce el mensaje de error del servicio de Multiplayer a un texto comprensible para el jugador.
    /// </summary>
    private static string GetFriendlyJoinError(string message)
    {
        if (string.IsNullOrEmpty(message)) return "Could not connect to session.";

        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("404"))
        {
            return "Session not found. Check the code.";
        }

        if (message.Contains("invalid character", StringComparison.OrdinalIgnoreCase))
        {
            return "Code contains invalid characters.";
        }

        if (message.Contains("full", StringComparison.OrdinalIgnoreCase) || message.Contains("409"))
        {
            return "Session is full.";
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection timed out. Try again.";
        }

        return "Could not connect to session.";
    }

    public void ShowMenuStatusError(string message)
    {
        if (statusErrorText == null)
        {
            statusErrorText = FindOrCreateStatusErrorText();
        }

        if (statusErrorText != null)
        {
            if (statusErrorCoroutine != null)
            {
                StopCoroutine(statusErrorCoroutine);
            }
            statusErrorCoroutine = StartCoroutine(DisplayStatusErrorRoutine(message));
        }
    }

    private TMP_Text FindOrCreateStatusErrorText()
    {
        if (joinCodeInput == null) return null;

        Transform parentTransform = joinCodeInput.transform.parent != null
            ? joinCodeInput.transform.parent
            : joinCodeInput.transform;

        Transform existing = parentTransform.Find("MenuStatusErrorText");
        if (existing != null && existing.TryGetComponent(out TMP_Text existingTmp))
        {
            return existingTmp;
        }

        GameObject obj = new GameObject("MenuStatusErrorText");
        obj.transform.SetParent(parentTransform, false);

        if (!obj.TryGetComponent(out RectTransform rect))
        {
            rect = obj.AddComponent<RectTransform>();
        }

        RectTransform inputRect = joinCodeInput.GetComponent<RectTransform>();
        if (inputRect != null)
        {
            rect.anchorMin = inputRect.anchorMin;
            rect.anchorMax = inputRect.anchorMax;
            rect.pivot = inputRect.pivot;
            rect.anchoredPosition = new Vector2(inputRect.anchoredPosition.x, inputRect.anchoredPosition.y + statusErrorOffsetY);
            rect.sizeDelta = new Vector2(500f, 40f);
        }

        TMP_Text tmp = obj.AddComponent<TextMeshProUGUI>();
        if (joinCodeInput.textComponent != null && joinCodeInput.textComponent.font != null)
        {
            tmp.font = joinCodeInput.textComponent.font;
        }
        tmp.fontSize = statusErrorFontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = statusErrorColor;
        tmp.raycastTarget = false;
        obj.SetActive(false);

        return tmp;
    }

    private IEnumerator DisplayStatusErrorRoutine(string message)
    {
        if (statusErrorText == null) yield break;

        statusErrorText.text = message;
        statusErrorText.gameObject.SetActive(true);

        if (statusErrorWait == null || !Mathf.Approximately(cachedStatusErrorDuration, statusErrorDuration))
        {
            cachedStatusErrorDuration = statusErrorDuration;
            statusErrorWait = new WaitForSecondsRealtime(statusErrorDuration);
        }

        yield return statusErrorWait;

        if (statusErrorText != null)
        {
            statusErrorText.gameObject.SetActive(false);
        }
        statusErrorCoroutine = null;
    }

    private void StartVisualTransition()
    {
        if (loadingText != null)
        {
            loadingText.SetActive(true);
        }

        if (menuButtonsPanel != null)
        {
            menuButtonsPanel.SetActive(false);
        }

        ResolveCameraReferences();

        if (camController != null && shipTransform != null)
        {
            camController.StartZoom(shipTransform, zoomSpeed, zoomCloseRatio, fovZoomMultiplier);
        }
    }

    private async Task ResetLoadingTransitionAsync()
    {
        if (loadingText != null)
        {
            loadingText.SetActive(false);
        }

        if (menuButtonsPanel != null)
        {
            menuButtonsPanel.SetActive(true);
        }

        if (camController != null)
        {
            camController.ResetZoom();
        }

        await FadeAsync(black: false, resetFadeDuration);
    }

    public void ExitGame()
    {
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
}
