using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : NetworkBehaviour
{
    [Header("Movement Speeds")]
    [SerializeField] private float walkSpeed = 5f;
    [SerializeField] private float sprintSpeed = 8.5f;
    [Tooltip("Si está activo, solo se puede correr moviéndose hacia delante (como la mayoría de FPS).")]
    [SerializeField] private bool sprintRequiresForward = true;

    [Header("Acceleration")]
    [Tooltip("Aceleración en suelo (m/s²). Más alto = más responsivo.")]
    [SerializeField] private float groundAcceleration = 55f;
    [Tooltip("Frenado en suelo al soltar el input (m/s²).")]
    [SerializeField] private float groundDeceleration = 65f;
    [Tooltip("Aceleración en el aire (control aéreo).")]
    [SerializeField] private float airAcceleration = 18f;
    [Tooltip("Frenado en el aire sin input (conserva el impulso del salto).")]
    [SerializeField] private float airDeceleration = 3f;

    [Header("Jump & Gravity")]
    [SerializeField] private float jumpHeight = 1.5f;
    [SerializeField] private float gravity = -19.62f;
    [Tooltip("Multiplicador de gravedad al caer: caídas más firmes, menos 'flotantes'.")]
    [SerializeField] private float fallGravityMultiplier = 1.5f;
    [Tooltip("Multiplicador de gravedad al soltar el botón de salto mientras sube (salto de altura variable).")]
    [SerializeField] private float lowJumpGravityMultiplier = 2f;
    [SerializeField] private float maxFallSpeed = 30f;
    [Tooltip("Tiempo tras salir de un borde en el que aún se puede saltar.")]
    [SerializeField] private float coyoteTime = 0.12f;
    [Tooltip("Tiempo que se recuerda el input de salto antes de tocar el suelo.")]
    [SerializeField] private float jumpBufferTime = 0.12f;
    [Tooltip("Velocidad vertical con la que se mantiene pegado al suelo (rampas y escalones hacia abajo).")]
    [SerializeField] private float groundStickVelocity = 4f;

    [Header("Ground Check")]
    [SerializeField] private LayerMask groundLayers = ~0;
    [Tooltip("Distancia extra bajo los pies para considerar que está en el suelo.")]
    [SerializeField] private float groundProbeDistance = 0.12f;

    [Header("Camera & Mouse Look")]
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private Transform meshTransform;
    [SerializeField] private float mouseSensitivity = 0.15f;
    [Tooltip("Velocidad de giro con stick de mando (grados/segundo).")]
    [SerializeField] private Vector2 gamepadLookSpeed = new Vector2(200f, 140f);
    [SerializeField] private float maxLookAngle = 85f;

    [Header("First-Person Local Visibility")]
    [Tooltip("Si está activo, oculta los Renderers del Mesh para el propio jugador en primera persona, manteniendo el Animator activo y permitiendo que los demás jugadores vean el cuerpo completo.")]
    [SerializeField] private bool hideMeshForLocalPlayer = true;

    [Header("Animator")]
    [SerializeField] private Animator playerAnimator;
    [Tooltip("Valor enviado al parámetro 'Speed' del Animator al caminar.")]
    [SerializeField] private float walkAnimSpeed = 0.5f;
    [Tooltip("Valor enviado al parámetro 'Speed' del Animator al correr (Sprint).")]
    [SerializeField] private float sprintAnimSpeed = 1f;
    [Tooltip("Suavizado al cambiar el parámetro 'Speed' en el Blend Tree.")]
    [SerializeField] private float animSpeedDampTime = 0.1f;

    [Header("Remote Players")]
    [Tooltip("Suavizado de la interpolación de jugadores remotos. Más alto = más pegado al dato de red.")]
    [SerializeField] private float remoteSmoothing = 18f;
    [Tooltip("Si el jugador remoto está más lejos que esto, se teletransporta en vez de interpolar.")]
    [SerializeField] private float remoteSnapDistance = 5f;

    // Parámetros del Animator. 'Speed' (float) alimenta el Blend Tree Idle/Walk/Run;
    // 'Jump' y 'Attack' son triggers. 'Grounded' (bool) es opcional: si existe en el controller se actualiza.
    private static readonly int AnimHashSpeed = Animator.StringToHash("Speed");
    private static readonly int AnimHashJump = Animator.StringToHash("Jump");
    private static readonly int AnimHashAttack = Animator.StringToHash("Attack");
    private static readonly int AnimHashGrounded = Animator.StringToHash("Grounded");

    private static readonly RaycastHit[] GroundHits = new RaycastHit[8];

    private Transform selfTransform;
    private Transform armatureTransform;
    private CharacterController characterController;
    private PlayerCombat playerCombat;
    private PlayerCameraFeel cameraFeel;
    private Renderer[] meshRenderers;
    private InputSystem_Actions inputActions;
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction jumpAction;
    private InputAction sprintAction;
    private InputAction attackAction;

    private Vector3 horizontalVelocity;
    private float verticalVelocity;
    private Vector3 initialMeshLocalPosition;
    private Quaternion initialMeshLocalRotation = Quaternion.identity;
    private Vector3 initialArmatureLocalPosition;
    private Quaternion initialArmatureLocalRotation = Quaternion.identity;
    private float xRotation;
    private float initialCameraLocalYaw;
    private float jumpVelocity;
    private float currentTargetAnimSpeed;
    private float lastGroundedTime = -10f;
    private float lastJumpPressedTime = -10f;
    private bool isGrounded;
    private bool isSprinting;
    private bool isJumping;
    private bool isGameplayScene;
    private bool hasInitializedGameplayPosition;
    private bool hasAppliedLocalVisibility;
    private bool lastLocalMeshHiddenState;

    private RuntimeAnimatorController cachedController;
    private bool animHasSpeed;
    private bool animHasJump;
    private bool animHasAttack;
    private bool animHasGrounded;

    private readonly NetworkVariable<Vector3> netPosition = new NetworkVariable<Vector3>(
        writePerm: NetworkVariableWritePermission.Owner);

    private readonly NetworkVariable<Quaternion> netRotation = new NetworkVariable<Quaternion>(
        writePerm: NetworkVariableWritePermission.Owner);

    private readonly NetworkVariable<float> netAnimSpeed = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    private readonly NetworkVariable<bool> netGrounded = new NetworkVariable<bool>(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    // ---------------------------------------------------------------- Public state (game feel / combat)

    /// <summary>Se dispara cuando el jugador local salta.</summary>
    public event Action Jumped;

    /// <summary>Se dispara al aterrizar. El parámetro es la velocidad de caída (positiva) en el impacto.</summary>
    public event Action<float> Landed;

    public Transform CameraTransform => cameraTransform;
    public float CameraPitch => xRotation;
    public float CameraBaseYaw => initialCameraLocalYaw;
    public Vector3 HorizontalVelocity => horizontalVelocity;
    public float VerticalVelocity => verticalVelocity;
    public bool IsGrounded => isGrounded;
    public bool IsSprinting => isSprinting;
    public float WalkSpeed => walkSpeed;
    public float SprintSpeed => sprintSpeed;
    public bool IsGameplayScene => isGameplayScene;

    /// <summary>Cuando es true, se ignoran el movimiento y el salto (por ejemplo, mientras se construye una torre).</summary>
    public bool MovementLocked { get; set; }

    private bool InputBlocked => MovementLocked || PauseMenu.IsOpen;

    /// <summary>True si este jugador lo controla la máquina local y estamos en una escena de juego.</summary>
    public bool IsLocallyControlled => isGameplayScene && (!IsSpawned || IsOwner);

    private void Awake()
    {
        useGUILayout = false;
        selfTransform = transform;
        characterController = GetComponent<CharacterController>();
        playerCombat = GetComponent<PlayerCombat>();

        if (cameraTransform == null)
        {
            Camera cam = GetComponentInChildren<Camera>(true);
            if (cam != null)
            {
                cameraTransform = cam.transform;
            }
        }

        if (cameraTransform != null)
        {
            initialCameraLocalYaw = cameraTransform.localEulerAngles.y;
            float pitch = cameraTransform.localEulerAngles.x;
            xRotation = Mathf.Clamp(pitch > 180f ? pitch - 360f : pitch, -maxLookAngle, maxLookAngle);
        }

        InitializeMeshAndAnimatorReferences();

        jumpVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        inputActions = new InputSystem_Actions();
        var playerMap = inputActions.Player;
        moveAction = playerMap.Move;
        lookAction = playerMap.Look;
        jumpAction = playerMap.Jump;
        sprintAction = playerMap.Sprint;
        attackAction = playerMap.Attack;

        // El game feel de cámara se añade automáticamente si el prefab aún no lo tiene.
        cameraFeel = GetComponent<PlayerCameraFeel>();
        if (cameraFeel == null)
        {
            cameraFeel = gameObject.AddComponent<PlayerCameraFeel>();
        }

        RefreshSceneState(SceneManager.GetActiveScene().name);
    }

    private void OnValidate()
    {
        walkSpeed = Mathf.Max(0.1f, walkSpeed);
        sprintSpeed = Mathf.Max(walkSpeed, sprintSpeed);
        jumpHeight = Mathf.Max(0f, jumpHeight);
        gravity = Mathf.Min(-0.1f, gravity);
        fallGravityMultiplier = Mathf.Max(1f, fallGravityMultiplier);
        lowJumpGravityMultiplier = Mathf.Max(1f, lowJumpGravityMultiplier);
        jumpVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
    }

    private void InitializeMeshAndAnimatorReferences()
    {
        if (meshTransform == null)
        {
            meshTransform = selfTransform.Find("Mesh");

            if (meshTransform == null)
            {
                Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Transform rt = renderers[i].transform;
                    if (rt != selfTransform && (cameraTransform == null || !rt.IsChildOf(cameraTransform)))
                    {
                        while (rt.parent != null && rt.parent != selfTransform)
                        {
                            rt = rt.parent;
                        }
                        meshTransform = rt;
                        break;
                    }
                }
            }
        }

        if (meshTransform != null && meshTransform != selfTransform)
        {
            if (meshTransform.parent == selfTransform)
            {
                initialMeshLocalPosition = meshTransform.localPosition;
                initialMeshLocalRotation = meshTransform.localRotation;
            }
            else
            {
                initialMeshLocalPosition = selfTransform.InverseTransformPoint(meshTransform.position);
                initialMeshLocalRotation = Quaternion.Inverse(selfTransform.rotation) * meshTransform.rotation;
            }

            for (int i = 0; i < meshTransform.childCount; i++)
            {
                Transform child = meshTransform.GetChild(i);
                if (child != null && child.name.StartsWith("Armature", StringComparison.OrdinalIgnoreCase))
                {
                    armatureTransform = child;
                    initialArmatureLocalPosition = new Vector3(0f, child.localPosition.y, 0f);
                    initialArmatureLocalRotation = child.localRotation;
                    if (child.name != "Armature.001")
                    {
                        child.name = "Armature.001";
                    }
                    break;
                }
            }

            meshRenderers = meshTransform.GetComponentsInChildren<Renderer>(true);

            Animator[] animators = meshTransform.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                if (animators[i] != null)
                {
                    animators[i].applyRootMotion = false;
                    animators[i].cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    if (playerAnimator == null)
                    {
                        playerAnimator = animators[i];
                    }
                }
            }
        }

        if (playerAnimator == null)
        {
            playerAnimator = GetComponentInChildren<Animator>(true);
        }

        if (playerAnimator != null)
        {
            playerAnimator.applyRootMotion = false;
            playerAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            playerAnimator.speed = 1f;
            if (playerAnimator.runtimeAnimatorController != null)
            {
                playerAnimator.Rebind();
                playerAnimator.Update(0f);
            }
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (!IsOwner)
        {
            inputActions?.Player.Disable();
        }

        RefreshSceneState(SceneManager.GetActiveScene().name);
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

        if (Cursor.lockState == CursorLockMode.Locked && (!IsSpawned || IsOwner))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        inputActions?.Dispose();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        hasInitializedGameplayPosition = false;
        RefreshSceneState(scene.name);
    }

    private void RefreshSceneState(string sceneName)
    {
        isGameplayScene = sceneName != "Main Menu" && sceneName != "Lobby";

        hasAppliedLocalVisibility = false;
        UpdateLocalMeshVisibility();
        SyncMeshPositionAndRotation();
    }

    private void UpdateLocalMeshVisibility()
    {
        bool shouldHide = hideMeshForLocalPlayer && isGameplayScene && (!IsSpawned || IsOwner);
        if (hasAppliedLocalVisibility && lastLocalMeshHiddenState == shouldHide)
        {
            return;
        }

        hasAppliedLocalVisibility = true;
        lastLocalMeshHiddenState = shouldHide;

        if (meshTransform == null || meshTransform == selfTransform)
        {
            return;
        }

        if (!meshTransform.gameObject.activeSelf)
        {
            meshTransform.gameObject.SetActive(true);
        }

        if (meshRenderers == null || meshRenderers.Length == 0)
        {
            meshRenderers = meshTransform.GetComponentsInChildren<Renderer>(true);
        }

        bool targetEnabled = !shouldHide;
        for (int i = 0; i < meshRenderers.Length; i++)
        {
            Renderer r = meshRenderers[i];
            if (r != null && (cameraTransform == null || !r.transform.IsChildOf(cameraTransform)))
            {
                r.enabled = targetEnabled;
            }
        }
    }

    private void Update()
    {
        UpdateLocalMeshVisibility();

        if (!isGameplayScene)
        {
            if (Cursor.lockState == CursorLockMode.Locked && (!IsSpawned || IsOwner))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            horizontalVelocity = Vector3.zero;
            currentTargetAnimSpeed = 0f;
            UpdateAnimatorLocomotion(0f, true, Time.deltaTime);
            SyncMeshPositionAndRotation();
            return;
        }

        if (IsSpawned && !IsOwner)
        {
            ApplyRemoteState();
            return;
        }

        if (!hasInitializedGameplayPosition)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            horizontalVelocity = Vector3.zero;
            verticalVelocity = 0f;

            if (IsSpawned)
            {
                netPosition.Value = selfTransform.position;
                netRotation.Value = selfTransform.rotation;
                netAnimSpeed.Value = 0f;
                netGrounded.Value = true;
            }
            hasInitializedGameplayPosition = true;
        }

        float dt = Time.deltaTime;

        HandleCursorLock();
        HandleLook(dt);
        HandleMovementAndJump(dt);
        HandleFallbackAttackInput();
        SyncMeshPositionAndRotation();

        if (IsSpawned)
        {
            SyncOwnerNetworkState(dt);
        }
    }

    private void LateUpdate()
    {
        SyncMeshPositionAndRotation();
    }

    private float netSyncTimer;
    private const float NetSyncInterval = 0.033f; // 30 Hz
    private const float MinNetMoveDistSqr = 0.0004f; // 0.02m
    private const float MinNetRotAngle = 0.75f; // 0.75 deg

    private void SyncOwnerNetworkState(float dt)
    {
        netSyncTimer += dt;
        if (netSyncTimer < NetSyncInterval)
        {
            return;
        }
        netSyncTimer = 0f;

        Vector3 currentPos = selfTransform.position;
        if ((netPosition.Value - currentPos).sqrMagnitude > MinNetMoveDistSqr)
        {
            netPosition.Value = currentPos;
        }

        Quaternion currentRot = selfTransform.rotation;
        if (Quaternion.Angle(netRotation.Value, currentRot) > MinNetRotAngle)
        {
            netRotation.Value = currentRot;
        }

        if (Mathf.Abs(netAnimSpeed.Value - currentTargetAnimSpeed) > 0.02f)
        {
            netAnimSpeed.Value = currentTargetAnimSpeed;
        }

        if (netGrounded.Value != isGrounded)
        {
            netGrounded.Value = isGrounded;
        }
    }

    // ---------------------------------------------------------------- Input / Look

    private void HandleCursorLock()
    {
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked && !PauseMenu.IsOpen)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void HandleLook(float dt)
    {
        if (Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 lookInput = lookAction.ReadValue<Vector2>();

            // El ratón entrega deltas (ya dependientes del frame); el stick entrega una dirección
            // que hay que escalar por tiempo para que no dependa de los FPS.
            InputControl activeControl = lookAction.activeControl;
            bool isGamepad = activeControl != null && activeControl.device is Gamepad;

            float yawDelta;
            float pitchDelta;
            if (isGamepad)
            {
                yawDelta = lookInput.x * gamepadLookSpeed.x * dt;
                pitchDelta = lookInput.y * gamepadLookSpeed.y * dt;
            }
            else
            {
                yawDelta = lookInput.x * mouseSensitivity;
                pitchDelta = lookInput.y * mouseSensitivity;
            }

            xRotation = Mathf.Clamp(xRotation - pitchDelta, -maxLookAngle, maxLookAngle);
            if (Mathf.Abs(yawDelta) > 0f)
            {
                selfTransform.Rotate(0f, yawDelta, 0f);
            }
        }

        // Rotación base de la cámara. PlayerCameraFeel añade bob/shake encima en LateUpdate.
        if (cameraTransform != null)
        {
            cameraTransform.localRotation = Quaternion.Euler(xRotation, initialCameraLocalYaw, 0f);
        }
    }

    // ---------------------------------------------------------------- Movement

    private void HandleMovementAndJump(float dt)
    {
        bool wasGrounded = isGrounded;
        isGrounded = CheckGrounded();

        if (isGrounded)
        {
            lastGroundedTime = Time.time;

            if (!wasGrounded)
            {
                float impactSpeed = Mathf.Max(0f, -verticalVelocity);
                isJumping = false;
                ResetTriggerSafe(AnimHashJump, animHasJump);
                Landed?.Invoke(impactSpeed);
            }

            if (verticalVelocity < 0f)
            {
                verticalVelocity = -groundStickVelocity;
            }
        }

        // ---- Input
        Vector2 moveInput = InputBlocked ? Vector2.zero : moveAction.ReadValue<Vector2>();
        if (moveInput.sqrMagnitude > 1f)
        {
            moveInput.Normalize();
        }

        bool hasMoveInput = moveInput.sqrMagnitude > 0.0001f;
        bool sprintHeld = sprintAction.IsPressed();
        bool canSprint = !sprintRequiresForward || moveInput.y > 0.1f;
        isSprinting = sprintHeld && hasMoveInput && canSprint;

        float targetSpeed = isSprinting ? sprintSpeed : walkSpeed;

        Transform referenceTransform = cameraTransform != null ? cameraTransform : selfTransform;
        Vector3 forward = Flatten(referenceTransform.forward);
        Vector3 right = Flatten(referenceTransform.right);
        Vector3 desiredVelocity = (right * moveInput.x + forward * moveInput.y) * targetSpeed;

        // ---- Aceleración / frenado (suelo vs aire)
        float accelRate;
        if (isGrounded)
        {
            accelRate = hasMoveInput ? groundAcceleration : groundDeceleration;

            // Cambios bruscos de dirección también usan el frenado para que se sientan firmes.
            if (hasMoveInput && Vector3.Dot(horizontalVelocity, desiredVelocity) < 0f)
            {
                accelRate = Mathf.Max(groundAcceleration, groundDeceleration);
            }
        }
        else
        {
            accelRate = hasMoveInput ? airAcceleration : airDeceleration;
        }

        horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, desiredVelocity, accelRate * dt);

        // ---- Salto con coyote time + jump buffer
        if (!InputBlocked && jumpAction.WasPressedThisFrame())
        {
            lastJumpPressedTime = Time.time;
        }

        bool jumpBuffered = Time.time - lastJumpPressedTime <= jumpBufferTime;
        bool canCoyote = Time.time - lastGroundedTime <= coyoteTime;

        if (jumpBuffered && canCoyote && !isJumping)
        {
            verticalVelocity = jumpVelocity;
            isJumping = true;
            isGrounded = false;
            lastJumpPressedTime = -10f;
            lastGroundedTime = -10f;
            TriggerJumpAnimation();
            Jumped?.Invoke();
        }

        // ---- Gravedad
        if (!isGrounded)
        {
            float gravityScale = 1f;
            if (verticalVelocity < 0f)
            {
                gravityScale = fallGravityMultiplier;
            }
            else if (isJumping && !jumpAction.IsPressed())
            {
                gravityScale = lowJumpGravityMultiplier;
            }

            verticalVelocity += gravity * gravityScale * dt;
            verticalVelocity = Mathf.Max(verticalVelocity, -maxFallSpeed);
        }

        Vector3 displacement = (horizontalVelocity + Vector3.up * verticalVelocity) * dt;
        CollisionFlags flags = characterController.Move(displacement);

        // Golpe contra el techo: corta la subida.
        if ((flags & CollisionFlags.Above) != 0 && verticalVelocity > 0f)
        {
            verticalVelocity = 0f;
        }

        // Si chocamos contra una pared, no acumulamos velocidad contra ella.
        if ((flags & CollisionFlags.Sides) != 0 && dt > 0f)
        {
            Vector3 actual = characterController.velocity;
            actual.y = 0f;
            if (actual.sqrMagnitude < horizontalVelocity.sqrMagnitude)
            {
                horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, actual, groundDeceleration * dt);
            }
        }

        currentTargetAnimSpeed = ComputeAnimSpeed(horizontalVelocity.magnitude);
        UpdateAnimatorLocomotion(currentTargetAnimSpeed, isGrounded, dt);
    }

    private bool CheckGrounded()
    {
        // Mientras sube por un salto no se considera suelo (evita re-saltos en el primer frame).
        if (isJumping && verticalVelocity > 0f)
        {
            return false;
        }

        if (characterController.isGrounded)
        {
            return true;
        }

        // CharacterController.isGrounded parpadea en rampas y escalones; lo reforzamos con un SphereCast.
        float radius = characterController.radius * 0.9f;
        Vector3 center = selfTransform.TransformPoint(characterController.center);
        float halfHeight = Mathf.Max(characterController.height * 0.5f, characterController.radius);
        Vector3 origin = center + Vector3.up * (radius - halfHeight + characterController.radius);
        origin.y = center.y - halfHeight + characterController.radius;
        float distance = (characterController.radius - radius) + characterController.skinWidth + groundProbeDistance;

        int count = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, GroundHits, distance, groundLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider col = GroundHits[i].collider;
            if (col == null || col == characterController || col.transform.IsChildOf(selfTransform))
            {
                continue;
            }

            // Ignora superficies demasiado inclinadas.
            if (Vector3.Angle(GroundHits[i].normal, Vector3.up) <= characterController.slopeLimit + 1f)
            {
                return true;
            }
        }

        return false;
    }

    private float ComputeAnimSpeed(float horizontalSpeed)
    {
        if (horizontalSpeed < 0.05f)
        {
            return 0f;
        }

        // Mapeo continuo: 0 → Idle, walkSpeed → walkAnimSpeed, sprintSpeed → sprintAnimSpeed.
        if (horizontalSpeed <= walkSpeed)
        {
            return walkAnimSpeed * (horizontalSpeed / walkSpeed);
        }

        float t = Mathf.Clamp01((horizontalSpeed - walkSpeed) / Mathf.Max(0.01f, sprintSpeed - walkSpeed));
        return Mathf.Lerp(walkAnimSpeed, sprintAnimSpeed, t);
    }

    private static Vector3 Flatten(Vector3 v)
    {
        v.y = 0f;
        float sqr = v.sqrMagnitude;
        return sqr > 0.0001f ? v / Mathf.Sqrt(sqr) : Vector3.zero;
    }

    private void HandleFallbackAttackInput()
    {
        if (Cursor.lockState != CursorLockMode.Locked)
        {
            return;
        }

        if (playerCombat != null && playerCombat.enabled && playerCombat.HasActiveWeapon)
        {
            return;
        }

        if (attackAction != null && attackAction.WasPressedThisFrame())
        {
            TriggerAttackAnimation();
        }
    }

    // ---------------------------------------------------------------- Animator

    private bool EnsureAnimatorReady()
    {
        if (playerAnimator == null || !playerAnimator.isActiveAndEnabled)
        {
            return false;
        }

        RuntimeAnimatorController controller = playerAnimator.runtimeAnimatorController;
        if (controller == null)
        {
            cachedController = null;
            return false;
        }

        if (controller != cachedController)
        {
            cachedController = controller;
            animHasSpeed = animHasJump = animHasAttack = animHasGrounded = false;

            AnimatorControllerParameter[] parameters = playerAnimator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                int hash = parameters[i].nameHash;
                AnimatorControllerParameterType type = parameters[i].type;
                if (hash == AnimHashSpeed && type == AnimatorControllerParameterType.Float) animHasSpeed = true;
                else if (hash == AnimHashJump && type == AnimatorControllerParameterType.Trigger) animHasJump = true;
                else if (hash == AnimHashAttack && type == AnimatorControllerParameterType.Trigger) animHasAttack = true;
                else if (hash == AnimHashGrounded && type == AnimatorControllerParameterType.Bool) animHasGrounded = true;
            }
        }

        return true;
    }

    private void UpdateAnimatorLocomotion(float targetSpeed, bool grounded, float dt)
    {
        if (!EnsureAnimatorReady())
        {
            return;
        }

        if (animHasSpeed)
        {
            if (animSpeedDampTime > 0f && dt > 0f)
            {
                playerAnimator.SetFloat(AnimHashSpeed, targetSpeed, animSpeedDampTime, dt);
                if (targetSpeed == 0f && playerAnimator.GetFloat(AnimHashSpeed) < 0.01f)
                {
                    playerAnimator.SetFloat(AnimHashSpeed, 0f);
                }
            }
            else
            {
                playerAnimator.SetFloat(AnimHashSpeed, targetSpeed);
            }
        }

        if (animHasGrounded)
        {
            playerAnimator.SetBool(AnimHashGrounded, grounded);
        }
    }

    private void SetTriggerSafe(int hash, bool exists)
    {
        if (EnsureAnimatorReady() && exists)
        {
            playerAnimator.SetTrigger(hash);
        }
    }

    private void ResetTriggerSafe(int hash, bool exists)
    {
        if (EnsureAnimatorReady() && exists)
        {
            playerAnimator.ResetTrigger(hash);
        }
    }

    public void TriggerJumpAnimation()
    {
        PlayJumpTriggerLocal();

        if (IsSpawned && IsOwner)
        {
            TriggerJumpServerRpc();
        }
    }

    public void TriggerAttackAnimation()
    {
        PlayAttackTriggerLocal();

        if (IsSpawned && IsOwner)
        {
            TriggerAttackServerRpc();
        }
    }

    private void PlayJumpTriggerLocal()
    {
        ResetTriggerSafe(AnimHashAttack, animHasAttack);
        SetTriggerSafe(AnimHashJump, animHasJump);
    }

    private void PlayAttackTriggerLocal()
    {
        SetTriggerSafe(AnimHashAttack, animHasAttack);
    }

    [Rpc(SendTo.Server)]
    private void TriggerJumpServerRpc()
    {
        TriggerJumpClientRpc();
    }

    [Rpc(SendTo.NotOwner)]
    private void TriggerJumpClientRpc()
    {
        PlayJumpTriggerLocal();
    }

    [Rpc(SendTo.Server)]
    private void TriggerAttackServerRpc()
    {
        TriggerAttackClientRpc();
    }

    [Rpc(SendTo.NotOwner)]
    private void TriggerAttackClientRpc()
    {
        PlayAttackTriggerLocal();
    }

    // ---------------------------------------------------------------- Remote players

    private void ApplyRemoteState()
    {
        float dt = Time.deltaTime;

        if (netPosition.Value != Vector3.zero)
        {
            Vector3 currentPos = selfTransform.position;
            Vector3 targetPos = netPosition.Value;

            if ((targetPos - currentPos).sqrMagnitude > remoteSnapDistance * remoteSnapDistance)
            {
                selfTransform.SetPositionAndRotation(targetPos, netRotation.Value);
            }
            else
            {
                // Suavizado exponencial: independiente del framerate.
                float t = 1f - Mathf.Exp(-remoteSmoothing * dt);
                Vector3 nextPos = Vector3.Lerp(currentPos, targetPos, t);
                Quaternion nextRot = Quaternion.Slerp(selfTransform.rotation, netRotation.Value, t);
                selfTransform.SetPositionAndRotation(nextPos, nextRot);
            }
        }

        UpdateAnimatorLocomotion(netAnimSpeed.Value, netGrounded.Value, dt);
        SyncMeshPositionAndRotation();
    }

    private void SyncMeshPositionAndRotation()
    {
        if (meshTransform == null || meshTransform == selfTransform)
        {
            return;
        }

        if (meshTransform.parent == selfTransform)
        {
            meshTransform.SetLocalPositionAndRotation(initialMeshLocalPosition, initialMeshLocalRotation);
        }
        else
        {
            Vector3 worldPos = selfTransform.TransformPoint(initialMeshLocalPosition);
            Quaternion worldRot = selfTransform.rotation * initialMeshLocalRotation;
            meshTransform.SetPositionAndRotation(worldPos, worldRot);
        }

        if (armatureTransform != null)
        {
            armatureTransform.SetLocalPositionAndRotation(initialArmatureLocalPosition, initialArmatureLocalRotation);
        }
    }
}
