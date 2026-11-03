using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Utilidades compartidas por las escenas con jugadores en red (Lobby y Partida):
/// búsqueda de spawn points, spawn de PlayerObjects, recolección ordenada de jugadores
/// y activación de componentes locales (cámara, audio, UI).
/// Sin allocations en las rutas que se ejecutan con frecuencia.
/// </summary>
public static class NetworkPlayerUtility
{
    public static readonly Comparison<NetworkObject> CompareByOwnerClientId =
        (a, b) => a.OwnerClientId.CompareTo(b.OwnerClientId);

    public static readonly Comparison<Transform> CompareByName = (a, b) =>
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        return string.CompareOrdinal(a.name, b.name);
    };

    public static readonly Comparison<Transform> CompareByParentThenName = (a, b) =>
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        string parentA = a.parent != null ? a.parent.name : string.Empty;
        string parentB = b.parent != null ? b.parent.name : string.Empty;
        int parentCmp = string.CompareOrdinal(parentA, parentB);
        return parentCmp != 0 ? parentCmp : string.CompareOrdinal(a.name, b.name);
    };

    // Buffers reutilizables sin Garbage Collection (solo hilo principal)
    private static readonly List<Camera> camerasBuffer = new List<Camera>(2);
    private static readonly List<AudioListener> listenersBuffer = new List<AudioListener>(2);
    private static readonly List<Canvas> canvasesBuffer = new List<Canvas>(2);

    // Spawn points activos de la escena actual para posicionar en el mismo instante de OnNetworkSpawn (Frame 0)
    private static Transform[] activeSceneSpawnPoints;
    public static event Action OnAnyPlayerSpawned;

    public static void SetActiveSceneSpawnPoints(Transform[] spawnPoints)
    {
        activeSceneSpawnPoints = spawnPoints;
    }

    public static void NotifyPlayerSpawned()
    {
        OnAnyPlayerSpawned?.Invoke();
    }

    public static Transform GetActiveSceneSpawnForClient(ulong clientId)
    {
        if (activeSceneSpawnPoints == null || activeSceneSpawnPoints.Length == 0 || activeSceneSpawnPoints[0] == null)
        {
            activeSceneSpawnPoints = FindSpawnPoints(null, CompareByName);
        }

        if (activeSceneSpawnPoints == null || activeSceneSpawnPoints.Length == 0) return null;
        int index = GetPlayerIndex(clientId);
        return GetSpawnPoint(activeSceneSpawnPoints, index);
    }

    /// <summary>
    /// Busca los spawn points: primero en el contenedor indicado, luego en "SpawnPoints"/"Spawns"
    /// y, como último recurso, cualquier Transform de la escena cuyo nombre empiece por "spawn".
    /// </summary>
    public static Transform[] FindSpawnPoints(Transform root, Comparison<Transform> comparison)
    {
        if (root == null || root.childCount == 0)
        {
            GameObject group = GameObject.Find("SpawnPoints");
            if (group == null) group = GameObject.Find("Spawns");
            root = group != null ? group.transform : null;
        }

        if (root != null && root.childCount > 0)
        {
            int childCount = root.childCount;
            Transform[] children = new Transform[childCount];
            for (int i = 0; i < childCount; i++)
            {
                children[i] = root.GetChild(i);
            }
            Array.Sort(children, comparison);
            return children;
        }

        Transform[] allTransforms = UnityEngine.Object.FindObjectsByType<Transform>();
        List<Transform> found = new List<Transform>(4);
        for (int i = 0; i < allTransforms.Length; i++)
        {
            Transform t = allTransforms[i];
            if (t != null && t.name.StartsWith("spawn", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(t);
            }
        }

        found.Sort(comparison);
        return found.ToArray();
    }

    public static Transform GetSpawnPoint(Transform[] spawnPoints, int index)
    {
        if (spawnPoints == null || spawnPoints.Length == 0) return null;
        return spawnPoints[Mathf.Clamp(index, 0, spawnPoints.Length - 1)];
    }

    /// <summary>Índice estable del cliente entre los conectados (por orden de ClientId), válido en Host y Clientes.</summary>
    public static int GetPlayerIndex(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null) return 0;

        int index = 0;
        if (nm.IsServer)
        {
            IReadOnlyList<ulong> connectedIds = nm.ConnectedClientsIds;
            for (int i = 0; i < connectedIds.Count; i++)
            {
                if (connectedIds[i] < clientId) index++;
            }
            return index;
        }

        if (nm.SpawnManager != null)
        {
            foreach (NetworkObject obj in nm.SpawnManager.SpawnedObjectsList)
            {
                if (obj != null && obj.IsPlayerObject && obj.OwnerClientId < clientId)
                {
                    index++;
                }
            }
        }
        return index;
    }

    /// <summary>
    /// Número de jugadores esperados: en servidor los clientes conectados; en cliente los ya replicados.
    /// </summary>
    public static int GetExpectedPlayerCount(int spawnedPlayers)
    {
        NetworkManager nm = NetworkManager.Singleton;
        return nm != null && nm.IsServer ? nm.ConnectedClientsIds.Count : spawnedPlayers;
    }

    /// <summary>Rellena el buffer con los PlayerObjects replicados, ordenados por OwnerClientId.</summary>
    public static void CollectSpawnedPlayers(List<NetworkObject> buffer)
    {
        buffer.Clear();

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null) return;

        foreach (NetworkObject obj in nm.SpawnManager.SpawnedObjectsList)
        {
            if (obj != null && obj.IsPlayerObject)
            {
                buffer.Add(obj);
            }
        }

        if (buffer.Count > 1)
        {
            buffer.Sort(CompareByOwnerClientId);
        }
    }

    public static void EnsureAllConnectedPlayersSpawned(NetworkObject playerPrefab, Transform[] spawnPoints)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;

        IReadOnlyList<ulong> clientIds = nm.ConnectedClientsIds;
        for (int i = 0; i < clientIds.Count; i++)
        {
            EnsurePlayerSpawned(clientIds[i], playerPrefab, spawnPoints);
        }
    }

    /// <summary>Instancia y spawnea el PlayerObject del cliente si aún no tiene uno (solo servidor).</summary>
    public static void EnsurePlayerSpawned(ulong clientId, NetworkObject playerPrefab, Transform[] spawnPoints)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;

        if (!nm.ConnectedClients.TryGetValue(clientId, out NetworkClient networkClient) || networkClient.PlayerObject != null)
        {
            return;
        }

        GameObject prefabToSpawn = playerPrefab != null ? playerPrefab.gameObject : nm.NetworkConfig.PlayerPrefab;
        if (prefabToSpawn == null)
        {
            Debug.LogWarning("[NetworkPlayerUtility] No PlayerPrefab assigned in the scene manager or NetworkManager.");
            return;
        }

        Transform spawn = GetSpawnPoint(spawnPoints, GetPlayerIndex(clientId));
        Vector3 pos = spawn != null ? spawn.position : Vector3.zero;
        Quaternion rot = spawn != null ? spawn.rotation : Quaternion.identity;

        GameObject instance = UnityEngine.Object.Instantiate(prefabToSpawn, pos, rot);
        if (instance.TryGetComponent(out NetworkObject netObj))
        {
            netObj.SpawnAsPlayerObject(clientId, true);
        }
    }

    /// <summary>
    /// Teletransporta al jugador al spawn desactivando temporalmente su CharacterController.
    /// </summary>
    public static void PlaceAtSpawn(NetworkObject playerObj, Transform spawn, bool reenableController)
    {
        if (playerObj.TryGetComponent(out PlayerMovement pm))
        {
            pm.TeleportTo(spawn.position, spawn.rotation, reenableController);
            return;
        }

        Transform t = playerObj.transform;
        if (playerObj.TryGetComponent(out CharacterController cc))
        {
            cc.enabled = false;
            t.SetPositionAndRotation(spawn.position, spawn.rotation);
            if (reenableController) cc.enabled = true;
        }
        else
        {
            t.SetPositionAndRotation(spawn.position, spawn.rotation);
        }
    }

    /// <summary>
    /// Activa/desactiva cámara, AudioListener y Canvas del jugador. Delegado en PlayerMovement si existe.
    /// </summary>
    public static void SetPlayerLocalComponentsActive(NetworkObject playerObj, bool active)
    {
        if (playerObj.TryGetComponent(out PlayerMovement pm))
        {
            pm.SetPlayerComponentsActive(active);
            return;
        }

        // Respaldo defensivo si el prefab no contiene PlayerMovement
        playerObj.GetComponentsInChildren(true, camerasBuffer);
        for (int i = 0; i < camerasBuffer.Count; i++)
        {
            Camera cam = camerasBuffer[i];
            if (cam.enabled != active) cam.enabled = active;
            if (active && !cam.gameObject.activeSelf) cam.gameObject.SetActive(true);
        }

        playerObj.GetComponentsInChildren(true, listenersBuffer);
        for (int i = 0; i < listenersBuffer.Count; i++)
        {
            AudioListener listener = listenersBuffer[i];
            if (listener.enabled != active) listener.enabled = active;
        }

        GameObject root = playerObj.gameObject;
        playerObj.GetComponentsInChildren(true, canvasesBuffer);
        for (int i = 0; i < canvasesBuffer.Count; i++)
        {
            Canvas canvas = canvasesBuffer[i];
            if (canvas.enabled != active) canvas.enabled = active;
            if (canvas.gameObject != root && canvas.gameObject.activeSelf != active) canvas.gameObject.SetActive(active);
        }

        // No retener referencias a objetos que podrían destruirse
        camerasBuffer.Clear();
        listenersBuffer.Clear();
        canvasesBuffer.Clear();
    }
}
