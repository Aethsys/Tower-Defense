using System;
using System.Collections.Generic;
using UnityEngine;

public class MeleeWeapon : WeaponBase
{
    [Header("Weapon Data")]
    [SerializeField] private MeleeWeaponData weaponData;

    [Header("Debug")]
    [SerializeField] private bool logHits = false;
    [SerializeField] private bool drawDebugGizmos = true;

    private struct HitCandidate
    {
        public EnemyHealth Enemy;
        public Vector3 Point;
        public float Score;
    }

    private const int MaxOverlap = 32;
    private static readonly Collider[] OverlapBuffer = new Collider[MaxOverlap];
    private static readonly RaycastHit[] ObstructionBuffer = new RaycastHit[8];
    private static readonly HitCandidate[] CandidateBuffer = new HitCandidate[MaxOverlap];
    private static readonly HashSet<EnemyHealth> HitEnemiesSet = new HashSet<EnemyHealth>();

    private float nextAllowedAttackTime;
    private bool hasPendingAttack;
    private bool pendingIsSecondary;
    private float pendingHitTime;
    private MeleeAttackProfile pendingProfile;

#if UNITY_EDITOR
    private Vector3 debugOrigin;
    private Vector3 debugDirection;
    private float debugRange;
    private float debugHalfAngle;
    private float debugTime = -10f;
#endif

    public override string WeaponName => weaponData != null ? weaponData.weaponName : "Unarmed";
    public MeleeWeaponData WeaponData
    {
        get => weaponData;
        set => weaponData = value;
    }

    public bool IsAttacking => hasPendingAttack;

    public event Action<MeleeAttackProfile> OnPrimaryAttackPerformed;
    public event Action<MeleeAttackProfile> OnSecondaryAttackPerformed;
    public event Action<EnemyHealth, int> OnTargetDamaged;

    private void Awake()
    {
        useGUILayout = false;
    }

    private void OnDisable()
    {
        hasPendingAttack = false;
    }

    private void Update()
    {
        if (hasPendingAttack && Time.time >= pendingHitTime)
        {
            hasPendingAttack = false;
            ResolveMeleeHit(pendingProfile, pendingIsSecondary);
        }
    }

    public override bool TryUsePrimary(bool wasPressedThisFrame, bool isHeld)
    {
        return TryBeginAttack(isSecondary: false, wasPressedThisFrame, isHeld);
    }

    public override bool TryUseSecondary(bool wasPressedThisFrame, bool isHeld)
    {
        return TryBeginAttack(isSecondary: true, wasPressedThisFrame, isHeld);
    }

    private bool TryBeginAttack(bool isSecondary, bool wasPressedThisFrame, bool isHeld)
    {
        if (weaponData == null || hasPendingAttack)
        {
            return false;
        }

        MeleeAttackProfile profile = isSecondary ? weaponData.secondaryAttack : weaponData.primaryAttack;
        EnsureValidProfile(ref profile, isSecondary);

        bool shouldTrigger = profile.allowHoldToAttack ? (wasPressedThisFrame || isHeld) : wasPressedThisFrame;
        if (!shouldTrigger || Time.time < nextAllowedAttackTime)
        {
            return false;
        }

        nextAllowedAttackTime = Time.time + Mathf.Max(0.05f, profile.cooldown);

        if (isSecondary)
        {
            OnSecondaryAttackPerformed?.Invoke(profile);
        }
        else
        {
            OnPrimaryAttackPerformed?.Invoke(profile);
        }

        RaiseAttackStarted(isSecondary);

        if (profile.hitDelay <= 0.001f)
        {
            ResolveMeleeHit(profile, isSecondary);
        }
        else
        {
            hasPendingAttack = true;
            pendingIsSecondary = isSecondary;
            pendingProfile = profile;
            pendingHitTime = Time.time + profile.hitDelay;
        }

        return true;
    }

