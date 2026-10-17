using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class EnemyMovement : NetworkBehaviour
{
    [Header("Movimiento")]
    [SerializeField] private float moveSpeed = 3.5f;
    [SerializeField] private float rotationSpeed = 10f;
    [SerializeField] private float networkLerpSpeed = 15f;

    [Header("Ataque a la Torre")]
    [SerializeField] private int attackDamage = 10;
    [SerializeField] private float attackRange = 1.25f;
    [SerializeField] private float firstAttackDelay = 0.5f;
    [SerializeField] private float attackInterval = 1.5f;
    [SerializeField] private float attackIntervalRandomVariation = 0.2f;
    [SerializeField] private string defaultTargetName = "Torch";

    private readonly NetworkVariable<Vector3> netPosition = new NetworkVariable<Vector3>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<Quaternion> netRotation = new NetworkVariable<Quaternion>(
        Quaternion.identity,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private TowerHealth targetTower;
    private Transform targetTransform;
    private float attackTimer;
    private bool isInAttackRange;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        attackTimer = firstAttackDelay;

        if (IsServer)
        {
            netPosition.Value = transform.position;
            netRotation.Value = transform.rotation;
            FindTargetTower();
        }
        else
        {
            transform.SetPositionAndRotation(netPosition.Value, netRotation.Value);
        }
    }

    private void Start()
    {
        if (!IsSpawned)
        {
            attackTimer = firstAttackDelay;
            FindTargetTower();
        }
    }

    private void Update()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            if (!IsSpawned)
            {
                return;
            }

            if (!IsServer)
            {
                SyncClientTransform();
                return;
            }
        }

        ServerUpdateMovementAndAttack();

        if (IsSpawned && IsServer)
        {
            netPosition.Value = transform.position;
            netRotation.Value = transform.rotation;
        }
    }

    private void ServerUpdateMovementAndAttack()
    {
        if (targetTransform == null || (targetTower != null && targetTower.IsDestroyed))
        {
            FindTargetTower();
            if (targetTransform == null)
            {
                return;
            }
        }

        Vector3 currentPos = transform.position;
        Vector3 targetPos = new Vector3(targetTransform.position.x, currentPos.y, targetTransform.position.z);
        Vector3 toTarget = targetPos - currentPos;
        float distanceToTarget = toTarget.magnitude;

        if (toTarget.sqrMagnitude > 0.0001f)
        {
            Quaternion desiredRotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, rotationSpeed * Time.deltaTime);
        }

        if (distanceToTarget > attackRange)
        {
            isInAttackRange = false;
            attackTimer = firstAttackDelay;
            transform.position = Vector3.MoveTowards(currentPos, targetPos, moveSpeed * Time.deltaTime);
        }
        else
        {
            if (!isInAttackRange)
            {
                isInAttackRange = true;
                attackTimer = firstAttackDelay;
            }

            attackTimer -= Time.deltaTime;
            if (attackTimer <= 0f)
            {
                AttackTower();
                float variation = attackIntervalRandomVariation > 0f
                    ? Random.Range(-attackIntervalRandomVariation, attackIntervalRandomVariation)
                    : 0f;
                attackTimer = Mathf.Max(0.1f, attackInterval + variation);
            }
        }
    }

    private void AttackTower()
    {
        if (targetTower == null && targetTransform != null)
        {
            targetTower = targetTransform.GetComponent<TowerHealth>();
        }

        if (targetTower != null && !targetTower.IsDestroyed)
        {
            targetTower.TakeDamage(attackDamage);
        }
    }

    private void FindTargetTower()
    {
        TowerHealth[] towers = FindObjectsByType<TowerHealth>();
        TowerHealth closestTower = null;
        float closestSqrDistance = float.MaxValue;

        foreach (TowerHealth tower in towers)
        {
            if (tower == null || tower.IsDestroyed)
            {
                continue;
            }

            float sqrDist = (tower.transform.position - transform.position).sqrMagnitude;
            if (sqrDist < closestSqrDistance)
            {
                closestSqrDistance = sqrDist;
                closestTower = tower;
            }
        }

        if (closestTower != null)
        {
            targetTower = closestTower;
            targetTransform = closestTower.transform;
            return;
        }

        if (!string.IsNullOrEmpty(defaultTargetName))
        {
            GameObject fallbackObj = GameObject.Find(defaultTargetName);
            if (fallbackObj != null)
            {
                targetTransform = fallbackObj.transform;
                targetTower = fallbackObj.GetComponent<TowerHealth>();
            }
        }
    }

    private void SyncClientTransform()
    {
        transform.position = Vector3.Lerp(transform.position, netPosition.Value, Time.deltaTime * networkLerpSpeed);
        transform.rotation = Quaternion.Slerp(transform.rotation, netRotation.Value, Time.deltaTime * networkLerpSpeed);
    }
}
