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
    private InputAction attackAction;
    private bool isGameplayScene;

    private void Awake()
    {
        useGUILayout = false;

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
        attackAction = inputActions.Player.Attack;
        RefreshSceneState(SceneManager.GetActiveScene().name);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (!IsOwner)
        {
            inputActions?.Player.Disable();
        }
    }

    private void OnEnable()
    {
        if (!IsSpawned || IsOwner)
        {
            inputActions?.Player.Enable();
        }

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

        if (weaponManager == null)
        {
            return;
        }

        WeaponBase activeWeapon = weaponManager.ActiveWeapon;
        if (activeWeapon == null)
        {
            return;
        }

        HandleCombatInput(activeWeapon);
    }

    private void HandleCombatInput(WeaponBase activeWeapon)
    {
        bool primaryPressed = attackAction.WasPressedThisFrame();
        bool primaryHeld = attackAction.IsPressed();

        if (primaryPressed || primaryHeld)
        {
            activeWeapon.TryUsePrimary(primaryPressed, primaryHeld);
        }

        bool secondaryPressed = false;
        bool secondaryHeld = false;

        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            var rightButton = mouse.rightButton;
            secondaryPressed = rightButton.wasPressedThisFrame;
            secondaryHeld = rightButton.isPressed;
        }

        if (!secondaryPressed && !secondaryHeld)
        {
            Gamepad gamepad = Gamepad.current;
            if (gamepad != null)
            {
                var leftTrigger = gamepad.leftTrigger;
                secondaryPressed = leftTrigger.wasPressedThisFrame;
                secondaryHeld = leftTrigger.isPressed;
            }
        }

        if (secondaryPressed || secondaryHeld)
        {
            activeWeapon.TryUseSecondary(secondaryPressed, secondaryHeld);
        }
    }
}
