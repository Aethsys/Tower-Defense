using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PlayerManagerNetwork : MonoBehaviour
{
    [Header("Map Spawn Points")]
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private NetworkObject playerPrefab;

    private static readonly Comparison<NetworkObject> CompareByOwnerClientId =
        (a, b) => a.OwnerClientId.CompareTo(b.OwnerClientId);

    private static readonly Comparison<Transform> CompareSpawnTransform = (a, b) =>
    {
        string parentA = a.parent != null ? a.parent.name : string.Empty;
        string parentB = b.parent != null ? b.parent.name : string.Empty;
        int parentCmp = string.CompareOrdinal(parentA, parentB);
        return parentCmp != 0 ? parentCmp : string.CompareOrdinal(a.name, b.name);
    };

    private readonly HashSet<ulong> positionedClientIds = new HashSet<ulong>();
    private readonly List<NetworkObject> playerObjectsBuffer = new List<NetworkObject>(4);
    private bool needsCoordination = true;

    private void Awake()
    {
        useGUILayout = false;

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
        if (needsCoordination)
        {
            CoordinatePlayersStartPositions(forceRepositionAll: false);
        }
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
        List<Transform> found = new List<Transform>(4);

        for (int i = 0; i < allTransforms.Length; i++)
        {
            Transform t = allTransforms[i];
            if (t != null && t.name.StartsWith("spawn", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(t);
            }
        }

        found.Sort(CompareSpawnTransform);
        spawnPoints = found.ToArray();
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
        needsCoordination = true;
        CoordinatePlayersStartPositions(forceRepositionAll: false);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        positionedClientIds.Remove(clientId);
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
            Debug.LogWarning("No PlayerPrefab assigned in PlayerManagerNetwork or NetworkManager.");
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
            needsCoordination = true;
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

        if (playerObjectsBuffer.Count == 0)
        {
            return;
        }

        if (playerObjectsBuffer.Count > 1)
        {
            playerObjectsBuffer.Sort(CompareByOwnerClientId);
        }

        bool allPositioned = true;
        for (int i = 0; i < playerObjectsBuffer.Count; i++)
        {
            NetworkObject playerObj = playerObjectsBuffer[i];
            ulong ownerId = playerObj.OwnerClientId;

            if (positionedClientIds.Add(ownerId))
            {
                ConfigurePlayerCamera(playerObj, playerObj.IsOwner);

                Transform spawn = GetSpawnPoint(i);
                if (spawn != null)
                {
                    PlacePlayerAtSpawn(playerObj, spawn);
                }
            }

            if (!positionedClientIds.Contains(ownerId))
            {
                allPositioned = false;
            }
        }

        int expectedPlayers = NetworkManager.Singleton.IsServer
            ? NetworkManager.Singleton.ConnectedClientsIds.Count
            : playerObjectsBuffer.Count;

        if (allPositioned && positionedClientIds.Count >= expectedPlayers && expectedPlayers > 0)
        {
            needsCoordination = false;
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
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera cam = cameras[i];
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
        for (int i = 0; i < listeners.Length; i++)
        {
            AudioListener listener = listeners[i];
            if (listener.enabled != isLocalOwner)
            {
                listener.enabled = isLocalOwner;
            }
        }

        Canvas[] canvases = playerObj.GetComponentsInChildren<Canvas>(true);
        for (int i = 0; i < canvases.Length; i++)
        {
            Canvas canvas = canvases[i];
            if (canvas.enabled != isLocalOwner)
            {
                canvas.enabled = isLocalOwner;
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
}
