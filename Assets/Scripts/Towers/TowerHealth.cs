using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[RequireComponent(typeof(NetworkObject))]
public class TowerHealth : NetworkBehaviour
{
    public static readonly List<TowerHealth> ActiveTowers = new List<TowerHealth>(8);

    private static Camera cachedLocalCamera;
    private static Transform cachedLocalCameraTransform;
    private static int lastCameraLookupFrame = -1;

    [Header("Health Settings")]
    [SerializeField] private int maxHealth = 100;
    [SerializeField] private bool coreTower;
    [SerializeField] private string lobbySceneName = "Lobby";
    [SerializeField] private GameObject blackScreenPanel;
    [SerializeField] private float fadeToBlackDuration = 0.2f;

    [Header("Tower UI")]
    [SerializeField] private TMP_Text healthText;
    [SerializeField] private bool lookAtLocalCamera = true;
    [SerializeField] private bool lockVerticalRotation = true;

    private readonly NetworkVariable<int> currentHealth = new NetworkVariable<int>(
        100,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private Transform healthTextTransform;
    private GameObject runtimeBlackScreenCanvas;
    private Image runtimeOverlayImage;
    private int lastDisplayedHealth = int.MinValue;
    private bool isReturningToLobby;
    private bool isShowingBlackScreen;
    private bool isApplicationQuitting;

    public int MaxHealth => maxHealth;
    public int CurrentHealth => IsSpawned ? currentHealth.Value : maxHealth;
    public bool IsCoreTower => coreTower;
    public bool IsDestroyed => CurrentHealth <= 0;

    public event Action<int, int> OnHealthChanged;
    public event Action<TowerHealth> OnTowerDestroyed;


    public bool RestoreHealth()
    {
        if (IsSpawned && !IsServer)
            return false;

        if (coreTower || !IsDestroyed)
            return false;

        if (IsSpawned)
        {
            currentHealth.Value = maxHealth;
        }

        UpdateHealthUI(maxHealth);
        OnHealthChanged?.Invoke(maxHealth, maxHealth);

        return true;
    }


    private void OnApplicationQuit()
    {
        isApplicationQuitting = true;
    }

    private void Awake()
    {
        useGUILayout = false;

        if (healthText == null)
        {
            healthText = GetComponentInChildren<TMP_Text>(true);
        }

        if (healthText != null)
        {
            healthTextTransform = healthText.transform;
        }
    }

    private void OnEnable()
    {
        if (!ActiveTowers.Contains(this))
        {
            ActiveTowers.Add(this);
        }
    }

    private void OnDisable()
    {
        ActiveTowers.Remove(this);
    }

    private void Start()
    {
        if (!IsSpawned)
        {
            UpdateHealthUI(maxHealth);
        }
    }

    private void LateUpdate()
    {
        if (!lookAtLocalCamera || healthTextTransform == null)
        {
            return;
        }

        Transform camTransform = GetLocalActiveCameraTransform();
        if (camTransform == null)
        {
            return;
        }

        Vector3 directionFromCamera = healthTextTransform.position - camTransform.position;
        if (lockVerticalRotation)
        {
            directionFromCamera.y = 0f;
        }

        if (directionFromCamera.sqrMagnitude > 0.0001f)
        {
            healthTextTransform.rotation = Quaternion.LookRotation(directionFromCamera, Vector3.up);
        }
    }

    private static Transform GetLocalActiveCameraTransform()
    {
        if (cachedLocalCamera != null && cachedLocalCamera.isActiveAndEnabled)
        {
            return cachedLocalCameraTransform;
        }

        int currentFrame = Time.frameCount;
        if (lastCameraLookupFrame == currentFrame)
        {
            return null;
        }
        lastCameraLookupFrame = currentFrame;

        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.SpawnManager != null)
        {
            NetworkObject localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
            if (localPlayer != null)
            {
                Camera playerCam = localPlayer.GetComponentInChildren<Camera>(false);
                if (playerCam != null && playerCam.isActiveAndEnabled)
                {
                    cachedLocalCamera = playerCam;
                    cachedLocalCameraTransform = playerCam.transform;
                    return cachedLocalCameraTransform;
                }
            }
        }

        Camera mainCam = Camera.main;
        if (mainCam != null && mainCam.isActiveAndEnabled)
        {
            cachedLocalCamera = mainCam;
            cachedLocalCameraTransform = mainCam.transform;
            return cachedLocalCameraTransform;
        }

        return null;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        currentHealth.OnValueChanged += HandleHealthChanged;

        if (IsServer)
        {
            currentHealth.Value = maxHealth;
        }

        UpdateHealthUI(currentHealth.Value);
    }

