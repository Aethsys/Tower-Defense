using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class EnemyHealth : NetworkBehaviour
{
    private static readonly Dictionary<Collider, EnemyHealth> ColliderToEnemyMap = new Dictionary<Collider, EnemyHealth>(64);

    [Header("Health Settings")]
    [SerializeField] private int maxHealth = 30;

    private readonly NetworkVariable<int> currentHealth = new NetworkVariable<int>(
        30,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private Collider[] cachedColliders;
    private bool isDead;
    private bool hasDespawned;

    public int MaxHealth => maxHealth;
    public int CurrentHealth => IsSpawned ? currentHealth.Value : maxHealth;
    public bool IsDead => isDead || CurrentHealth <= 0;

    public event Action<int, int> OnHealthChanged;
    public event Action<EnemyHealth> OnEnemyDied;

    public static bool TryGetEnemy(Collider col, out EnemyHealth enemy)
    {
        if (col == null)
        {
            enemy = null;
            return false;
        }

        if (ColliderToEnemyMap.TryGetValue(col, out enemy))
        {
            return enemy != null;
        }

        if (col.TryGetComponent(out enemy) || (enemy = col.GetComponentInParent<EnemyHealth>()) != null)
        {
            ColliderToEnemyMap[col] = enemy;
            return true;
        }

        return false;
    }

    private void Awake()
    {
        useGUILayout = false;
        cachedColliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cachedColliders.Length; i++)
        {
            Collider col = cachedColliders[i];
            if (col != null)
            {
                ColliderToEnemyMap[col] = this;
            }
        }
    }

    public override void OnDestroy()
    {
        if (cachedColliders != null)
        {
            for (int i = 0; i < cachedColliders.Length; i++)
            {
                Collider col = cachedColliders[i];
                if (col != null)
                {
                    ColliderToEnemyMap.Remove(col);
                }
            }
        }

        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        isDead = false;
        hasDespawned = false;
        currentHealth.OnValueChanged += HandleHealthChanged;

        if (IsServer)
        {
            currentHealth.Value = maxHealth;
        }
    }

    public override void OnNetworkDespawn()
    {
        currentHealth.OnValueChanged -= HandleHealthChanged;
        base.OnNetworkDespawn();
    }

    public void TakeDamage(int damage)
    {
        if (damage <= 0 || isDead) return;

        if (!IsSpawned)
        {
            isDead = true;
            OnEnemyDied?.Invoke(this);
            Destroy(gameObject);
            return;
        }

        if (IsServer)
        {
            ApplyDamageInternal(damage);
        }
        else
        {
            TakeDamageServerRpc(damage);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TakeDamageServerRpc(int damage)
    {
        ApplyDamageInternal(damage);
    }

    private void ApplyDamageInternal(int damage)
    {
        if (!IsServer || damage <= 0 || isDead || currentHealth.Value <= 0)
        {
            return;
        }

        int nextHealth = Mathf.Max(0, currentHealth.Value - damage);
        currentHealth.Value = nextHealth;

        if (nextHealth <= 0)
        {
            DieServer();
        }
    }

    private void HandleHealthChanged(int previousValue, int newValue)
    {
        OnHealthChanged?.Invoke(newValue, maxHealth);

        if (previousValue > 0 && newValue <= 0)
        {
            isDead = true;
            OnEnemyDied?.Invoke(this);
        }
    }

    private void DieServer()
    {
        if (hasDespawned) return;
        hasDespawned = true;
        isDead = true;

        if (EnemyManagerNetwork.Instance != null)
        {
            EnemyManagerNetwork.Instance.DespawnEnemy(NetworkObject, true);
        }
        else if (NetworkObject != null && NetworkObject.IsSpawned)
        {
            NetworkObject.Despawn(true);
        }
        else
        {
            Destroy(gameObject);
        }
    }
}
