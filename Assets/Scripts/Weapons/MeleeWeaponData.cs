using System;
using UnityEngine;

[Serializable]
public struct MeleeAttackProfile
{
    public int damage;
    public float range;
    public float radius;
    public float cooldown;
    [Min(1)] public int maxTargets;
    public bool allowHoldToAttack;
    public string animationTrigger;

    public static MeleeAttackProfile CreateDefaultPrimary()
    {
        return new MeleeAttackProfile
        {
            damage = 10,
            range = 2.6f,
            radius = 0.45f,
            cooldown = 0.4f,
            maxTargets = 1,
            allowHoldToAttack = false,
            animationTrigger = "PrimaryAttack"
        };
    }

    public static MeleeAttackProfile CreateDefaultSecondary()
    {
        return new MeleeAttackProfile
        {
            damage = 20,
            range = 2.8f,
            radius = 0.55f,
            cooldown = 0.85f,
            maxTargets = 1,
            allowHoldToAttack = false,
            animationTrigger = "SecondaryAttack"
        };
    }
}

[CreateAssetMenu(fileName = "NewMeleeWeaponData", menuName = "Weapons/Melee Weapon Data")]
public class MeleeWeaponData : ScriptableObject
{
    [Header("Weapon Info")]
    public string weaponName = "Unarmed";

    [Header("Attack Profiles")]
    public MeleeAttackProfile primaryAttack = MeleeAttackProfile.CreateDefaultPrimary();
    public MeleeAttackProfile secondaryAttack = MeleeAttackProfile.CreateDefaultSecondary();

    [Header("Hit Detection")]
    public LayerMask hitLayers = ~0;
}
