using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class EnemyMovement : NetworkBehaviour
{
    private static readonly int AnimHashSpeed = Animator.StringToHash("Speed");
    private static readonly int AnimHashAttack = Animator.StringToHash("Attack");

    [Header("Animation")]
    [SerializeField] private Animator enemyAnimator;
    [SerializeField] private float animSpeedDampTime = 0.1f;

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

    [Header("Enemy Route")]
    [SerializeField] private EnemyPath enemyPath;
    [SerializeField] private float waypointReachDistance = 0.25f;

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

    private readonly NetworkVariable<float> netAnimSpeed = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private Transform selfTransform;
    private TowerHealth targetTower;
    private Transform targetTransform;
    private BaitTower targetBait;
    private Watchtower targetWatchtower;

    private float speedMultiplier = 1f;
    private float attackTimer;
    private float targetSearchTimer;
    private bool isInAttackRange;
    private int currentWaypointIndex;
    private bool hasReachedPathEnd;

    private EnemyHealth enemyHealth;

    // Knockback
    private Vector3 knockbackVelocity;
    private float stunTimer;

    // Animator cache
    private RuntimeAnimatorController cachedController;
    private bool animHasSpeed;
    private bool animHasAttack;

    public void SetPath(EnemyPath path)
    {
        enemyPath = path;
        currentWaypointIndex = 0;
        hasReachedPathEnd = false;
        isInAttackRange = false;
    }

    public void SetSpeedMultiplier(float multiplier)
    {
        speedMultiplier = Mathf.Max(0.1f, multiplier);
    }

    private void Awake()
    {
        useGUILayout = false;
        selfTransform = transform;
        enemyHealth = GetComponent<EnemyHealth>();

        if (enemyAnimator == null)
        {
            enemyAnimator = GetComponentInChildren<Animator>(true);
        }

        if (enemyAnimator != null)
        {
            enemyAnimator.applyRootMotion = false;
        }

        // Asegurar que el SkinnedMeshRenderer no desaparezca por frustum culling o bounds en cero
        SkinnedMeshRenderer[] smrs = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < smrs.Length; i++)
        {
            if (smrs[i] == null) continue;
            smrs[i].updateWhenOffscreen = true;
            if (smrs[i].localBounds.size.sqrMagnitude < 0.001f)
            {
                smrs[i].localBounds = new Bounds(Vector3.up * 0.5f, Vector3.one * 2f);
            }
        }
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

    public void ApplyKnockback(Vector3 direction, float force, float stunDuration = 0.2f)
    {
        if (enemyHealth != null && enemyHealth.IsDead) return;

        // Forzar estrictamente en el plano XZ para NUNCA empujar al enemigo contra el suelo
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.0001f)
        {
            knockbackVelocity = direction.normalized * force;
            stunTimer = Mathf.Max(stunTimer, stunDuration);
        }
    }

    private void Update()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            if (!IsSpawned) return;

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

    private void ServerUpdateMovementAndAttack()
    {
        if (enemyHealth != null && enemyHealth.IsDead) return;

        float dt = Time.deltaTime;

        // Decaimiento del knockback
        if (knockbackVelocity.sqrMagnitude > 0.001f)
        {
            selfTransform.position += knockbackVelocity * dt;
            knockbackVelocity = Vector3.MoveTowards(knockbackVelocity, Vector3.zero, 15f * dt);
        }

        if (stunTimer > 0f)
        {
            stunTimer -= dt;
            UpdateAnimatorLocomotion(0f, dt);
            if (IsSpawned && IsServer) netAnimSpeed.Value = 0f;
            return;
        }

        // Liberar la torre señuelo si deja de estar activa
        if (targetBait != null && !targetBait.IsActive)
        {
            targetBait = null;
            targetTower = null;
            targetTransform = null;
            isInAttackRange = false;
            attackTimer = firstAttackDelay;
            targetSearchTimer = 0f;
        }

        // Liberar la torre de vigilancia si es destruida
        if (targetWatchtower != null && !targetWatchtower.IsActive)
        {
            targetWatchtower = null;
            targetTower = null;
            targetTransform = null;
            isInAttackRange = false;
            attackTimer = firstAttackDelay;
            targetSearchTimer = 0f;
        }

        // Buscar señuelos prioritarios si no hay uno activo
        if (targetBait == null && targetWatchtower == null)
        {
            targetBait = FindAvailableBait();
            if (targetBait == null)
            {
                targetWatchtower = FindAvailableWatchtower();
            }
        }

        // Perseguir torre señuelo
        if (targetBait != null)
        {
            targetTower = targetBait.Health;
            targetTransform = targetBait.transform;

            if (targetTower == null || targetTower.IsDestroyed)
            {
                targetBait = null;
                targetTower = null;
                targetTransform = null;
                isInAttackRange = false;
                return;
            }

            MoveTowardsCurrentTarget();
            return;
        }

        // Perseguir torre de vigilancia
        if (targetWatchtower != null)
        {
            targetTower = targetWatchtower.Health;
            targetTransform = targetWatchtower.transform;

            if (targetTower == null || targetTower.IsDestroyed)
            {
                targetWatchtower = null;
                targetTower = null;
                targetTransform = null;
                isInAttackRange = false;
                return;
            }

            MoveTowardsCurrentTarget();
            return;
        }

        // Si no hay señuelos, seguir los waypoints de la ruta
        if (!hasReachedPathEnd && enemyPath != null && enemyPath.WaypointCount > 0)
        {
            targetTower = null;
            targetTransform = null;
            isInAttackRange = false;
            attackTimer = firstAttackDelay;

            FollowPath();
            return;
        }

        // Al terminar la ruta, buscar el Core / torre principal
        if (targetTransform == null || targetTower == null || targetTower.IsDestroyed)
        {
            targetSearchTimer -= dt;
            if (targetSearchTimer > 0f) return;

            targetSearchTimer = 0.5f;
            FindTargetTower();

            if (targetTransform == null || targetTower == null)
            {
                UpdateAnimatorLocomotion(0f, dt);
                if (IsSpawned && IsServer) netAnimSpeed.Value = 0f;
                return;
            }
        }

        MoveTowardsCurrentTarget();
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
            {
                hasReachedPathEnd = true;
            }
            return;
        }

        float dt = Time.deltaTime;
        Vector3 currentPos = selfTransform.position;
        Vector3 waypointPos = waypoint.position;

        // Seguir las coordenadas X y Z del waypoint, y ajustar Y a la altura del waypoint
        Vector3 targetPos = waypointPos;
        Vector3 toTarget = targetPos - currentPos;

        // Rotación solo en el plano horizontal XZ
        Vector3 horizontalDir = new Vector3(toTarget.x, 0f, toTarget.z);
        if (horizontalDir.sqrMagnitude > 0.0001f)
        {
            Quaternion desiredRotation = Quaternion.LookRotation(horizontalDir.normalized, Vector3.up);
            selfTransform.rotation = Quaternion.Slerp(selfTransform.rotation, desiredRotation, rotationSpeed * dt);
        }

        // Comprobar si se alcanzó el waypoint (en el plano XZ)
        if (horizontalDir.magnitude <= waypointReachDistance)
        {
            currentWaypointIndex++;
            if (currentWaypointIndex >= enemyPath.WaypointCount)
            {
                hasReachedPathEnd = true;
            }
            return;
        }

        float effectiveSpeed = moveSpeed * speedMultiplier;
        Vector3 nextPos = Vector3.MoveTowards(currentPos, targetPos, effectiveSpeed * dt);
        selfTransform.position = nextPos;

        UpdateAnimatorLocomotion(effectiveSpeed, dt);
        if (IsSpawned && IsServer) netAnimSpeed.Value = effectiveSpeed;
    }

    private void MoveTowardsCurrentTarget()
    {
        if (targetTransform == null || targetTower == null || targetTower.IsDestroyed)
        {
            isInAttackRange = false;
            return;
        }

        float dt = Time.deltaTime;
        Vector3 currentPos = selfTransform.position;
        Vector3 targetWorldPos = targetTransform.position;

        Vector3 toTargetHorizontal = new Vector3(targetWorldPos.x - currentPos.x, 0f, targetWorldPos.z - currentPos.z);
        float sqrDistanceHorizontal = toTargetHorizontal.sqrMagnitude;
        float sqrAttackRange = attackRange * attackRange;

        if (toTargetHorizontal.sqrMagnitude > 0.0001f)
        {
            Quaternion desiredRotation = Quaternion.LookRotation(toTargetHorizontal.normalized, Vector3.up);
            selfTransform.rotation = Quaternion.Slerp(selfTransform.rotation, desiredRotation, rotationSpeed * dt);
        }

        if (sqrDistanceHorizontal > sqrAttackRange)
        {
            isInAttackRange = false;
            attackTimer = firstAttackDelay;

            // Mantener Y consistente con el objetivo para no atravesar suelos en desniveles
            Vector3 targetPos = new Vector3(targetWorldPos.x, targetWorldPos.y, targetWorldPos.z);
            float effectiveSpeed = moveSpeed * speedMultiplier;
            Vector3 nextPos = Vector3.MoveTowards(currentPos, targetPos, effectiveSpeed * dt);
            selfTransform.position = nextPos;

            UpdateAnimatorLocomotion(effectiveSpeed, dt);
            if (IsSpawned && IsServer) netAnimSpeed.Value = effectiveSpeed;
        }
        else
        {
            UpdateAnimatorLocomotion(0f, dt);
            if (IsSpawned && IsServer) netAnimSpeed.Value = 0f;

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
            TriggerAttackAnimation();
        }
    }

    private BaitTower FindAvailableBait()
    {
        BaitTower closestBait = null;
        float closestSqrDist = float.MaxValue;
        List<BaitTower> baits = BaitTower.ActiveBaits;

        for (int i = 0; i < baits.Count; i++)
        {
            BaitTower bait = baits[i];
            if (bait == null || !bait.CanAttract(selfTransform.position)) continue;

            Vector3 offset = bait.transform.position - selfTransform.position;
            offset.y = 0f;
            float sqrDist = offset.sqrMagnitude;
            if (sqrDist < closestSqrDist)
            {
                closestSqrDist = sqrDist;
                closestBait = bait;
            }
        }

        return closestBait;
    }

    private Watchtower FindAvailableWatchtower()
    {
        Watchtower closestWatchtower = null;
        float closestSqrDist = float.MaxValue;
        List<Watchtower> watchtowers = Watchtower.ActiveWatchtowers;

        for (int i = 0; i < watchtowers.Count; i++)
        {
            Watchtower watchtower = watchtowers[i];
            if (watchtower == null || !watchtower.CanAttract(selfTransform.position)) continue;

            Vector3 offset = watchtower.transform.position - selfTransform.position;
            offset.y = 0f;
            float sqrDist = offset.sqrMagnitude;
            if (sqrDist < closestSqrDist)
            {
                closestSqrDist = sqrDist;
                closestWatchtower = watchtower;
            }
        }

        return closestWatchtower;
    }

    private void FindTargetTower()
    {
        TowerHealth mainTower = null;
        List<TowerHealth> activeTowers = TowerHealth.ActiveTowers;

        for (int i = 0; i < activeTowers.Count; i++)
        {
            TowerHealth tower = activeTowers[i];
            if (tower == null || tower.IsDestroyed || !tower.IsCoreTower) continue;

            mainTower = tower;
            break;
        }

        targetTower = mainTower;
        targetTransform = mainTower != null ? mainTower.transform : null;
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

    private void SyncClientTransform()
    {
        float t = Time.deltaTime * networkLerpSpeed;
        Vector3 nextPos = Vector3.Lerp(selfTransform.position, netPosition.Value, t);
        Quaternion nextRot = Quaternion.Slerp(selfTransform.rotation, netRotation.Value, t);
        selfTransform.SetPositionAndRotation(nextPos, nextRot);

        UpdateAnimatorLocomotion(netAnimSpeed.Value, Time.deltaTime);
    }

    // ------------------------------------------------------------------ Animador

    private bool EnsureAnimatorReady()
    {
        if (enemyAnimator == null || !enemyAnimator.isActiveAndEnabled)
        {
            return false;
        }

        RuntimeAnimatorController controller = enemyAnimator.runtimeAnimatorController;
        if (controller == null)
        {
            cachedController = null;
            return false;
        }

        if (controller != cachedController)
        {
            cachedController = controller;
            animHasSpeed = false;
            animHasAttack = false;

            AnimatorControllerParameter[] parameters = enemyAnimator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                int hash = parameters[i].nameHash;
                AnimatorControllerParameterType type = parameters[i].type;
                if (hash == AnimHashSpeed && type == AnimatorControllerParameterType.Float) animHasSpeed = true;
                else if (hash == AnimHashAttack && type == AnimatorControllerParameterType.Trigger) animHasAttack = true;
            }
        }

        return true;
    }

    private void UpdateAnimatorLocomotion(float targetSpeed, float dt)
    {
        if (!EnsureAnimatorReady() || !animHasSpeed) return;

        if (animSpeedDampTime > 0f && dt > 0f)
        {
            enemyAnimator.SetFloat(AnimHashSpeed, targetSpeed, animSpeedDampTime, dt);
            if (targetSpeed == 0f && enemyAnimator.GetFloat(AnimHashSpeed) < 0.01f)
            {
                enemyAnimator.SetFloat(AnimHashSpeed, 0f);
            }
        }
        else
        {
            enemyAnimator.SetFloat(AnimHashSpeed, targetSpeed);
        }
    }

    public void TriggerAttackAnimation()
    {
        if (IsServer && IsSpawned)
        {
            TriggerAttackAnimationClientRpc();
        }
        else
        {
            PlayAttackAnimationLocal();
        }
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void TriggerAttackAnimationClientRpc()
    {
        PlayAttackAnimationLocal();
    }

    private void PlayAttackAnimationLocal()
    {
        if (EnsureAnimatorReady() && animHasAttack)
        {
            enemyAnimator.SetTrigger(AnimHashAttack);
        }
    }
}