    public override void OnNetworkDespawn()
    {
        currentHealth.OnValueChanged -= HandleHealthChanged;
        base.OnNetworkDespawn();
    }

    public void TakeDamage(int damage)
    {
        if (damage <= 0) return;

        if (!IsSpawned)
        {
            int fallbackHealth = Mathf.Max(0, maxHealth - damage);
            UpdateHealthUI(fallbackHealth);
            return;
        }

        if (IsServer)
        {
            ApplyDamageInternal(damage);
        }
        else
        {
            TakeDamageServerRpc(damage);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TakeDamageServerRpc(int damage)
    {
        ApplyDamageInternal(damage);
    }

    private void ApplyDamageInternal(int damage)
    {
        if (!IsServer || damage <= 0 || currentHealth.Value <= 0)
        {
            return;
        }

        int nextHealth = Mathf.Max(0, currentHealth.Value - damage);
        currentHealth.Value = nextHealth;

        if (nextHealth <= 0 && coreTower)
        {
            HandleCoreTowerDefeat();
        }
    }

    private void HandleHealthChanged(int previousValue, int newValue)
    {
        UpdateHealthUI(newValue);
        OnHealthChanged?.Invoke(newValue, maxHealth);

        if (previousValue > 0 && newValue <= 0)
        {
            OnTowerDestroyed?.Invoke(this);

            if (coreTower && !IsServer)
            {
                _ = ShowBlackScreenAsync();
            }
        }
    }

    private async void HandleCoreTowerDefeat()
    {
        if (isReturningToLobby || !IsServer || isApplicationQuitting || !Application.isPlaying)
        {
            return;
        }

        isReturningToLobby = true;

        await ShowBlackScreenAsync();

        if (isApplicationQuitting || !Application.isPlaying || Application.exitCancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (EnemyManagerNetwork.Instance != null)
        {
            EnemyManagerNetwork.Instance.StopWaves();
        }

        try
        {
            if (MenuManagerNetwork.CurrentSession is IHostSession hostSession &&
                hostSession.State == SessionState.Connected &&
                hostSession.IsLocked)
            {
                hostSession.IsLocked = false;
                await hostSession.SavePropertiesAsync();
            }
        }
        catch (Exception e)
        {
            if (!isApplicationQuitting && Application.isPlaying)
            {
                Debug.LogWarning($"Failed to unlock session when returning to lobby: {e.Message}");
            }
        }

        if (!isApplicationQuitting &&
            Application.isPlaying &&
            !Application.exitCancellationToken.IsCancellationRequested &&
            NetworkManager.Singleton != null &&
            NetworkManager.Singleton.IsServer &&
            NetworkManager.Singleton.SceneManager != null)
        {
            NetworkManager.Singleton.SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single);
        }
    }

    private async Task ShowBlackScreenAsync()
    {
        if (isShowingBlackScreen || isApplicationQuitting || !Application.isPlaying)
        {
            return;
        }

        isShowingBlackScreen = true;

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

    private void UpdateHealthUI(int healthValue)
    {
        if (healthText != null && lastDisplayedHealth != healthValue)
        {
            lastDisplayedHealth = healthValue;
            healthText.SetText("{0}", healthValue);
        }
    }
}
