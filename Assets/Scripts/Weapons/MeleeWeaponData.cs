using System;
using UnityEngine;

[Serializable]
public struct MeleeAttackProfile
{
    [Header("Damage")]
    public int damage;
    [Min(1)] public int maxTargets;

    [Header("Reach")]
    [Tooltip("Distancia máxima del golpe medida desde el origen de apuntado (cámara).")]
    public float range;
    [Tooltip("Radio extra de tolerancia del golpe (hace el ataque más permisivo).")]
    public float radius;
    [Tooltip("Medio ángulo del cono de golpe en grados. 45 = cono de 90º delante de la cámara.")]
    [Range(5f, 180f)] public float arcHalfAngle;

    [Header("Timing")]
    public float cooldown;
    [Tooltip("Tiempo entre el input y el momento en que el golpe conecta (wind-up). 0 = instantáneo.")]
    [Min(0f)] public float hitDelay;
    public bool allowHoldToAttack;

    [Header("Impact")]
    [Tooltip("Velocidad de empuje aplicada al enemigo (m/s).")]
    [Min(0f)] public float knockbackForce;
    [Tooltip("Tiempo que el enemigo queda aturdido tras recibir el golpe.")]
    [Min(0f)] public float stunDuration;
    [Tooltip("Intensidad del camera shake cuando el golpe impacta (0-1).")]
    [Range(0f, 1f)] public float hitShake;

    [Header("Animation")]
    public string animationTrigger;

    public static MeleeAttackProfile CreateDefaultPrimary()
    {
        return new MeleeAttackProfile
        {
            damage = 10,
            maxTargets = 1,
            range = 2.6f,
            radius = 0.45f,
            arcHalfAngle = 40f,
            cooldown = 0.4f,
            hitDelay = 0.08f,
            allowHoldToAttack = false,
            knockbackForce = 4f,
            stunDuration = 0.2f,
            hitShake = 0.18f,
            animationTrigger = "PrimaryAttack"
        };
    }

    public static MeleeAttackProfile CreateDefaultSecondary()
    {
        return new MeleeAttackProfile
        {
            damage = 20,
            maxTargets = 3,
            range = 2.8f,
            radius = 0.55f,
            arcHalfAngle = 55f,
            cooldown = 0.85f,
            hitDelay = 0.16f,
            allowHoldToAttack = false,
            knockbackForce = 7.5f,
            stunDuration = 0.4f,
            hitShake = 0.3f,
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
    [Tooltip("Capas que bloquean el golpe (paredes, torre...). Si un obstáculo está entre la cámara y el enemigo, no hay daño.")]
    public LayerMask obstructionLayers = 0;
}
