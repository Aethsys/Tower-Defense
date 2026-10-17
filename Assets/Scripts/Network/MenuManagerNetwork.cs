using System;
using System.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MenuManagerNetwork : MonoBehaviour
{
    public static ISession CurrentSession { get; private set; }
    public static string SessionCode { get; private set; }

    [Header("Configuración de Sesión")]
    [SerializeField] private int maxPlayers = 4;
    [SerializeField] private string lobbySceneName = "Lobby";

    [Header("UI (Opcional / Join por Código)")]
    [SerializeField] private TMP_InputField joinCodeInput;

    private bool isBusy;

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
        if (isBusy) return;
        isBusy = true;

        try
        {
            await EnsureServicesInitializedAsync();

            var options = new SessionOptions
            {
                MaxPlayers = maxPlayers
            }.WithRelayNetwork();

            // CreateSessionAsync con WithRelayNetwork() configura el transporte y
            // arranca automáticamente Netcode for GameObjects como Host.
            CurrentSession = await MultiplayerService.Instance.CreateSessionAsync(options);
            SessionCode = CurrentSession.Code;
            Debug.Log($"Sesión creada con éxito. Código: {SessionCode}");

            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost)
            {
                NetworkManager.Singleton.SceneManager.LoadScene(lobbySceneName, LoadSceneMode.Single);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error al crear la sesión: {e.Message}");
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
        if (isBusy) return;

        if (string.IsNullOrWhiteSpace(sessionCode))
        {
            Debug.LogWarning("Debes ingresar un código de sesión válido para unirte.");
            return;
        }

        isBusy = true;

        try
        {
            await EnsureServicesInitializedAsync();

            string cleanCode = sessionCode.Trim().ToUpperInvariant();
            SessionCode = cleanCode;

            // JoinSessionByCodeAsync se conecta al Relay de la sesión y
            // arranca automáticamente Netcode for GameObjects como Client.
            // NetworkSceneManager sincronizará automáticamente la escena Lobby con el Host.
            CurrentSession = await MultiplayerService.Instance.JoinSessionByCodeAsync(cleanCode);
            if (!string.IsNullOrEmpty(CurrentSession?.Code))
            {
                SessionCode = CurrentSession.Code;
            }
            Debug.Log($"Unido a la sesión con éxito. Código: {SessionCode}");
        }
        catch (Exception e)
        {
            SessionCode = null;
            Debug.LogError($"Error al unirse a la sesión: {e.Message}");
        }
        finally
        {
            isBusy = false;
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
