
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(TowerHealth))]
public class AreaDamageTower : NetworkBehaviour
{
    [Header("Target Detection")]
    [SerializeField, Min(0.1f)] private float detectionRadius = 10f;
    [SerializeField] private LayerMask enemyLayers = ~0;

    [Header("Attack Settings")]
    [SerializeField, Min(0.1f)] private float attackInterval = 2f;
    [SerializeField, Min(1)] private int areaDamage = 15;
    [SerializeField, Min(0.1f)] private float explosionRadius = 3f;
    [SerializeField, Min(0.1f)] private float projectileSpeed = 12f;

    [Header("Projectile")]
    [SerializeField] private Transform firePoint;
    [SerializeField] private NetworkObject projectilePrefab;

    private float attackTimer;

    private readonly Collider[] detectedColliders = new Collider[64];
    private readonly HashSet<EnemyHealth> uniqueEnemies =
        new HashSet<EnemyHealth>();

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        attackTimer = attackInterval;

        if (IsServer)
        {
            UnityEngine.Debug.Log(
                $"[AreaDamageTower] {name} activa en el servidor.", this);
        }
    }

    private void Update()
    {
        if (!IsServer || !IsSpawned)
            return;

        TowerHealth health = GetComponent<TowerHealth>();

        if (health == null || health.IsDestroyed)
            return;

        attackTimer -= Time.deltaTime;

        if (attackTimer > 0f)
            return;

        attackTimer = attackInterval;

        EnemyHealth target = FindClosestEnemy();

        if (target == null)
            return;

        if (projectilePrefab == null)
        {
            UnityEngine.Debug.LogWarning(
                $"[AreaDamageTower] {name}: falta asignar Projectile Prefab.",
                this);
            return;
        }

        if (firePoint == null)
        {
            UnityEngine.Debug.LogWarning(
                $"[AreaDamageTower] {name}: falta asignar Fire Point.",
                this);
            return;
        }

        FireProjectile(target);
    }

    private EnemyHealth FindClosestEnemy()
    {
        int count = Physics.OverlapSphereNonAlloc(
            transform.position,
            detectionRadius,
            detectedColliders,
            enemyLayers,
            QueryTriggerInteraction.Collide);

        EnemyHealth closestEnemy = null;
        float closestDistance = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            Collider col = detectedColliders[i];

            if (col == null ||
                !EnemyHealth.TryGetEnemy(col, out EnemyHealth enemy) ||
                enemy == null ||
                enemy.IsDead)
            {
                continue;
            }

            float distance =
                (enemy.transform.position - transform.position).sqrMagnitude;

            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestEnemy = enemy;
            }
        }

        return closestEnemy;
    }

    private void FireProjectile(EnemyHealth target)
    {
        if (target == null || !target.IsSpawned)
            return;

        NetworkObject projectileObject = Instantiate(
            projectilePrefab,
            firePoint.position,
            firePoint.rotation);

        AreaDamageProjectile projectile =
            projectileObject.GetComponent<AreaDamageProjectile>();

        if (projectile == null)
        {
            UnityEngine.Debug.LogError(
                "[AreaDamageTower] El prefab necesita AreaDamageProjectile.",
                projectileObject);

            Destroy(projectileObject.gameObject);
            return;
        }

        projectileObject.Spawn(true);

        projectile.Initialize(
            target.transform,
            areaDamage,
            explosionRadius,
            projectileSpeed);

        UnityEngine.Debug.Log(
            $"[AreaDamageTower] {name} dispara contra {target.name}.", this);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, detectionRadius);
    }
}
