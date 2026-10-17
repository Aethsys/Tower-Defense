using UnityEngine;

public abstract class WeaponBase : MonoBehaviour
{
    protected Transform OwnerRoot { get; private set; }
    protected Transform AimOrigin { get; private set; }

    public abstract string WeaponName { get; }

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
}
