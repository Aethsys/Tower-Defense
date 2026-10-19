using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class AreaDamageProjectile : NetworkBehaviour
{
    [Header("Explosion")]
    [SerializeField, Min(0.1f)] private float explosionLifetime = 0.5f;
    [SerializeField] private LayerMask enemyLayers = ~0;

    private Transform target;
    private int damage;
    private float explosionRadius;
    private float speed;
    private bool initialized;
    private bool exploded;

    private readonly Collider[] hitColliders = new Collider[128];
    private readonly HashSet<EnemyHealth> damagedEnemies =
        new HashSet<EnemyHealth>();

    public void Initialize(
        Transform targetTransform,
        int damageAmount,
        float radius,
        float projectileSpeed)
    {
        if (!IsServer)
            return;

        target = targetTransform;
        damage = damageAmount;
        explosionRadius = radius;
        speed = projectileSpeed;
        initialized = true;
    }

    private void Update()
    {
        if (!IsServer || !IsSpawned || !initialized || exploded)
            return;

        if (target == null)
        {
            Explode();
            return;
        }

        Vector3 destination = target.position;
        transform.position = Vector3.MoveTowards(
            transform.position,
            destination,
            speed * Time.deltaTime);

        Vector3 direction = destination - transform.position;

        if (direction.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.LookRotation(direction);

        if (Vector3.Distance(transform.position, destination) <= 0.15f)
            Explode();
    }

    private void Explode()
    {
        if (!IsServer || exploded)
            return;

        exploded = true;

        int count = Physics.OverlapSphereNonAlloc(
            transform.position,
            explosionRadius,
            hitColliders,
            enemyLayers,
            QueryTriggerInteraction.Collide);

        damagedEnemies.Clear();
        Vector3 explosionCenter = transform.position;

        for (int i = 0; i < count; i++)
        {
            Collider col = hitColliders[i];

            if (col == null ||
                !EnemyHealth.TryGetEnemy(col, out EnemyHealth enemy) ||
                enemy == null ||
                enemy.IsDead ||
                !damagedEnemies.Add(enemy))
            {
                continue;
            }

            // Empuje radial estrictamente en el plano horizontal (XZ).
            // Evita componentes hacia abajo que empujen al enemigo contra el suelo.
            Vector3 diff = enemy.transform.position - explosionCenter;
            diff.y = 0f;
            Vector3 knockDir = diff.sqrMagnitude > 0.0001f ? diff.normalized : Vector3.forward;
            enemy.TakeDamage(damage, knockDir, 3.5f, 0.2f);
        }

        // Ocultar proyectil y desactivar colisiones inmediatamente al detonar
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null) renderers[i].enabled = false;
        }

        Collider[] cols = GetComponentsInChildren<Collider>();
        for (int i = 0; i < cols.Length; i++)
        {
            if (cols[i] != null) cols[i].enabled = false;
        }

        StartCoroutine(DespawnAfterDelay());
    }

    private System.Collections.IEnumerator DespawnAfterDelay()
    {
        yield return new WaitForSeconds(explosionLifetime);

        if (IsServer && NetworkObject != null && NetworkObject.IsSpawned)
            NetworkObject.Despawn(true);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, explosionRadius);
    }
}