using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class EnemyMovement : NetworkBehaviour
{

    public void SetPath(EnemyPath path)
    {
        enemyPath = path;
        currentWaypointIndex = 0;
        hasReachedPathEnd = false;
        isInAttackRange = false;
    }

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

    private Transform selfTransform;
    private TowerHealth targetTower;
    private Transform targetTransform;
    private float attackTimer;
    private float targetSearchTimer;
    private bool isInAttackRange;

    [Header("Enemy Route")]
    [SerializeField] private EnemyPath enemyPath;
    [SerializeField] private float waypointReachDistance = 0.15f;

    private int currentWaypointIndex;
    private bool hasReachedPathEnd;

    private void Awake()
    {
        useGUILayout = false;
        selfTransform = transform;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        attackTimer = firstAttackDelay;

        if (IsServer)
        {
            netPosition.Value = selfTransform.position;
            netRotation.Value = selfTransform.rotation;
            FindTargetTower();
        }
        else
        {
            selfTransform.SetPositionAndRotation(netPosition.Value, netRotation.Value);
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
        Vector3 currentPos = selfTransform.position;
        if ((netPosition.Value - currentPos).sqrMagnitude > 0.0001f)
        {
            netPosition.Value = currentPos;
        }

        Quaternion currentRot = selfTransform.rotation;
        if (Quaternion.Angle(netRotation.Value, currentRot) > 0.1f)
        {
            netRotation.Value = currentRot;
        }
    }

    /*private void ServerUpdateMovementAndAttack()
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

        float dt = Time.deltaTime;
        Vector3 currentPos = selfTransform.position;
        Vector3 targetWorldPos = targetTransform.position;
        Vector3 targetPos = new Vector3(targetWorldPos.x, currentPos.y, targetWorldPos.z);
        Vector3 toTarget = targetPos - currentPos;
        float sqrDistanceToTarget = toTarget.sqrMagnitude;
        float sqrAttackRange = attackRange * attackRange;

        Quaternion nextRotation = selfTransform.rotation;
        if (sqrDistanceToTarget > 0.0001f)
        {
            Quaternion desiredRotation = Quaternion.LookRotation(toTarget, Vector3.up);
            nextRotation = Quaternion.Slerp(nextRotation, desiredRotation, rotationSpeed * dt);
        }

        if (sqrDistanceToTarget > sqrAttackRange)
        {
            isInAttackRange = false;
            attackTimer = firstAttackDelay;
            Vector3 nextPos = Vector3.MoveTowards(currentPos, targetPos, moveSpeed * dt);
            selfTransform.SetPositionAndRotation(nextPos, nextRotation);
        }
        else
        {
            selfTransform.rotation = nextRotation;

            if (!isInAttackRange)
            {
                isInAttackRange = true;
                attackTimer = firstAttackDelay;
            }

            attackTimer -= dt;
            if (attackTimer <= 0f)
            {
                AttackTower();
                float variation = attackIntervalRandomVariation > 0f
                    ? UnityEngine.Random.Range(-attackIntervalRandomVariation, attackIntervalRandomVariation)
                    : 0f;
                attackTimer = Mathf.Max(0.1f, attackInterval + variation);
            }
        }
    }*/


    private void ServerUpdateMovementAndAttack()
    {
        if (!hasReachedPathEnd && enemyPath != null)
        {
            FollowPath();
            return;
        }

        if (targetTransform == null ||
            (targetTower != null && targetTower.IsDestroyed))
        {
            targetSearchTimer -= Time.deltaTime;

            if (targetSearchTimer > 0f)
                return;

            targetSearchTimer = 0.5f;
            FindTargetTower();

            if (targetTransform == null)
                return;
        }

        float dt = Time.deltaTime;
        Vector3 currentPos = selfTransform.position;
        Vector3 targetWorldPos = targetTransform.position;

        Vector3 targetPos = new Vector3(
            targetWorldPos.x,
            currentPos.y,
            targetWorldPos.z
        );

        Vector3 toTarget = targetPos - currentPos;
        float sqrDistance = toTarget.sqrMagnitude;
        float sqrAttackRange = attackRange * attackRange;

        Quaternion nextRotation = selfTransform.rotation;

        if (sqrDistance > 0.0001f)
        {
            Quaternion desiredRotation =
                Quaternion.LookRotation(toTarget, Vector3.up);

            nextRotation = Quaternion.Slerp(
                nextRotation,
                desiredRotation,
                rotationSpeed * dt
            );
        }

        if (sqrDistance > sqrAttackRange)
        {
            isInAttackRange = false;
            attackTimer = firstAttackDelay;

            Vector3 nextPos = Vector3.MoveTowards(
                currentPos,
                targetPos,
                moveSpeed * dt
            );

            selfTransform.SetPositionAndRotation(nextPos, nextRotation);
        }
        else
        {
            selfTransform.rotation = nextRotation;

            if (!isInAttackRange)
            {
                isInAttackRange = true;
                attackTimer = firstAttackDelay;
            }

            attackTimer -= dt;

            if (attackTimer <= 0f)
            {
                AttackTower();

                float variation = attackIntervalRandomVariation > 0f
                    ? UnityEngine.Random.Range(
                        -attackIntervalRandomVariation,
                        attackIntervalRandomVariation
                    )
                    : 0f;

                attackTimer = Mathf.Max(
                    0.1f,
                    attackInterval + variation
                );
            }
        }
    }

    private void FollowPath()
    {
        if (enemyPath == null || enemyPath.WaypointCount == 0)
        {
            hasReachedPathEnd = true;
            return;
        }

        Transform waypoint = enemyPath.GetWaypoint(currentWaypointIndex);

        if (waypoint == null)
        {
            currentWaypointIndex++;

            if (currentWaypointIndex >= enemyPath.WaypointCount)
                hasReachedPathEnd = true;

            return;
        }

        Vector3 currentPos = selfTransform.position;
        Vector3 targetPos = new Vector3(
            waypoint.position.x,
            currentPos.y,
            waypoint.position.z
        );

        Vector3 direction = targetPos - currentPos;

        Quaternion nextRotation = selfTransform.rotation;

        if (direction.sqrMagnitude > 0.0001f)
        {
            Quaternion desiredRotation =
                Quaternion.LookRotation(direction, Vector3.up);

            nextRotation = Quaternion.Slerp(
                selfTransform.rotation,
                desiredRotation,
                rotationSpeed * Time.deltaTime
            );
        }

        selfTransform.rotation = nextRotation;

        if (direction.magnitude <= waypointReachDistance)
        {
            currentWaypointIndex++;

            if (currentWaypointIndex >= enemyPath.WaypointCount)
                hasReachedPathEnd = true;

            return;
        }

        Vector3 nextPosition = Vector3.MoveTowards(
            currentPos,
            targetPos,
            moveSpeed * Time.deltaTime
        );

        selfTransform.position = nextPosition;
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

    /*private void FindTargetTower()
    {
        TowerHealth closestTower = null;
        float closestSqrDistance = float.MaxValue;
        Vector3 myPos = selfTransform.position;

        List<TowerHealth> activeTowers = TowerHealth.ActiveTowers;
        for (int i = 0; i < activeTowers.Count; i++)
        {
            TowerHealth tower = activeTowers[i];
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

        if (closestTower == null)
        {
            TowerHealth[] towers = FindObjectsByType<TowerHealth>();
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
    }*/


    private void FindTargetTower()
    {
        TowerHealth mainTower = null;

        List<TowerHealth> activeTowers = TowerHealth.ActiveTowers;

        for (int i = 0; i < activeTowers.Count; i++)
        {
            TowerHealth tower = activeTowers[i];

            if (tower == null ||
                tower.IsDestroyed ||
                !tower.IsCoreTower)
            {
                continue;
            }

            mainTower = tower;
            break;
        }

        targetTower = mainTower;
        targetTransform = mainTower != null
            ? mainTower.transform
            : null;
    }


    private void SyncClientTransform()
    {
        float t = Time.deltaTime * networkLerpSpeed;
        Vector3 nextPos = Vector3.Lerp(selfTransform.position, netPosition.Value, t);
        Quaternion nextRot = Quaternion.Slerp(selfTransform.rotation, netRotation.Value, t);
        selfTransform.SetPositionAndRotation(nextPos, nextRot);
    }
}