    private void ResolveMeleeHit(MeleeAttackProfile profile, bool isSecondary)
    {
        // El aim se lee en el momento del impacto: el jugador puede corregir durante el wind-up.
        Transform originTransform = AimOrigin != null ? AimOrigin : transform;
        Vector3 origin = originTransform.position;
        Vector3 direction = originTransform.forward;

        float radius = Mathf.Max(0.1f, profile.radius);
        float range = Mathf.Max(0.5f, profile.range);
        float halfAngle = Mathf.Clamp(profile.arcHalfAngle, 5f, 180f);
        int damage = Mathf.Max(1, profile.damage);
        int maxTargets = Mathf.Max(1, profile.maxTargets);

        LayerMask mask = weaponData.hitLayers.value != 0 ? weaponData.hitLayers : (LayerMask)(~0);

#if UNITY_EDITOR
        debugOrigin = origin;
        debugDirection = direction;
        debugRange = range;
        debugHalfAngle = halfAngle;
        debugTime = Time.time;
#endif

        // Volumen amplio que cubre todo el cono; luego se filtra por ángulo y distancia.
        float sphereRadius = range * 0.5f + radius;
        Vector3 sphereCenter = origin + direction * (range * 0.5f);
        int overlapCount = Physics.OverlapSphereNonAlloc(sphereCenter, sphereRadius, OverlapBuffer, mask, QueryTriggerInteraction.Collide);

        HitEnemiesSet.Clear();
        int candidateCount = 0;
        float cosHalfAngle = Mathf.Cos(halfAngle * Mathf.Deg2Rad);

        for (int i = 0; i < overlapCount; i++)
        {
            Collider col = OverlapBuffer[i];
            if (col == null || (OwnerRoot != null && col.transform.IsChildOf(OwnerRoot)))
            {
                continue;
            }

            if (!EnemyHealth.TryGetEnemy(col, out EnemyHealth enemy) || enemy.IsDead || !HitEnemiesSet.Add(enemy))
            {
                continue;
            }

            Vector3 closest = GetClosestPoint(col, origin);
            Vector3 toClosest = closest - origin;
            float distance = toClosest.magnitude;
            if (distance > range + radius)
            {
                continue;
            }

            Vector3 toCenter = col.bounds.center - origin;
            float centerDistance = toCenter.magnitude;
            float cos = centerDistance > 0.0001f ? Vector3.Dot(direction, toCenter / centerDistance) : 1f;

            // A quemarropa siempre conecta; si no, tiene que estar dentro del cono.
            bool pointBlank = distance <= radius;
            if (!pointBlank && cos < cosHalfAngle)
            {
                continue;
            }

            if (IsObstructed(origin, closest, distance, enemy))
            {
                continue;
            }

            // Prioriza lo que está centrado en pantalla y cerca.
            float score = distance + (1f - cos) * range * 1.5f;
            CandidateBuffer[candidateCount++] = new HitCandidate { Enemy = enemy, Point = closest, Score = score };
        }

        SortCandidates(candidateCount);

        int targetsHit = 0;
        Vector3 ownerPos = OwnerRoot != null ? OwnerRoot.position : origin;
        Vector3 flatForward = new Vector3(direction.x, 0f, direction.z);
        flatForward = flatForward.sqrMagnitude > 0.0001f ? flatForward.normalized : Vector3.forward;

        for (int i = 0; i < candidateCount && targetsHit < maxTargets; i++)
        {
            EnemyHealth enemy = CandidateBuffer[i].Enemy;
            if (enemy == null || enemy.IsDead)
            {
                continue;
            }

            Vector3 away = enemy.transform.position - ownerPos;
            away.y = 0f;
            Vector3 knockDir = (away.sqrMagnitude > 0.0001f ? away.normalized : flatForward) + flatForward;
            knockDir = knockDir.sqrMagnitude > 0.0001f ? knockDir.normalized : flatForward;

            enemy.TakeDamage(damage, knockDir, profile.knockbackForce, profile.stunDuration);
            enemy.PlayPredictedHitFeedback(damage, CandidateBuffer[i].Point);
            OnTargetDamaged?.Invoke(enemy, damage);
            targetsHit++;

            if (logHits)
            {
                Debug.Log($"[{WeaponName}] Hit {enemy.name} for {damage} damage.");
            }
        }

        RaiseAttackResolved(new WeaponAttackResult
        {
            TargetsHit = targetsHit,
            DamagePerTarget = damage,
            HitShake = profile.hitShake,
            IsSecondary = isSecondary
        });
    }

