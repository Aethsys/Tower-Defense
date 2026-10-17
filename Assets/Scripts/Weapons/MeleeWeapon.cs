using System;
using System.Collections.Generic;
using UnityEngine;

public class MeleeWeapon : WeaponBase
{
    [Header("Weapon Data")]
    [SerializeField] private MeleeWeaponData weaponData;

    private static readonly Collider[] OverlapBuffer = new Collider[16];
    private static readonly RaycastHit[] HitBuffer = new RaycastHit[16];
    private static readonly HashSet<EnemyHealth> HitEnemiesSet = new HashSet<EnemyHealth>();

    private float nextAllowedAttackTime;

    public override string WeaponName => weaponData != null ? weaponData.weaponName : "Unarmed";
    public MeleeWeaponData WeaponData
    {
        get => weaponData;
        set => weaponData = value;
    }

    public event Action<MeleeAttackProfile> OnPrimaryAttackPerformed;
    public event Action<MeleeAttackProfile> OnSecondaryAttackPerformed;
    public event Action<EnemyHealth, int> OnTargetDamaged;

    public override bool TryUsePrimary(bool wasPressedThisFrame, bool isHeld)
    {
        if (weaponData == null)
        {
            return false;
        }

        MeleeAttackProfile profile = weaponData.primaryAttack;
        EnsureValidProfile(ref profile, isSecondary: false);

        bool shouldTrigger = profile.allowHoldToAttack ? (wasPressedThisFrame || isHeld) : wasPressedThisFrame;
        if (!shouldTrigger || Time.time < nextAllowedAttackTime)
        {
            return false;
        }

        ExecuteMeleeAttack(profile);
        OnPrimaryAttackPerformed?.Invoke(profile);
        return true;
    }

    public override bool TryUseSecondary(bool wasPressedThisFrame, bool isHeld)
    {
        if (weaponData == null)
        {
            return false;
        }

        MeleeAttackProfile profile = weaponData.secondaryAttack;
        EnsureValidProfile(ref profile, isSecondary: true);

        bool shouldTrigger = profile.allowHoldToAttack ? (wasPressedThisFrame || isHeld) : wasPressedThisFrame;
        if (!shouldTrigger || Time.time < nextAllowedAttackTime)
        {
            return false;
        }

        ExecuteMeleeAttack(profile);
        OnSecondaryAttackPerformed?.Invoke(profile);
        return true;
    }

    private void ExecuteMeleeAttack(MeleeAttackProfile profile)
    {
        nextAllowedAttackTime = Time.time + Mathf.Max(0.05f, profile.cooldown);

        Physics.SyncTransforms();

        Transform originTransform = AimOrigin != null ? AimOrigin : transform;
        Vector3 origin = originTransform.position;
        Vector3 direction = originTransform.forward;

        float radius = Mathf.Max(0.1f, profile.radius);
        float range = Mathf.Max(0.5f, profile.range);
        int damage = Mathf.Max(1, profile.damage);
        int maxTargets = Mathf.Max(1, profile.maxTargets);

        LayerMask mask = weaponData.hitLayers.value != 0 ? weaponData.hitLayers : (LayerMask)(~0);

        HitEnemiesSet.Clear();
        int targetsHit = 0;

        // 1. Check colliders already overlapping the starting sphere at camera origin.
        int overlapCount = Physics.OverlapSphereNonAlloc(origin, radius, OverlapBuffer, mask, QueryTriggerInteraction.Collide);
        for (int i = 0; i < overlapCount && targetsHit < maxTargets; i++)
        {
            if (TryDamageCollider(OverlapBuffer[i], damage))
            {
                targetsHit++;
            }
        }

        if (targetsHit >= maxTargets)
        {
            return;
        }

        // 2. SphereCast forward along the crosshair direction.
        int hitCount = Physics.SphereCastNonAlloc(origin, radius, direction, HitBuffer, range, mask, QueryTriggerInteraction.Collide);
        if (hitCount > 0)
        {
            SortHitsByDistance(hitCount);
            for (int i = 0; i < hitCount && targetsHit < maxTargets; i++)
            {
                if (TryDamageCollider(HitBuffer[i].collider, damage))
                {
                    targetsHit++;
                }
            }
        }
    }

    private bool TryDamageCollider(Collider col, int damage)
    {
        if (col == null)
        {
            return false;
        }

        if (OwnerRoot != null && col.transform.IsChildOf(OwnerRoot))
        {
            return false;
        }

        EnemyHealth enemy = col.GetComponentInParent<EnemyHealth>();
        if (enemy == null)
        {
            enemy = col.GetComponent<EnemyHealth>();
        }

        if (enemy != null && !enemy.IsDead && HitEnemiesSet.Add(enemy))
        {
            enemy.TakeDamage(damage);
            OnTargetDamaged?.Invoke(enemy, damage);
            Debug.Log($"[{WeaponName}] Hit {enemy.name} for {damage} damage. Remaining HP: {enemy.CurrentHealth}/{enemy.MaxHealth}");
            return true;
        }

        return false;
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
        if (profile.cooldown <= 0.01f)
        {
            profile.cooldown = isSecondary ? 0.85f : 0.4f;
        }
        if (profile.maxTargets < 1)
        {
            profile.maxTargets = 1;
        }
    }

    private static void SortHitsByDistance(int count)
    {
        for (int i = 1; i < count; i++)
        {
            RaycastHit current = HitBuffer[i];
            int j = i - 1;
            while (j >= 0 && HitBuffer[j].distance > current.distance)
            {
                HitBuffer[j + 1] = HitBuffer[j];
                j--;
            }
            HitBuffer[j + 1] = current;
        }
    }
}
