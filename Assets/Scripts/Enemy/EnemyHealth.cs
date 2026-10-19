using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class EnemyHealth : NetworkBehaviour
{
    private static readonly Dictionary<Collider, EnemyHealth> ColliderToEnemyMap = new Dictionary<Collider, EnemyHealth>(64);
    private static readonly int ColorPropId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorPropId = Shader.PropertyToID("_BaseColor");
    private static readonly int EmissionColorPropId = Shader.PropertyToID("_EmissionColor");

    private static MaterialPropertyBlock sharedFlashBlock;
    private static MaterialPropertyBlock sharedClearBlock;

    [Header("Health Settings")]
    [SerializeField] private int maxHealth = 30;

    [Header("Visual Feedback")]
    [SerializeField] private Color hitFlashColor = new Color(1f, 0.35f, 0.35f, 1f);
    [SerializeField] private float hitFlashDuration = 0.12f;

    private readonly NetworkVariable<int> currentHealth = new NetworkVariable<int>(
        30,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private Collider[] cachedColliders;
    private Renderer[] cachedRenderers;
    private EnemyMovement enemyMovement;

    private int localHealth;
    private bool isDead;
    private float hitFlashTimer;
    private float lastLocalFeedbackTime = -10f;

    public int MaxHealth => maxHealth;
    public int CurrentHealth => IsSpawned ? currentHealth.Value : localHealth;
    public bool IsDead => isDead || CurrentHealth <= 0;

    public event Action<int, int> OnHealthChanged;
    public event Action<EnemyHealth> OnEnemyDied;
    public event Action<int, Vector3> OnHitReceived;

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
        localHealth = maxHealth;
        enemyMovement = GetComponent<EnemyMovement>();

        EnsureSharedBlocks();

        cachedColliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cachedColliders.Length; i++)
        {
            Collider col = cachedColliders[i];
            if (col != null)
            {
                ColliderToEnemyMap[col] = this;
            }
        }

        cachedRenderers = GetComponentsInChildren<Renderer>(true);
    }

    private void EnsureSharedBlocks()
    {
        if (sharedFlashBlock == null)
        {
            sharedFlashBlock = new MaterialPropertyBlock();
            sharedFlashBlock.SetColor(ColorPropId, hitFlashColor);
            sharedFlashBlock.SetColor(BaseColorPropId, hitFlashColor);
            sharedFlashBlock.SetColor(EmissionColorPropId, hitFlashColor * 0.5f);
        }

        if (sharedClearBlock == null)
        {
            sharedClearBlock = new MaterialPropertyBlock();
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
        localHealth = maxHealth;
        hitFlashTimer = 0f;
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

    private void Update()
    {
        if (hitFlashTimer > 0f)
        {
            hitFlashTimer -= Time.deltaTime;
            if (hitFlashTimer <= 0f)
            {
                ClearFlashColor();
            }
        }
    }

    public void TakeDamage(int damage)
    {
        TakeDamage(damage, Vector3.zero, 0f, 0f);
    }

    public void TakeDamage(int damage, Vector3 knockbackDir, float knockbackForce = 0f, float stunDuration = 0f)
    {
        if (damage <= 0 || isDead) return;

        if (!IsSpawned)
        {
            localHealth = Mathf.Max(0, localHealth - damage);
            PlayHitFeedbackLocal();

            if (enemyMovement != null)
            {
                enemyMovement.ApplyKnockback(knockbackDir, knockbackForce, stunDuration);
            }

            if (localHealth <= 0)
            {
                HandleDeathLocal();
                Destroy(gameObject);
            }
            return;
        }

        if (IsServer)
        {
            ApplyDamageInternal(damage, knockbackDir, knockbackForce, stunDuration);
        }
        else
        {
            TakeDamageServerRpc(damage, knockbackDir, knockbackForce, stunDuration);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TakeDamageServerRpc(int damage, Vector3 knockbackDir, float knockbackForce, float stunDuration)
    {
        ApplyDamageInternal(damage, knockbackDir, knockbackForce, stunDuration);
    }

    private void ApplyDamageInternal(int damage, Vector3 knockbackDir, float knockbackForce, float stunDuration)
    {
        if (!IsServer || !IsSpawned || damage <= 0 || isDead || currentHealth.Value <= 0)
        {
            return;
        }

        int nextHealth = Mathf.Max(0, currentHealth.Value - damage);
        currentHealth.Value = nextHealth;

        if (nextHealth <= 0)
        {
            DieServer();
            return;
        }

        if (enemyMovement != null)
        {
            enemyMovement.ApplyKnockback(knockbackDir, knockbackForce, stunDuration);
        }

        if (IsSpawned)
        {
            PlayHitFeedbackClientRpc(damage, knockbackDir, knockbackForce, stunDuration);
        }
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void PlayHitFeedbackClientRpc(int damage, Vector3 knockbackDir, float knockbackForce, float stunDuration)
    {
        if (Time.time - lastLocalFeedbackTime < 0.08f)
        {
            return;
        }

        PlayHitFeedbackLocal();
    }

    public void PlayPredictedHitFeedback(int damage, Vector3 hitPoint)
    {
        lastLocalFeedbackTime = Time.time;
        PlayHitFeedbackLocal();
        OnHitReceived?.Invoke(damage, hitPoint);
    }

    private void PlayHitFeedbackLocal()
    {
        hitFlashTimer = hitFlashDuration;
        SetFlashColor();
    }

    private void SetFlashColor()
    {
        if (cachedRenderers == null || sharedFlashBlock == null) return;

        for (int i = 0; i < cachedRenderers.Length; i++)
        {
            Renderer r = cachedRenderers[i];
            if (r != null)
            {
                r.SetPropertyBlock(sharedFlashBlock);
            }
        }
    }

    private void ClearFlashColor()
    {
        if (cachedRenderers == null || sharedClearBlock == null) return;

        for (int i = 0; i < cachedRenderers.Length; i++)
        {
            Renderer r = cachedRenderers[i];
            if (r != null)
            {
                r.SetPropertyBlock(sharedClearBlock);
            }
        }
    }

    private void HandleHealthChanged(int previousValue, int newValue)
    {
        OnHealthChanged?.Invoke(newValue, maxHealth);

        if (previousValue > 0 && newValue <= 0)
        {
            HandleDeathLocal();
        }
    }

    private void HandleDeathLocal()
    {
        if (isDead) return;
        isDead = true;
        ClearFlashColor();
        OnEnemyDied?.Invoke(this);

        // Desactivar colisiones para no obstaculizar al jugador ni a otros enemigos
        if (cachedColliders != null)
        {
            for (int i = 0; i < cachedColliders.Length; i++)
            {
                if (cachedColliders[i] != null) cachedColliders[i].enabled = false;
            }
        }

        // Ocultar renderers de inmediato para que desaparezca a la vista en 0ms
        if (cachedRenderers != null)
        {
            for (int i = 0; i < cachedRenderers.Length; i++)
            {
                if (cachedRenderers[i] != null) cachedRenderers[i].enabled = false;
            }
        }
    }

    private void DieServer()
    {
        HandleDeathLocal();
        DespawnSelfServer();
    }

    private void DespawnSelfServer()
    {
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
