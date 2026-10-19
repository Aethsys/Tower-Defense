using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[RequireComponent(typeof(WeaponManager))]
public class PlayerCombat : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private PlayerMovement playerMovement;
    [SerializeField] private PlayerCameraFeel cameraFeel;
    [SerializeField] private Image crosshairImage;

    [Header("Feel & Feedback")]
    [Tooltip("Trauma aplicado a la cámara al iniciar el golpe (wind-up).")]
    [SerializeField] private float attackStartPitchKick = -2.5f;
    [Tooltip("Color del crosshair al conectar un golpe.")]
    [SerializeField] private Color hitMarkerColor = new Color(1f, 0.2f, 0.2f, 1f);
    [Tooltip("Tiempo de duración del hit marker en pantalla.")]
    [SerializeField] private float hitMarkerDuration = 0.1f;
    [Tooltip("Escala extra del crosshair al impactar.")]
    [SerializeField] private float hitMarkerScaleMultiplier = 1.35f;

    [Header("Input Buffering")]
    [Tooltip("Margen de tiempo antes de terminar el cooldown en el que se almacena el clic.")]
    [SerializeField] private float attackBufferWindow = 0.15f;

    private InputSystem_Actions inputActions;
    private InputAction attackAction;
    private bool isGameplayScene;

    private float lastPrimaryPressTime = -10f;
    private float lastSecondaryPressTime = -10f;
    private Color defaultCrosshairColor = Color.white;
    private Vector3 defaultCrosshairScale = Vector3.one;
    private float hitMarkerTimer;
    private WeaponBase hookedWeapon;

    public bool HasActiveWeapon => weaponManager != null && weaponManager.ActiveWeapon != null;

    private void Awake()
    {
        useGUILayout = false;

        if (weaponManager == null)
        {
            weaponManager = GetComponent<WeaponManager>();
        }

        if (playerMovement == null)
        {
            playerMovement = GetComponent<PlayerMovement>();
        }

        if (cameraFeel == null)
        {
            cameraFeel = GetComponent<PlayerCameraFeel>();
        }

        if (cameraTransform == null)
        {
            Camera cam = GetComponentInChildren<Camera>(true);
            if (cam != null)
            {
                cameraTransform = cam.transform;
            }
        }

        if (crosshairImage == null)
        {
            Image[] images = GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i].gameObject.name.IndexOf("Crosshair", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    crosshairImage = images[i];
                    break;
                }
            }
        }

        if (crosshairImage != null)
        {
            defaultCrosshairColor = crosshairImage.color;
            defaultCrosshairScale = crosshairImage.rectTransform.localScale;
        }

        if (weaponManager != null)
        {
            weaponManager.Initialize(transform, cameraTransform != null ? cameraTransform : transform);
        }

        inputActions = new InputSystem_Actions();
        attackAction = inputActions.Player.Attack;
        RefreshSceneState(SceneManager.GetActiveScene().name);
    }

    private void HookWeaponEvents(WeaponBase weapon)
    {
        if (hookedWeapon != null)
        {
            hookedWeapon.AttackStarted -= HandleAttackStarted;
            hookedWeapon.AttackResolved -= HandleAttackResolved;
        }

        hookedWeapon = weapon;

        if (hookedWeapon != null)
        {
            hookedWeapon.AttackStarted += HandleAttackStarted;
            hookedWeapon.AttackResolved += HandleAttackResolved;
        }
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

        if (weaponManager != null)
        {
            weaponManager.OnWeaponChanged -= HookWeaponEvents;
            weaponManager.OnWeaponChanged += HookWeaponEvents;
            HookWeaponEvents(weaponManager.ActiveWeapon);
        }

        SceneManager.sceneLoaded += OnSceneLoaded;
        RefreshSceneState(SceneManager.GetActiveScene().name);
    }

    private void OnDisable()
    {
        inputActions?.Player.Disable();
        SceneManager.sceneLoaded -= OnSceneLoaded;

        if (hookedWeapon != null)
        {
            hookedWeapon.AttackStarted -= HandleAttackStarted;
            hookedWeapon.AttackResolved -= HandleAttackResolved;
            hookedWeapon = null;
        }

        if (weaponManager != null)
        {
            weaponManager.OnWeaponChanged -= HookWeaponEvents;
        }
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

        if (hitMarkerTimer > 0f && crosshairImage != null)
        {
            hitMarkerTimer -= Time.deltaTime;
            if (hitMarkerTimer <= 0f)
            {
                crosshairImage.color = defaultCrosshairColor;
                crosshairImage.rectTransform.localScale = defaultCrosshairScale;
            }
            else
            {
                float t = 1f - (hitMarkerTimer / hitMarkerDuration);
                crosshairImage.color = Color.Lerp(hitMarkerColor, defaultCrosshairColor, t);
                crosshairImage.rectTransform.localScale = Vector3.Lerp(defaultCrosshairScale * hitMarkerScaleMultiplier, defaultCrosshairScale, t);
            }
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
        float now = Time.time;

        bool primaryPressed = attackAction.WasPressedThisFrame();
        bool primaryHeld = attackAction.IsPressed();
        if (primaryPressed)
        {
            lastPrimaryPressTime = now;
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

        if (secondaryPressed)
        {
            lastSecondaryPressTime = now;
        }

        // Buffer de entrada
        bool tryPrimary = (now - lastPrimaryPressTime <= attackBufferWindow) || primaryHeld;
        bool trySecondary = (now - lastSecondaryPressTime <= attackBufferWindow) || secondaryHeld;

        if (trySecondary)
        {
            if (activeWeapon.TryUseSecondary(secondaryPressed, secondaryHeld))
            {
                lastSecondaryPressTime = -10f;
                playerMovement?.TriggerAttackAnimation();
                return;
            }
        }

        if (tryPrimary)
        {
            if (activeWeapon.TryUsePrimary(primaryPressed, primaryHeld))
            {
                lastPrimaryPressTime = -10f;
                playerMovement?.TriggerAttackAnimation();
            }
        }
    }

    private void HandleAttackStarted(WeaponBase weapon, bool isSecondary)
    {
        if (cameraFeel != null && (!IsSpawned || IsOwner))
        {
            cameraFeel.AddPitchImpulse(attackStartPitchKick * (isSecondary ? 1.5f : 1f));
        }
    }

    private void HandleAttackResolved(WeaponBase weapon, WeaponAttackResult result)
    {
        if (!result.HitSomething)
        {
            return;
        }

        // Si somos el atacante local:
        if (!IsSpawned || IsOwner)
        {
            if (cameraFeel != null)
            {
                cameraFeel.AddTrauma(result.HitShake);
            }

            PlayHitMarker();
        }
    }

    private void PlayHitMarker()
    {
        if (crosshairImage == null) return;

        hitMarkerTimer = hitMarkerDuration;
        crosshairImage.color = hitMarkerColor;
        crosshairImage.rectTransform.localScale = defaultCrosshairScale * hitMarkerScaleMultiplier;
    }
}
