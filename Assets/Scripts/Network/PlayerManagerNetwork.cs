using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PlayerManagerNetwork : MonoBehaviour
{
    [Header("Puntos de Spawn en el Mapa")]
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private NetworkObject playerPrefab;

    private readonly HashSet<ulong> positionedClientIds = new HashSet<ulong>();

    private void Awake()
    {
        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            AutoFindSpawnPoints();
        }
    }

    private void Start()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;

            if (NetworkManager.Singleton.SceneManager != null)
            {
                NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += OnSceneLoadCompleted;
                NetworkManager.Singleton.SceneManager.OnSynchronizeComplete += OnSynchronizeComplete;
            }

            if (NetworkManager.Singleton.IsServer)
            {
                EnsureAllConnectedPlayersSpawned();
            }
        }

        CoordinatePlayersStartPositions(forceRepositionAll: true);
    }

    private void Update()
    {
        // Asegura que cualquier jugador que termine de sincronizar o migrar desde el Lobby
        // reciba su posición inicial y su cámara correspondiente.
        CoordinatePlayersStartPositions(forceRepositionAll: false);
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
                NetworkManager.Singleton.SceneManager.OnSynchronizeComplete -= OnSynchronizeComplete;
            }
        }
    }

    private void AutoFindSpawnPoints()
    {
        Transform[] allTransforms = FindObjectsByType<Transform>();
        spawnPoints = allTransforms
            .Where(t => t.name.StartsWith("spawn", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.parent != null ? t.parent.name : string.Empty)
            .ThenBy(t => t.name)
            .ToArray();
    }

    private void OnSceneLoadCompleted(string sceneName, LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            EnsureAllConnectedPlayersSpawned();
        }

        CoordinatePlayersStartPositions(forceRepositionAll: true);
    }

    private void OnSynchronizeComplete(ulong clientId)
    {
        CoordinatePlayersStartPositions(forceRepositionAll: true);
    }

    private void OnClientConnected(ulong clientId)
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            EnsurePlayerSpawned(clientId);
        }

        positionedClientIds.Remove(clientId);
        CoordinatePlayersStartPositions(forceRepositionAll: false);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        positionedClientIds.Remove(clientId);
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

        // Si el jugador ya viene spawneado desde el Lobby (o por NetworkManager), reutilizarlo
        if (networkClient.PlayerObject != null)
        {
            return;
        }

        GameObject prefabToSpawn = playerPrefab != null
            ? playerPrefab.gameObject
            : NetworkManager.Singleton.NetworkConfig.PlayerPrefab;

        if (prefabToSpawn == null)
        {
            Debug.LogWarning("No se encontró un PlayerPrefab asignado en PlayerManagerNetwork ni en NetworkManager.");
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

    public void CoordinatePlayersStartPositions(bool forceRepositionAll)
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null)
        {
            return;
        }

        if (forceRepositionAll)
        {
            positionedClientIds.Clear();
        }

        List<NetworkObject> playerObjects = NetworkManager.Singleton.SpawnManager.SpawnedObjectsList
            .Where(obj => obj != null && obj.IsPlayerObject)
            .OrderBy(obj => obj.OwnerClientId)
            .ToList();

        for (int i = 0; i < playerObjects.Count; i++)
        {
            NetworkObject playerObj = playerObjects[i];
            ulong ownerId = playerObj.OwnerClientId;

            // Configurar la cámara y AudioListener: en la partida solo debe estar activa la del jugador local (IsOwner)
            ConfigurePlayerCamera(playerObj, playerObj.IsOwner);

            if (!positionedClientIds.Contains(ownerId))
            {
                Transform spawn = GetSpawnPoint(i);
                if (spawn != null)
                {
                    PlacePlayerAtSpawn(playerObj, spawn);
                    positionedClientIds.Add(ownerId);
                }
            }
        }
    }

    private void PlacePlayerAtSpawn(NetworkObject playerObj, Transform spawn)
    {
        CharacterController cc = playerObj.GetComponent<CharacterController>();
        if (cc != null)
        {
            cc.enabled = false;
        }

        playerObj.transform.SetPositionAndRotation(spawn.position, spawn.rotation);

        if (cc != null)
        {
            cc.enabled = true;
        }
    }

    private void ConfigurePlayerCamera(NetworkObject playerObj, bool isLocalOwner)
    {
        Camera[] cameras = playerObj.GetComponentsInChildren<Camera>(true);
        foreach (Camera cam in cameras)
        {
            if (cam.enabled != isLocalOwner)
            {
                cam.enabled = isLocalOwner;
            }

            if (isLocalOwner && !cam.gameObject.activeSelf)
            {
                cam.gameObject.SetActive(true);
            }
        }

        AudioListener[] listeners = playerObj.GetComponentsInChildren<AudioListener>(true);
        foreach (AudioListener listener in listeners)
        {
            if (listener.enabled != isLocalOwner)
            {
                listener.enabled = isLocalOwner;
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
}
