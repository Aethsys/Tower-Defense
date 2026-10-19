
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
    private TowerHealth towerHealth;
    private readonly Collider[] detectedColliders = new Collider[64];

    private void Awake()
    {
        useGUILayout = false;
        towerHealth = GetComponent<TowerHealth>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (towerHealth == null)
        {
            towerHealth = GetComponent<TowerHealth>();
        }

        attackTimer = attackInterval;
    }

    private void Update()
    {
        if (!IsServer || !IsSpawned)
            return;

        if (towerHealth == null || towerHealth.IsDestroyed)
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

        Vector3 spawnPos = firePoint != null ? firePoint.position : transform.position + Vector3.up * 1.5f;
        Quaternion spawnRot = firePoint != null ? firePoint.rotation : transform.rotation;

        NetworkObject projectileObject = Instantiate(
            projectilePrefab,
            spawnPos,
            spawnRot);

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
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, detectionRadius);
    }
}
