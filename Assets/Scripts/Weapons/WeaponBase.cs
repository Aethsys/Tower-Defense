using System;
using UnityEngine;

/// <summary>
/// Resultado de un ataque ya resuelto (después del hit delay). Se usa para el feedback del jugador
/// (camera shake, hit marker, etc.) sin acoplar el combate a un tipo de arma concreto.
/// </summary>
public struct WeaponAttackResult
{
    public int TargetsHit;
    public int DamagePerTarget;
    public float HitShake;
    public bool IsSecondary;

    public bool HitSomething => TargetsHit > 0;
}

public abstract class WeaponBase : MonoBehaviour
{
    protected Transform OwnerRoot { get; private set; }
    protected Transform AimOrigin { get; private set; }

    public abstract string WeaponName { get; }

    /// <summary>Se dispara en el momento del input aceptado (útil para animación / kick de cámara).</summary>
    public event Action<WeaponBase, bool> AttackStarted;

    /// <summary>Se dispara cuando el golpe se resuelve (impacte o no).</summary>
    public event Action<WeaponBase, WeaponAttackResult> AttackResolved;

    public virtual void Initialize(Transform ownerRoot, Transform aimOrigin)
    {
        OwnerRoot = ownerRoot;
        AimOrigin = aimOrigin;
    }

    public virtual void OnEquip()
    {
        gameObject.SetActive(true);
    }

    public virtual void OnUnequip()
    {
        if (this != null && gameObject != null && transform.parent != null && OwnerRoot != null && gameObject != OwnerRoot.gameObject)
        {
            gameObject.SetActive(false);
        }
    }

    public abstract bool TryUsePrimary(bool wasPressedThisFrame, bool isHeld);

    public abstract bool TryUseSecondary(bool wasPressedThisFrame, bool isHeld);

    protected void RaiseAttackStarted(bool isSecondary)
    {
        AttackStarted?.Invoke(this, isSecondary);
    }

    protected void RaiseAttackResolved(WeaponAttackResult result)
    {
        AttackResolved?.Invoke(this, result);
    }
}
