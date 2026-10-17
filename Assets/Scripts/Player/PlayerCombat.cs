using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

[RequireComponent(typeof(WeaponManager))]
public class PlayerCombat : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] private Transform cameraTransform;

    private InputSystem_Actions inputActions;
    private bool isGameplayScene;

    private void Awake()
    {
        if (weaponManager == null)
        {
            weaponManager = GetComponent<WeaponManager>();
        }

        if (cameraTransform == null)
        {
            Camera cam = GetComponentInChildren<Camera>(true);
            if (cam != null)
            {
                cameraTransform = cam.transform;
            }
        }

        if (weaponManager != null)
        {
            weaponManager.Initialize(transform, cameraTransform != null ? cameraTransform : transform);
        }

        inputActions = new InputSystem_Actions();
        RefreshSceneState(SceneManager.GetActiveScene().name);
    }

    private void OnEnable()
    {
        inputActions?.Player.Enable();
        SceneManager.sceneLoaded += OnSceneLoaded;
        RefreshSceneState(SceneManager.GetActiveScene().name);
    }

    private void OnDisable()
    {
        inputActions?.Player.Disable();
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        inputActions?.Dispose();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        RefreshSceneState(scene.name);
    }

    private void RefreshSceneState(string sceneName)
    {
        isGameplayScene = sceneName != "Main Menu" && sceneName != "Lobby";
    }

    private void Update()
    {
        if (!isGameplayScene)
        {
            return;
        }

        if (IsSpawned && !IsOwner)
        {
            return;
        }

        if (Cursor.lockState != CursorLockMode.Locked)
        {
            return;
        }

        if (weaponManager == null || weaponManager.ActiveWeapon == null)
        {
            return;
        }

        HandleCombatInput();
    }

    private void HandleCombatInput()
    {
        WeaponBase activeWeapon = weaponManager.ActiveWeapon;

        // Primary Action (Left Click / Gamepad Attack)
        bool primaryPressed = inputActions.Player.Attack.WasPressedThisFrame();
        bool primaryHeld = inputActions.Player.Attack.IsPressed();

        if (primaryPressed || primaryHeld)
        {
            activeWeapon.TryUsePrimary(primaryPressed, primaryHeld);
        }

        // Secondary Action (Right Click / Gamepad Left Trigger)
        bool secondaryPressed = false;
        bool secondaryHeld = false;

        if (Mouse.current != null)
        {
            secondaryPressed = Mouse.current.rightButton.wasPressedThisFrame;
            secondaryHeld = Mouse.current.rightButton.isPressed;
        }

        if (!secondaryPressed && !secondaryHeld && Gamepad.current != null)
        {
            secondaryPressed = Gamepad.current.leftTrigger.wasPressedThisFrame;
            secondaryHeld = Gamepad.current.leftTrigger.isPressed;
        }

        if (secondaryPressed || secondaryHeld)
        {
            activeWeapon.TryUseSecondary(secondaryPressed, secondaryHeld);
        }
    }
}
