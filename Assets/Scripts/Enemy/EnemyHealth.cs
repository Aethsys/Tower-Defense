using System;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class EnemyHealth : NetworkBehaviour
{
    [Header("Health Settings")]
    [SerializeField] private int maxHealth = 30;

    private readonly NetworkVariable<int> currentHealth = new NetworkVariable<int>(
        30,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private bool isDead;
    private bool hasDespawned;

    public int MaxHealth => maxHealth;
    public int CurrentHealth => IsSpawned ? currentHealth.Value : maxHealth;
    public bool IsDead => isDead || CurrentHealth <= 0;

    public event Action<int, int> OnHealthChanged;
    public event Action<EnemyHealth> OnEnemyDied;

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
