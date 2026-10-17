using System;
using UnityEngine;

public class WeaponManager : MonoBehaviour
{
    [Header("Weapon References")]
    [SerializeField] private WeaponBase defaultUnarmedWeapon;
    [SerializeField] private WeaponBase currentEquippedWeapon;
    [SerializeField] private Transform aimOrigin;

    public WeaponBase ActiveWeapon => currentEquippedWeapon != null ? currentEquippedWeapon : defaultUnarmedWeapon;
    public WeaponBase EquippedWeapon => currentEquippedWeapon;
    public WeaponBase UnarmedWeapon => defaultUnarmedWeapon;
    public bool IsUnarmed => currentEquippedWeapon == null || currentEquippedWeapon == defaultUnarmedWeapon;

    public event Action<WeaponBase> OnWeaponChanged;

    public void Initialize(Transform ownerRoot, Transform cameraOrAimOrigin)
    {
        if (cameraOrAimOrigin != null)
        {
            aimOrigin = cameraOrAimOrigin;
        }

        if (defaultUnarmedWeapon == null)
        {
            defaultUnarmedWeapon = GetComponent<MeleeWeapon>();
            if (defaultUnarmedWeapon == null)
            {
                defaultUnarmedWeapon = gameObject.AddComponent<MeleeWeapon>();
            }
        }

        defaultUnarmedWeapon.Initialize(ownerRoot, aimOrigin);

        if (currentEquippedWeapon != null && currentEquippedWeapon != defaultUnarmedWeapon)
        {
            currentEquippedWeapon.Initialize(ownerRoot, aimOrigin);
            currentEquippedWeapon.OnEquip();
        }
        else
        {
            defaultUnarmedWeapon.OnEquip();
        }
    }

    public void EquipWeapon(WeaponBase newWeapon)
    {
        if (newWeapon == currentEquippedWeapon)
        {
            return;
        }

        if (currentEquippedWeapon != null && currentEquippedWeapon != defaultUnarmedWeapon)
        {
            currentEquippedWeapon.OnUnequip();
        }

        currentEquippedWeapon = newWeapon;

        if (currentEquippedWeapon != null)
        {
            currentEquippedWeapon.Initialize(transform, aimOrigin);
            currentEquippedWeapon.OnEquip();
        }
        else if (defaultUnarmedWeapon != null)
        {
            defaultUnarmedWeapon.OnEquip();
        }

        OnWeaponChanged?.Invoke(ActiveWeapon);
    }

    public void UnequipWeapon()
    {
        EquipWeapon(null);
    }
}
