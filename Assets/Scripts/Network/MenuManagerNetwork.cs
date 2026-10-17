using System;
using System.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class MenuManagerNetwork : MonoBehaviour
{
    public static ISession CurrentSession { get; private set; }
    public static string SessionCode { get; private set; }

    [Header("Session Settings")]
    [SerializeField] private int maxPlayers = 4;
    [SerializeField] private string lobbySceneName = "Lobby";

    [Header("UI References")]
    [SerializeField] private TMP_InputField joinCodeInput;
    [SerializeField] private GameObject blackScreenPanel;
    [SerializeField] private float fadeToBlackDuration = 0.2f;

    private GameObject runtimeBlackScreenCanvas;
    private bool isBusy;
    private bool isApplicationQuitting;

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
        await EnsureServicesInitializedAsync();
    }

    private async Task EnsureServicesInitializedAsync()
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

        try
        {
            await ShowBlackScreenAsync();

            if (isApplicationQuitting || !Application.isPlaying || Application.exitCancellationToken.IsCancellationRequested)
            {
                return;
            }

            await EnsureServicesInitializedAsync();

            var options = new SessionOptions
            {
                MaxPlayers = maxPlayers
            }.WithRelayNetwork();

            // CreateSessionAsync with WithRelayNetwork() configures the transport and
            // automatically starts Netcode for GameObjects as Host.
            CurrentSession = await MultiplayerService.Instance.CreateSessionAsync(options);
            SessionCode = CurrentSession.Code;
            Debug.Log($"Session created successfully. Code: {SessionCode}");

            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost)
            {
                NetworkManager.Singleton.SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single);
            }
        }
        catch (Exception e)
        {
            HideBlackScreen();
            Debug.LogError($"Failed to create session: {e.Message}");
        }
        finally
        {
            isBusy = false;
        }
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

        if (string.IsNullOrWhiteSpace(sessionCode))
        {
            Debug.LogWarning("Please enter a valid session code to join.");
            return;
        }

        isBusy = true;

        try
        {
            await ShowBlackScreenAsync();

            if (isApplicationQuitting || !Application.isPlaying || Application.exitCancellationToken.IsCancellationRequested)
            {
                return;
            }

            await EnsureServicesInitializedAsync();

            string cleanCode = sessionCode.Trim().ToUpperInvariant();
            SessionCode = cleanCode;

            // JoinSessionByCodeAsync connects to the session Relay and
            // automatically starts Netcode for GameObjects as Client.
            // NetworkSceneManager automatically synchronizes the Lobby scene with the Host.
            CurrentSession = await MultiplayerService.Instance.JoinSessionByCodeAsync(cleanCode);
            if (!string.IsNullOrEmpty(CurrentSession?.Code))
            {
                SessionCode = CurrentSession.Code;
            }
            Debug.Log($"Joined session successfully. Code: {SessionCode}");
        }
        catch (Exception e)
        {
            SessionCode = null;
            HideBlackScreen();
            Debug.LogError($"Failed to join session: {e.Message}");
        }
        finally
        {
            isBusy = false;
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

                overlayImage = imageObj.AddComponent<Image>();
                overlayImage.raycastTarget = true;
            }
            else
            {
                runtimeBlackScreenCanvas.SetActive(true);
                overlayImage = runtimeBlackScreenCanvas.GetComponentInChildren<Image>();
            }

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

    public void ExitGame()
    {
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
}
