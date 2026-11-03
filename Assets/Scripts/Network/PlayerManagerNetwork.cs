using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

#pragma warning disable CS0649
[DefaultExecutionOrder(-500)]
[DisallowMultipleComponent]
public class PlayerManagerNetwork : MonoBehaviour
{
    [Header("Map Spawn Points")]
    [Tooltip("Contenedor padre opcional con los puntos de spawn en el mapa.")]
    [SerializeField] private Transform spawnPointsRoot;
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private NetworkObject playerPrefab;

    [Header("Scene Transitions")]
    [Tooltip("Duración del fade in desde negro al iniciar la partida.")]
    [SerializeField] private float fadeInDuration = 0.4f;
    [Tooltip("Si es true, realiza un fade in suave desde negro al entrar a la escena de partida.")]
    [SerializeField] private bool fadeInOnStart = true;
    [Tooltip("Nombre de la escena del menú principal a la que volver si el host se desconecta.")]
    [SerializeField] private string mainMenuSceneName = "Main Menu";

    private readonly HashSet<ulong> positionedClientIds = new HashSet<ulong>();
    private readonly List<NetworkObject> playerObjectsBuffer = new List<NetworkObject>(4);
    private System.Func<bool> isRevealCancelled;
    private bool needsCoordination = true;
    private bool isLeaving;

    private void Awake()
    {
        useGUILayout = false;
        isRevealCancelled = () => this == null || isLeaving;

        SceneTransitionOverlay overlay = SceneTransitionOverlay.Instance;
        if (overlay != null)
        {
            overlay.SetBlack();
        }

        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            spawnPoints = NetworkPlayerUtility.FindSpawnPoints(spawnPointsRoot, NetworkPlayerUtility.CompareByParentThenName);
        }
        NetworkPlayerUtility.SetActiveSceneSpawnPoints(spawnPoints);
    }

    private async void Start()
    {
        NetworkPlayerUtility.OnAnyPlayerSpawned += OnAnyPlayerSpawned;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.OnClientConnectedCallback += OnClientConnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;

            if (nm.SceneManager != null)
            {
                nm.SceneManager.OnLoadEventCompleted += OnSceneLoadCompleted;
                nm.SceneManager.OnSynchronizeComplete += OnSynchronizeComplete;
            }

            NetworkPlayerUtility.EnsureAllConnectedPlayersSpawned(playerPrefab, spawnPoints);
        }

        CoordinatePlayersStartPositions(forceRepositionAll: true);

        // Esperar a que transforms, físicas y cámaras de la escena terminen de asentarse (2 frames reales)
        await Awaitable.NextFrameAsync();
        await Awaitable.NextFrameAsync();

        if (this == null || !Application.isPlaying || isLeaving || !fadeInOnStart) return;

        // Revelar suavemente la partida si venía de un fundido a negro
        SceneTransitionOverlay overlay = SceneTransitionOverlay.Instance;
        if (overlay != null)
        {
            await overlay.RevealSceneAsync(fadeInDuration, isRevealCancelled);
        }
    }

    private void Update()
    {
        if (needsCoordination)
        {
            CoordinatePlayersStartPositions(forceRepositionAll: false);
        }
    }

    private void OnDestroy()
    {
        NetworkPlayerUtility.OnAnyPlayerSpawned -= OnAnyPlayerSpawned;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null) return;

        nm.OnClientConnectedCallback -= OnClientConnected;
        nm.OnClientDisconnectCallback -= OnClientDisconnected;

        if (nm.SceneManager != null)
        {
            nm.SceneManager.OnLoadEventCompleted -= OnSceneLoadCompleted;
            nm.SceneManager.OnSynchronizeComplete -= OnSynchronizeComplete;
        }
    }

    private void OnSceneLoadCompleted(string sceneName, LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        NetworkPlayerUtility.EnsureAllConnectedPlayersSpawned(playerPrefab, spawnPoints);
        CoordinatePlayersStartPositions(forceRepositionAll: true);
    }

    private void OnSynchronizeComplete(ulong clientId)
    {
        CoordinatePlayersStartPositions(forceRepositionAll: true);
    }

    private void OnAnyPlayerSpawned()
    {
        needsCoordination = true;
        CoordinatePlayersStartPositions(forceRepositionAll: false);
    }

    private void OnClientConnected(ulong clientId)
    {
        NetworkPlayerUtility.EnsurePlayerSpawned(clientId, playerPrefab, spawnPoints);

        positionedClientIds.Remove(clientId);
        needsCoordination = true;
        CoordinatePlayersStartPositions(forceRepositionAll: false);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        positionedClientIds.Remove(clientId);

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && !nm.IsServer &&
            (clientId == nm.LocalClientId || clientId == NetworkManager.ServerClientId))
        {
            HandleDisconnectFromGame();
        }
    }

    private async void HandleDisconnectFromGame()
    {
        if (isLeaving || !Application.isPlaying) return;
        isLeaving = true;

        SceneTransitionOverlay overlay = SceneTransitionOverlay.Instance;
        if (overlay != null)
        {
            await overlay.FadeOutAsync(0.35f);
        }

        var session = MenuManagerNetwork.CurrentSession;
        if (session != null && session.State == Unity.Services.Multiplayer.SessionState.Connected)
        {
            try
            {
                await session.LeaveAsync();
            }
            catch
            {
                // Ignorar errores al cerrar sesión tras desconexión del host
            }
        }

        MenuSharedUtility.ShutdownNetworkIfActive();
        MenuManagerNetwork.ClearCurrentSession();

        if (!MenuSharedUtility.IsShuttingDown(false))
        {
            SceneManager.LoadScene(mainMenuSceneName, LoadSceneMode.Single);
        }
    }

    public void CoordinatePlayersStartPositions(bool forceRepositionAll)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
        {
            return;
        }

        if (forceRepositionAll)
        {
            positionedClientIds.Clear();
            needsCoordination = true;
        }

        NetworkPlayerUtility.CollectSpawnedPlayers(playerObjectsBuffer);
        int playerCount = playerObjectsBuffer.Count;
        if (playerCount == 0)
        {
            return;
        }

        for (int i = 0; i < playerCount; i++)
        {
            NetworkObject playerObj = playerObjectsBuffer[i];
            if (!positionedClientIds.Add(playerObj.OwnerClientId))
            {
                continue;
            }

            NetworkPlayerUtility.SetPlayerLocalComponentsActive(playerObj, playerObj.IsOwner);

            int spawnIndex = NetworkPlayerUtility.GetPlayerIndex(playerObj.OwnerClientId);
            Transform spawn = NetworkPlayerUtility.GetSpawnPoint(spawnPoints, spawnIndex);
            if (spawn != null)
            {
                NetworkPlayerUtility.PlaceAtSpawn(playerObj, spawn, reenableController: true);
            }
        }

        // Una vez colocados todos los jugadores esperados se deja de sondear en Update.
        // (Si no hay spawn points no hay nada que reintentar, así que también se detiene.)
        int expectedPlayers = NetworkPlayerUtility.GetExpectedPlayerCount(playerCount);
        if (expectedPlayers > 0 && positionedClientIds.Count >= expectedPlayers)
        {
            needsCoordination = false;
        }

        playerObjectsBuffer.Clear();
    }
}
