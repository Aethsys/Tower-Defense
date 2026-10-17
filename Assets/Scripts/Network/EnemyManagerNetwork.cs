using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class EnemyManagerNetwork : MonoBehaviour
{
    public static EnemyManagerNetwork Instance { get; private set; }

    [System.Serializable]
    private class SpawnRouteAssignment
    {
        public Transform spawnPoint;
        public EnemyPath path;
    }

    [SerializeField] private SpawnRouteAssignment[] spawnRoutes;

    [Header("Spawn References")]
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private NetworkObject[] enemyPrefabs;

    [Header("Wave Settings")]
    [SerializeField] private bool autoStartWaves = true;
    [SerializeField] private float initialDelay = 3f;
    [Tooltip("Total number of waves. Use 0 or a negative value for infinite waves.")]
    [SerializeField] private int totalWaves = 5;
    [SerializeField] private int baseEnemiesPerWave = 5;
    [SerializeField] private int additionalEnemiesPerWave = 2;
    [SerializeField] private float timeBetweenSpawns = 1.5f;
    [SerializeField] private float timeBetweenWaves = 5f;
    [SerializeField] private bool waitUntilWaveCleared = true;

    private static readonly WaitForSeconds WaitQuarterSecond = new WaitForSeconds(0.25f);
    private static readonly Comparison<Transform> CompareTransformByName =
        (a, b) => string.CompareOrdinal(a.name, b.name);

    private readonly List<NetworkObject> activeEnemies = new List<NetworkObject>(32);
    private readonly List<NetworkObject> validEnemyPrefabs = new List<NetworkObject>(8);
    private Coroutine waveRoutine;
    private bool wavesStarted;
    private int nextSpawnPointIndex;

    public int CurrentWave { get; private set; }
    public bool IsSpawningWave { get; private set; }
    public int ActiveEnemiesCount
    {
        get
        {
            CleanupDestroyedEnemies();
            return activeEnemies.Count;
        }
    }

    public IReadOnlyList<NetworkObject> ActiveEnemies
    {
        get
        {
            CleanupDestroyedEnemies();
            return activeEnemies;
        }
    }

    private void Awake()
    {
        useGUILayout = false;

        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            AutoFindEnemySpawnPoints();
        }

        RebuildValidPrefabCache();
    }

    private void Start()
    {
        if (NetworkManager.Singleton != null)
        {
            if (NetworkManager.Singleton.SceneManager != null)
            {
                NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += OnSceneLoadCompleted;
            }

            if (NetworkManager.Singleton.IsServer && autoStartWaves)
            {
                StartWaves();
            }
        }
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.SceneManager != null)
        {
            NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= OnSceneLoadCompleted;
        }

        StopWaves();
    }

    private void OnSceneLoadCompleted(string sceneName, LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && autoStartWaves && !wavesStarted)
        {
            StartWaves();
        }
    }

    private void RebuildValidPrefabCache()
    {
        validEnemyPrefabs.Clear();
        if (enemyPrefabs == null)
        {
            return;
        }

        for (int i = 0; i < enemyPrefabs.Length; i++)
        {
            if (enemyPrefabs[i] != null)
            {
                validEnemyPrefabs.Add(enemyPrefabs[i]);
            }
        }
    }

    private void AutoFindEnemySpawnPoints()
    {
        Transform[] allTransforms = FindObjectsByType<Transform>();
        List<Transform> found = new List<Transform>(8);

        for (int i = 0; i < allTransforms.Length; i++)
        {
            Transform t = allTransforms[i];
            if (t != null && t.name.IndexOf("EnemySpawn", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found.Add(t);
            }
        }

        found.Sort(CompareTransformByName);
        spawnPoints = found.ToArray();
    }

    public void StartWaves()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        {
            return;
        }

        if (waveRoutine != null)
        {
            StopCoroutine(waveRoutine);
        }

        wavesStarted = true;
        waveRoutine = StartCoroutine(RunWavesCoroutine());
    }

    public void StopWaves()
    {
        if (waveRoutine != null)
        {
            StopCoroutine(waveRoutine);
            waveRoutine = null;
        }

        IsSpawningWave = false;
        wavesStarted = false;
    }

    private IEnumerator RunWavesCoroutine()
    {
        if (initialDelay > 0f)
        {
            yield return new WaitForSeconds(initialDelay);
        }

        WaitForSeconds spawnWait = timeBetweenSpawns > 0f ? new WaitForSeconds(timeBetweenSpawns) : null;
        WaitForSeconds waveWait = timeBetweenWaves > 0f ? new WaitForSeconds(timeBetweenWaves) : null;

        CurrentWave = 0;

        while (totalWaves <= 0 || CurrentWave < totalWaves)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            {
                yield break;
            }

            CurrentWave++;
            IsSpawningWave = true;

            int enemiesThisWave = Mathf.Max(1, baseEnemiesPerWave + (CurrentWave - 1) * additionalEnemiesPerWave);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UnityEngine.Debug.Log($"[EnemyManagerNetwork] Starting Wave {CurrentWave} with {enemiesThisWave} enemies.");
#endif

            for (int i = 0; i < enemiesThisWave; i++)
            {
                SpawnEnemy();

                if (i < enemiesThisWave - 1 && spawnWait != null)
                {
                    yield return spawnWait;
                }
            }

            IsSpawningWave = false;

            if (waitUntilWaveCleared)
            {
                while (ActiveEnemiesCount > 0)
                {
                    yield return WaitQuarterSecond;
                }
            }

            bool hasMoreWaves = totalWaves <= 0 || CurrentWave < totalWaves;
            if (hasMoreWaves && waveWait != null)
            {
                yield return waveWait;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        UnityEngine.Debug.Log("[EnemyManagerNetwork] All waves completed.");
#endif
        waveRoutine = null;
    }

    public NetworkObject SpawnEnemy(Transform customSpawnPoint = null, NetworkObject customPrefab = null)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        {
            return null;
        }

        NetworkObject prefabToSpawn = customPrefab != null ? customPrefab : GetRandomEnemyPrefab();
        if (prefabToSpawn == null)
        {
            UnityEngine.Debug.LogWarning("[EnemyManagerNetwork] No enemy prefabs assigned in EnemyManagerNetwork.");
            return null;
        }

        Transform spawn = customSpawnPoint != null ? customSpawnPoint : GetNextSpawnPoint();
        Vector3 position = spawn != null ? spawn.position : transform.position;
        Quaternion rotation = spawn != null ? spawn.rotation : Quaternion.identity;

        /* NetworkObject enemyInstance = Instantiate(prefabToSpawn, position, rotation);
         enemyInstance.Spawn(true);*/

        NetworkObject enemyInstance = Instantiate(
        prefabToSpawn,
        position,
        rotation
);

        EnemyMovement movement = enemyInstance.GetComponent<EnemyMovement>();

        if (movement != null)
        {
            EnemyPath path = GetPathForSpawn(spawn);

            if (path != null)
            {
                movement.SetPath(path);
            }
        }

        enemyInstance.Spawn(true);

        activeEnemies.Add(enemyInstance);
        return enemyInstance;
    }

    private EnemyPath GetPathForSpawn(Transform spawnPoint)
    {
        if (spawnPoint == null || spawnRoutes == null)
            return null;

        for (int i = 0; i < spawnRoutes.Length; i++)
        {
            SpawnRouteAssignment assignment = spawnRoutes[i];

            if (assignment == null)
                continue;

            if (assignment.spawnPoint == spawnPoint)
                return assignment.path;
        }

        UnityEngine.Debug.LogWarning(
            $"No EnemyPath assigned to spawn point: {spawnPoint.name}"
        );

        return null;
    }

    public void DespawnEnemy(NetworkObject enemy, bool destroy = true)
    {
        if (enemy == null) return;

        activeEnemies.Remove(enemy);

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && enemy.IsSpawned)
        {
            enemy.Despawn(destroy);
        }
    }

    public void UnregisterEnemy(NetworkObject enemy)
    {
        if (enemy == null) return;
        activeEnemies.Remove(enemy);
    }

    private void CleanupDestroyedEnemies()
    {
        for (int i = activeEnemies.Count - 1; i >= 0; i--)
        {
            if (activeEnemies[i] == null || !activeEnemies[i].IsSpawned)
            {
                activeEnemies.RemoveAt(i);
            }
        }
    }

    private NetworkObject GetRandomEnemyPrefab()
    {
        if (validEnemyPrefabs.Count == 0)
        {
            RebuildValidPrefabCache();
            if (validEnemyPrefabs.Count == 0)
            {
                return null;
            }
        }

        int index = UnityEngine.Random.Range(0, validEnemyPrefabs.Count);
        return validEnemyPrefabs[index];
    }

    private Transform GetNextSpawnPoint()
    {
        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            return null;
        }

        int count = spawnPoints.Length;
        for (int attempt = 0; attempt < count; attempt++)
        {
            int index = (nextSpawnPointIndex + attempt) % count;
            if (spawnPoints[index] != null)
            {
                nextSpawnPointIndex = (index + 1) % count;
                return spawnPoints[index];
            }
        }

        return null;
    }
}