    private bool IsObstructed(Vector3 origin, Vector3 target, float distance, EnemyHealth enemy)
    {
        if (weaponData.obstructionLayers.value == 0 || distance <= 0.05f)
        {
            return false;
        }

        Vector3 dir = (target - origin) / distance;
        int count = Physics.RaycastNonAlloc(origin, dir, ObstructionBuffer, distance - 0.02f, weaponData.obstructionLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider hitCol = ObstructionBuffer[i].collider;
            if (hitCol == null)
            {
                continue;
            }

            if (OwnerRoot != null && hitCol.transform.IsChildOf(OwnerRoot))
            {
                continue;
            }

            if (hitCol.transform.IsChildOf(enemy.transform))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static Vector3 GetClosestPoint(Collider col, Vector3 point)
    {
        if (col is MeshCollider meshCol && !meshCol.convex)
        {
            return col.bounds.ClosestPoint(point);
        }

        return col.ClosestPoint(point);
    }

    private static void EnsureValidProfile(ref MeleeAttackProfile profile, bool isSecondary)
    {
        if (profile.damage <= 0)
        {
            profile.damage = isSecondary ? 20 : 10;
        }
        if (profile.range <= 0.1f)
        {
            profile.range = isSecondary ? 2.8f : 2.6f;
        }
        if (profile.radius <= 0.05f)
        {
            profile.radius = isSecondary ? 0.55f : 0.45f;
        }
        if (profile.arcHalfAngle <= 1f)
        {
            profile.arcHalfAngle = isSecondary ? 55f : 40f;
        }
        if (profile.cooldown <= 0.01f)
        {
            profile.cooldown = isSecondary ? 0.85f : 0.4f;
        }
        if (profile.maxTargets < 1)
        {
            profile.maxTargets = 1;
        }

        // El golpe nunca puede conectar después de que el arma ya esté lista de nuevo.
        profile.hitDelay = Mathf.Clamp(profile.hitDelay, 0f, profile.cooldown * 0.9f);
    }

    private static void SortCandidates(int count)
    {
        for (int i = 1; i < count; i++)
        {
            HitCandidate current = CandidateBuffer[i];
            int j = i - 1;
            while (j >= 0 && CandidateBuffer[j].Score > current.Score)
            {
                CandidateBuffer[j + 1] = CandidateBuffer[j];
                j--;
            }
            CandidateBuffer[j + 1] = current;
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!drawDebugGizmos || Time.time - debugTime > 0.35f)
        {
            return;
        }

        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.9f);
        Vector3 up = Vector3.up;
        Vector3 right = Vector3.Cross(up, debugDirection);
        if (right.sqrMagnitude < 0.0001f)
        {
            right = Vector3.right;
        }
        right.Normalize();

        Vector3 left = Quaternion.AngleAxis(-debugHalfAngle, up) * debugDirection;
        Vector3 rightDir = Quaternion.AngleAxis(debugHalfAngle, up) * debugDirection;
        Gizmos.DrawLine(debugOrigin, debugOrigin + debugDirection * debugRange);
        Gizmos.DrawLine(debugOrigin, debugOrigin + left * debugRange);
        Gizmos.DrawLine(debugOrigin, debugOrigin + rightDir * debugRange);
    }
#endif
}
