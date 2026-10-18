using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(TowerHealth))]
public class BaitTower : NetworkBehaviour
{
    public static readonly List<BaitTower> ActiveBaits =
        new List<BaitTower>(8);

    [Header("Bait Settings")]
    [SerializeField] private float attractionRadius = 10f;
    [SerializeField] private float activeDuration = 8f;
    [SerializeField] private float cooldown = 20f;


    [Header("Destroyed State")]
    [SerializeField] private Light towerLight;
    [SerializeField, Min(0f)] private float normalLightIntensity = 2f;
    [SerializeField, Min(0f)] private float destroyedLightIntensity = 0.2f;


    [Header("Testing")]
    [SerializeField] private bool activateOnStart = false;

    private TowerHealth towerHealth;
    private NetworkVariable<bool> baitActive =
        new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    private Coroutine activationRoutine;
    private float nextActivationTime;

    public bool IsActive =>
        baitActive.Value &&
        towerHealth != null &&
        !towerHealth.IsDestroyed;

    public float AttractionRadius => attractionRadius;
    public TowerHealth Health => towerHealth;

    private void Awake()
    {
        towerHealth = GetComponent<TowerHealth>();
    }

    private void OnEnable()
    {
        if (!ActiveBaits.Contains(this))
            ActiveBaits.Add(this);
    }

    private void OnDisable()
    {
        ActiveBaits.Remove(this);
    }


    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();


        // 1. Obtener el componente de vida.
        if (towerHealth == null)
        {
            towerHealth = GetComponent<TowerHealth>();
        }

        if (towerHealth == null)
        {
            return;
        }

        // 2. Suscribirse al evento de destrucción.
        towerHealth.OnTowerDestroyed -= HandleTowerDestroyed;
        towerHealth.OnTowerDestroyed += HandleTowerDestroyed;

        towerHealth.OnHealthChanged -= OnHealthChanged;
        towerHealth.OnHealthChanged += OnHealthChanged;

        UpdateLightState(!towerHealth.IsDestroyed);

        // 3. Solo el servidor controla el estado del señuelo.
        if (!IsServer)
        {
            return;
        }

        // 4. Inicializar el estado de red.
        baitActive.Value = false;
        nextActivationTime = 0f;

        // 5. Validar la configuración.
        if (activeDuration <= 0f)
        {
            UnityEngine.Debug.LogError(
                $"[BaitTower] ERROR: Active Duration debe ser mayor que cero. " +
                $"Valor actual={activeDuration}",
                this
            );

            return;
        }

        if (attractionRadius <= 0f)
        {
            UnityEngine.Debug.LogWarning(
                $"[BaitTower] ADVERTENCIA: Attraction Radius no es válido. " +
                $"Valor actual={attractionRadius}",
                this
            );
        }

        // 6. Activación automática para pruebas.
        if (activateOnStart)
        {
            bool activated = TryActivate();

            if (activated)
            {
                UnityEngine.Debug.Log(
                    $"[BaitTower] ÉXITO: {name} activó el señuelo."
                );
            }
            else
            {
                UnityEngine.Debug.LogWarning(
                    $"[BaitTower] No se pudo activar {name}. " +
                    $"IsActive={IsActive}, " +
                    $"IsDestroyed={towerHealth.IsDestroyed}, " +
                    $"ActiveDuration={activeDuration}"
                );
            }
        }
        else
        {
            UnityEngine.Debug.Log(
                $"[BaitTower] {name} está inicializada, " +
                "pero Activate On Start está desactivado."
            );
        }
    }


    public override void OnNetworkDespawn()
    {
        if (towerHealth != null)
            towerHealth.OnTowerDestroyed -= HandleTowerDestroyed;

        if (IsServer)
        {
            if (activationRoutine != null)
                StopCoroutine(activationRoutine);

            activationRoutine = null;
            baitActive.Value = false;
        }

        if (towerHealth != null)
        {
            towerHealth.OnHealthChanged -= OnHealthChanged;
        }

        base.OnNetworkDespawn();
    }

    public bool TryActivate()
    {
        // Solo el servidor controla la activación.
        if (IsSpawned && !IsServer)
            return false;

        if (towerHealth == null || towerHealth.IsDestroyed)
            return false;

        if (IsActive || Time.time < nextActivationTime)
            return false;

        if (activeDuration <= 0f)
            return false;

        baitActive.Value = true;
        activationRoutine = StartCoroutine(ActivationTimer());

        return true;
    }

    private IEnumerator ActivationTimer()
    {
        float endTime = Time.time + activeDuration;

        while (Time.time < endTime &&
               towerHealth != null &&
               !towerHealth.IsDestroyed)
        {
            yield return null;
        }

        EndActivation();
    }


    private void HandleTowerDestroyed(TowerHealth tower)
    {
        if (tower != towerHealth)
            return;

        // La luz se actualiza también en los clientes.
        UpdateLightState(false);

        UnityEngine.Debug.Log(
            $"[BaitTower] {name} destruida. " +
            "Reduciendo intensidad de la luz."
        );

        // Solo el servidor controla el estado del señuelo.
        if (IsServer)
        {
            EndActivation();
        }
    }


    private void EndActivation()
    {
        if (IsServer && baitActive.Value)
        {
            baitActive.Value = false;
            nextActivationTime = Time.time + cooldown;
        }

        activationRoutine = null;
    }

    public bool CanAttract(Vector3 enemyPosition)
    {
        if (!IsActive)
            return false;

        Vector3 offset = transform.position - enemyPosition;
        offset.y = 0f;

        return offset.sqrMagnitude <=
               attractionRadius * attractionRadius;
    }


    private void OnHealthChanged(int currentHealth, int maxHealth)
    {
        UpdateLightState(currentHealth > 0);
    }


    private void UpdateLightState(bool operational)
    {
        if (towerLight == null)
        {
            UnityEngine.Debug.LogWarning(
                $"[BaitTower] {name}: no hay ninguna Light " +
                "asignada en el campo Tower Light.",
                this
            );
            return;
        }

        float previousIntensity = towerLight.intensity;

        towerLight.intensity = operational
            ? normalLightIntensity
            : destroyedLightIntensity;

        UnityEngine.Debug.Log(
            $"[BaitTower] {name}: luz actualizada. " +
            $"Operativa={operational}, " +
            $"Intensidad anterior={previousIntensity}, " +
            $"Intensidad nueva={towerLight.intensity}"
        );
    }


    /// <summary>
    /// Reconstruye la torre y restaura su vida y su iluminación.
    /// Esta función queda preparada para el futuro sistema de construcción.
    /// </summary>
    public bool RebuildTower()
    {
        if (IsSpawned && !IsServer)
            return false;

        if (towerHealth == null || !towerHealth.IsDestroyed)
            return false;

        if (!towerHealth.RestoreHealth())
            return false;

        UpdateLightState(true);

        nextActivationTime = 0f;
        baitActive.Value = false;

        UnityEngine.Debug.Log($"[BaitTower] {name} ha sido reconstruida.");

        return true;
    }

    [ContextMenu("TEST - Reconstruir Torre")]
    private void TestRebuildTower()
    {
        bool result = RebuildTower();

        UnityEngine.Debug.Log(
            $"[BaitTower] Prueba de reconstrucción: {result}",
            this
        );
    }

}
