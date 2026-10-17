using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class EnemyManagerNetwork : MonoBehaviour
{
    public static EnemyManagerNetwork Instance { get; private set; }

    [Header("Referencias de Spawn")]
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private NetworkObject[] enemyPrefabs;

    [Header("Configuración de Oleadas (Waves)")]
    [SerializeField] private bool autoStartWaves = true;
    [SerializeField] private float initialDelay = 3f;
    [Tooltip("Cantidad total de oleadas. Usa 0 o un valor negativo para oleadas infinitas.")]
    [SerializeField] private int totalWaves = 5;
    [SerializeField] private int baseEnemiesPerWave = 5;
    [SerializeField] private int additionalEnemiesPerWave = 2;
    [SerializeField] private float timeBetweenSpawns = 1.5f;
    [SerializeField] private float timeBetweenWaves = 5f;
    [SerializeField] private bool waitUntilWaveCleared = true;

    private readonly List<NetworkObject> activeEnemies = new List<NetworkObject>();
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

    private void AutoFindEnemySpawnPoints()
    {
        Transform[] allTransforms = FindObjectsByType<Transform>();
        spawnPoints = allTransforms
            .Where(t => t != null && t.name.IndexOf("EnemySpawn", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(t => t.name)
            .ToArray();
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
            Debug.Log($"[EnemyManagerNetwork] Iniciando Oleada {CurrentWave} con {enemiesThisWave} enemigos.");

            for (int i = 0; i < enemiesThisWave; i++)
            {
                SpawnEnemy();

                if (i < enemiesThisWave - 1 && timeBetweenSpawns > 0f)
                {
                    yield return new WaitForSeconds(timeBetweenSpawns);
                }
            }

            IsSpawningWave = false;

            if (waitUntilWaveCleared)
            {
                while (ActiveEnemiesCount > 0)
                {
                    yield return new WaitForSeconds(0.25f);
                }
            }

            bool hasMoreWaves = totalWaves <= 0 || CurrentWave < totalWaves;
            if (hasMoreWaves && timeBetweenWaves > 0f)
            {
                yield return new WaitForSeconds(timeBetweenWaves);
            }
        }

        Debug.Log("[EnemyManagerNetwork] Todas las oleadas han finalizado.");
        waveRoutine = null;
    }

    public NetworkObject SpawnEnemy(Transform customSpawnPoint = null, NetworkObject customPrefab = null)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        {
            Debug.LogWarning("[EnemyManagerNetwork] Solo el servidor/host puede spawnear enemigos.");
            return null;
        }

        NetworkObject prefabToSpawn = customPrefab != null ? customPrefab : GetRandomEnemyPrefab();
        if (prefabToSpawn == null)
        {
            Debug.LogWarning("[EnemyManagerNetwork] No hay prefabs de enemigos asignados en EnemyManagerNetwork.");
            return null;
        }

        Transform spawn = customSpawnPoint != null ? customSpawnPoint : GetNextSpawnPoint();
        Vector3 position = spawn != null ? spawn.position : transform.position;
        Quaternion rotation = spawn != null ? spawn.rotation : Quaternion.identity;

        NetworkObject enemyInstance = Instantiate(prefabToSpawn, position, rotation);
        enemyInstance.Spawn(true);

        activeEnemies.Add(enemyInstance);
        return enemyInstance;
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
        if (enemyPrefabs == null || enemyPrefabs.Length == 0)
        {
            return null;
        }

        List<NetworkObject> validPrefabs = enemyPrefabs.Where(p => p != null).ToList();
        if (validPrefabs.Count == 0)
        {
            return null;
        }

        int randomIndex = UnityEngine.Random.Range(0, validPrefabs.Count);
        return validPrefabs[randomIndex];
    }

    private Transform GetNextSpawnPoint()
    {
        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            return null;
        }

        List<Transform> validSpawns = spawnPoints.Where(s => s != null).ToList();
        if (validSpawns.Count == 0)
        {
            return null;
        }

        Transform selected = validSpawns[nextSpawnPointIndex % validSpawns.Count];
        nextSpawnPointIndex = (nextSpawnPointIndex + 1) % validSpawns.Count;
        return selected;
    }
}
