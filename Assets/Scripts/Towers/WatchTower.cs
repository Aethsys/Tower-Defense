
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(TowerHealth))]
public class Watchtower : NetworkBehaviour
{
    public static readonly List<Watchtower> ActiveWatchtowers =
        new List<Watchtower>(8);

    [Header("Attraction Settings")]
    [SerializeField, Min(0f)] private float attractionRadius = 10f;

    [Header("Watchtower Lighting")]
    [SerializeField] private Light towerLight;
    [SerializeField, Min(0f)] private float normalLightIntensity = 3f;
    [SerializeField, Min(0f)] private float destroyedLightIntensity = 0.1f;

    private TowerHealth towerHealth;

    public bool IsActive =>
        towerHealth != null && !towerHealth.IsDestroyed;

    public float AttractionRadius => attractionRadius;
    public TowerHealth Health => towerHealth;

    private void Awake()
    {
        useGUILayout = false;
        towerHealth = GetComponent<TowerHealth>();
        if (towerLight == null)
        {
            towerLight = GetComponentInChildren<Light>(true);
        }
    }

    private void OnEnable()
    {
        if (!ActiveWatchtowers.Contains(this))
            ActiveWatchtowers.Add(this);
    }

    private void OnDisable()
    {
        ActiveWatchtowers.Remove(this);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (towerHealth == null)
            towerHealth = GetComponent<TowerHealth>();

        if (towerHealth == null)
        {
            UnityEngine.Debug.LogError(
                $"[Watchtower] {name}: falta TowerHealth.", this);
            return;
        }

        towerHealth.OnHealthChanged -= HandleHealthChanged;
        towerHealth.OnHealthChanged += HandleHealthChanged;

        towerHealth.OnTowerDestroyed -= HandleTowerDestroyed;
        towerHealth.OnTowerDestroyed += HandleTowerDestroyed;

        UpdateLighting(!towerHealth.IsDestroyed);

        UnityEngine.Debug.Log(
            $"[Watchtower] {name} registrada. Radio={attractionRadius}",
            this);
    }

    public override void OnNetworkDespawn()
    {
        if (towerHealth != null)
        {
            towerHealth.OnHealthChanged -= HandleHealthChanged;
            towerHealth.OnTowerDestroyed -= HandleTowerDestroyed;
        }

        base.OnNetworkDespawn();
    }

    public bool CanAttract(Vector3 enemyPosition)
    {
        if (!IsActive || attractionRadius <= 0f)
            return false;

        Vector3 offset = transform.position - enemyPosition;
        offset.y = 0f;

        return offset.sqrMagnitude <=
               attractionRadius * attractionRadius;
    }

    private void HandleHealthChanged(int currentHealth, int maxHealth)
    {
        UpdateLighting(currentHealth > 0);
    }

    private void HandleTowerDestroyed(TowerHealth tower)
    {
        if (tower != towerHealth)
            return;

        UpdateLighting(false);

        UnityEngine.Debug.Log(
            $"[Watchtower] {name} destruida.", this);
    }

    private void UpdateLighting(bool operational)
    {
        if (towerLight == null)
            return;

        towerLight.intensity = operational
            ? normalLightIntensity
            : destroyedLightIntensity;
    }
}
