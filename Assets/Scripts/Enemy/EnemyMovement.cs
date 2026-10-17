using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class EnemyMovement : NetworkBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float moveSpeed = 3.5f;
    [SerializeField] private float rotationSpeed = 10f;
    [SerializeField] private float networkLerpSpeed = 15f;

    [Header("Tower Attack Settings")]
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
    private float targetSearchTimer;
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
            SyncServerNetworkState();
        }
    }

    private void SyncServerNetworkState()
    {
        Vector3 currentPos = transform.position;
        if ((netPosition.Value - currentPos).sqrMagnitude > 0.0001f)
        {
            netPosition.Value = currentPos;
        }

        Quaternion currentRot = transform.rotation;
        if (Quaternion.Angle(netRotation.Value, currentRot) > 0.1f)
        {
            netRotation.Value = currentRot;
        }
    }

    private void ServerUpdateMovementAndAttack()
    {
        if (targetTransform == null || (targetTower != null && targetTower.IsDestroyed))
        {
            targetSearchTimer -= Time.deltaTime;
            if (targetSearchTimer > 0f)
            {
                return;
            }

            targetSearchTimer = 0.5f;
            FindTargetTower();
            if (targetTransform == null)
            {
                return;
            }
        }

        Vector3 currentPos = transform.position;
        Vector3 targetPos = new Vector3(targetTransform.position.x, currentPos.y, targetTransform.position.z);
        Vector3 toTarget = targetPos - currentPos;
        float sqrDistanceToTarget = toTarget.sqrMagnitude;
        float sqrAttackRange = attackRange * attackRange;

        if (sqrDistanceToTarget > 0.0001f)
        {
            Quaternion desiredRotation = Quaternion.LookRotation(toTarget, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, rotationSpeed * Time.deltaTime);
        }

        if (sqrDistanceToTarget > sqrAttackRange)
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
        Vector3 myPos = transform.position;

        for (int i = 0; i < towers.Length; i++)
        {
            TowerHealth tower = towers[i];
            if (tower == null || tower.IsDestroyed)
            {
                continue;
            }

            float sqrDist = (tower.transform.position - myPos).sqrMagnitude;
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

        targetTower = null;
        targetTransform = null;

        if (!string.IsNullOrEmpty(defaultTargetName))
        {
            GameObject fallbackObj = GameObject.Find(defaultTargetName);
            if (fallbackObj != null)
            {
                TowerHealth fallbackTower = fallbackObj.GetComponent<TowerHealth>();
                if (fallbackTower == null || !fallbackTower.IsDestroyed)
                {
                    targetTransform = fallbackObj.transform;
                    targetTower = fallbackTower;
                }
            }
        }
    }

    private void SyncClientTransform()
    {
        transform.position = Vector3.Lerp(transform.position, netPosition.Value, Time.deltaTime * networkLerpSpeed);
        transform.rotation = Quaternion.Slerp(transform.rotation, netRotation.Value, Time.deltaTime * networkLerpSpeed);
    }
}
