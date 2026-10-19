using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class EnemyManagerNetwork : MonoBehaviour
{
    public static EnemyManagerNetwork Instance { get; private set; }

    [Serializable]
    public struct SpawnRouteAssignment
    {
        public Transform spawnPoint;
        public EnemyPath path;
    }

    [Header("Spawn References")]
    [SerializeField] private SpawnRouteAssignment[] spawnRoutes;
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private NetworkObject[] enemyPrefabs;

    [Header("Wave Settings")]
    [SerializeField] private bool autoStartWaves = true;
    [SerializeField] private float initialDelay = 5f;
    [Tooltip("Total de oleadas. 0 o negativo para oleadas infinitas.")]
    [SerializeField] private int totalWaves = 5;
    [SerializeField] private int baseEnemiesPerWave = 2;
    [SerializeField] private int additionalEnemiesPerWave = 2;
    [SerializeField] private float timeBetweenSpawns = 1.5f;
    [SerializeField] private float timeBetweenWaves = 5f;
    [SerializeField] private bool waitUntilWaveCleared = true;

    [Header("Spawn Feel & Variation")]
    [Tooltip("Radio de dispersión aleatoria alrededor del punto de spawn para evitar superposiciones iniciales.")]
    [SerializeField] private float spawnJitterRadius = 0.6f;
    [Tooltip("Ligero incremento de velocidad de los enemigos en cada nueva oleada (porcentaje).")]
    [SerializeField] private float speedScalingPerWave = 0.03f;

    private static readonly WaitForSeconds WaitQuarterSecond = new WaitForSeconds(0.25f);
    private static readonly Comparison<Transform> CompareTransformByName =
        (a, b) => string.CompareOrdinal(a.name, b.name);

    private readonly List<NetworkObject> activeEnemies = new List<NetworkObject>(32);
    private readonly List<NetworkObject> validEnemyPrefabs = new List<NetworkObject>(8);
    private readonly Dictionary<Transform, EnemyPath> spawnRouteMap = new Dictionary<Transform, EnemyPath>(8);
    private Coroutine waveRoutine;
    private bool wavesStarted;
    private int nextSpawnPointIndex;

    public int CurrentWave { get; private set; }
    public int TotalWaves => totalWaves;
    public bool IsSpawningWave { get; private set; }
    public float NextWaveTimer { get; private set; }

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

    public event Action<int, int> OnWaveStarted;
    public event Action<int> OnWaveCompleted;
    public event Action OnAllWavesCleared;
    public event Action<NetworkObject> OnEnemySpawned;

    private void Awake()
    {
        useGUILayout = false;

        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        RebuildSpawnRouteCache();

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

    private void RebuildSpawnRouteCache()
    {
        spawnRouteMap.Clear();

        if (spawnRoutes == null || spawnRoutes.Length == 0)
        {
            return;
        }

        bool needSpawnPointsFromRoutes = spawnPoints == null || spawnPoints.Length == 0;
        List<Transform> extractedSpawns = needSpawnPointsFromRoutes ? new List<Transform>(spawnRoutes.Length) : null;

        for (int i = 0; i < spawnRoutes.Length; i++)
        {
            SpawnRouteAssignment route = spawnRoutes[i];
            if (route.spawnPoint == null)
            {
                continue;
            }

            if (route.path != null)
            {
                spawnRouteMap[route.spawnPoint] = route.path;
            }

            if (needSpawnPointsFromRoutes && !extractedSpawns.Contains(route.spawnPoint))
            {
                extractedSpawns.Add(route.spawnPoint);
            }
        }

        if (needSpawnPointsFromRoutes && extractedSpawns.Count > 0)
        {
            spawnPoints = extractedSpawns.ToArray();
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
            if (t != null)
            {
                string name = t.name;
                bool isEnemySpawn = name.IndexOf("EnemySpawn", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isGenericSpawn = name.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                      name.IndexOf("Player", StringComparison.OrdinalIgnoreCase) < 0;

                if (isEnemySpawn || isGenericSpawn)
                {
                    found.Add(t);
                }
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
        NextWaveTimer = 0f;
    }

    private IEnumerator RunWavesCoroutine()
    {
        if (initialDelay > 0f)
        {
            float countdown = initialDelay;
            while (countdown > 0f)
            {
                NextWaveTimer = countdown;
                countdown -= Time.deltaTime;
                yield return null;
            }
            NextWaveTimer = 0f;
        }

        WaitForSeconds spawnWait = timeBetweenSpawns > 0f ? new WaitForSeconds(timeBetweenSpawns) : null;
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
            OnWaveStarted?.Invoke(CurrentWave, enemiesThisWave);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[EnemyManagerNetwork] Starting Wave {CurrentWave} with {enemiesThisWave} enemies.");
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

            OnWaveCompleted?.Invoke(CurrentWave);

            bool hasMoreWaves = totalWaves <= 0 || CurrentWave < totalWaves;
            if (hasMoreWaves)
            {
                float waveCountdown = timeBetweenWaves;
                while (waveCountdown > 0f)
                {
                    NextWaveTimer = waveCountdown;
                    waveCountdown -= Time.deltaTime;
                    yield return null;
                }
                NextWaveTimer = 0f;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log("[EnemyManagerNetwork] All waves completed.");
#endif
        OnAllWavesCleared?.Invoke();
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
            Debug.LogWarning("[EnemyManagerNetwork] No enemy prefabs assigned in EnemyManagerNetwork.");
            return null;
        }

        Transform spawn = customSpawnPoint != null ? customSpawnPoint : GetNextSpawnPoint();
        Vector3 basePosition = spawn != null ? spawn.position : transform.position;
        Quaternion rotation = spawn != null ? spawn.rotation : Quaternion.identity;

        // Dispersión suave para que no aparezcan colisionando exactamente en el mismo punto
        Vector2 jitter = UnityEngine.Random.insideUnitCircle * spawnJitterRadius;
        Vector3 position = basePosition + new Vector3(jitter.x, 0f, jitter.y);

        EnemyPath assignedPath = GetPathForSpawn(spawn);
        if (assignedPath != null)
        {
            Vector3 targetLookPos = Vector3.zero;
            bool hasLookPos = false;

            if (assignedPath.WaypointCount > 1 && assignedPath.TryGetWaypointPosition(1, out Vector3 wp1))
            {
                if (assignedPath.TryGetWaypointPosition(0, out Vector3 wp0) && (wp0 - position).sqrMagnitude <= 9f)
                {
                    targetLookPos = wp1;
                    hasLookPos = true;
                }
                else if (assignedPath.TryGetWaypointPosition(0, out wp0))
                {
                    targetLookPos = wp0;
                    hasLookPos = true;
                }
            }
            else if (assignedPath.TryGetWaypointPosition(0, out Vector3 wp0))
            {
                targetLookPos = wp0;
                hasLookPos = true;
            }

            if (hasLookPos)
            {
                Vector3 toTarget = new Vector3(targetLookPos.x - position.x, 0f, targetLookPos.z - position.z);
                if (toTarget.sqrMagnitude > 0.001f)
                {
                    rotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
                }
            }
        }

        NetworkObject enemyInstance = Instantiate(prefabToSpawn, position, rotation);

        if (enemyInstance.TryGetComponent(out EnemyMovement movement))
        {
            if (assignedPath != null)
            {
                movement.SetPath(assignedPath);
            }

            if (speedScalingPerWave > 0f && CurrentWave > 1)
            {
                movement.SetSpeedMultiplier(1f + (CurrentWave - 1) * speedScalingPerWave);
            }
        }

        enemyInstance.Spawn(true);

        activeEnemies.Add(enemyInstance);
        OnEnemySpawned?.Invoke(enemyInstance);
        return enemyInstance;
    }

    private EnemyPath GetPathForSpawn(Transform spawnPoint)
    {
        if (spawnPoint == null)
        {
            return null;
        }

        if (spawnRouteMap.TryGetValue(spawnPoint, out EnemyPath cachedPath))
        {
            return cachedPath;
        }

        if (spawnRoutes != null)
        {
            for (int i = 0; i < spawnRoutes.Length; i++)
            {
                if (spawnRoutes[i].spawnPoint == spawnPoint && spawnRoutes[i].path != null)
                {
                    spawnRouteMap[spawnPoint] = spawnRoutes[i].path;
                    return spawnRoutes[i].path;
                }
            }
        }

        if (spawnPoint.TryGetComponent(out EnemyPath attachedPath))
        {
            spawnRouteMap[spawnPoint] = attachedPath;
            return attachedPath;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.LogWarning($"[EnemyManagerNetwork] No EnemyPath assigned to spawn point: {spawnPoint.name}");
#endif
        spawnRouteMap[spawnPoint] = null;
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
        else if (destroy)
        {
            Destroy(enemy.gameObject);
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
                int last = activeEnemies.Count - 1;
                activeEnemies[i] = activeEnemies[last];
                activeEnemies.RemoveAt(last);
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
