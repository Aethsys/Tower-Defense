using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

#pragma warning disable CS0649
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(CharacterController))]
[DisallowMultipleComponent]
public class PlayerMovement : NetworkBehaviour
{
    public enum LocalPlayerBodyVisibility
    {
        [Tooltip("Invisible para la cámara del jugador local, pero proyecta sombra en el suelo.")]
        ShadowsOnly,
        [Tooltip("Completamente oculto (desactiva renderers).")]
        Hidden,
        [Tooltip("Completamente visible en la cámara del jugador local.")]
        Visible
    }

    [Header("Movement Speeds")]
    [SerializeField] private float walkSpeed = 5f;
    [SerializeField] private float sprintSpeed = 8.5f;
    [SerializeField] private float crouchSpeed = 2.5f;

    [Header("Jump & Gravity")]
    [SerializeField] private float jumpHeight = 1.5f;
    [SerializeField] private float gravity = -19.62f;
    [Tooltip("Fuerza aplicada hacia abajo al estar en el suelo para evitar despegarse en rampas o desniveles.")]
    [SerializeField] private float groundedStickForce = -2f;

    [Header("Crouch Settings")]
    [SerializeField] private float standingHeight = 2f;
    [SerializeField] private float crouchHeight = 1.1f;
    [SerializeField] private float crouchTransitionSpeed = 10f;
    [Tooltip("Capas de colisión que impiden ponerse de pie si hay un obstáculo encima.")]
    [SerializeField] private LayerMask obstacleLayers = ~0;

    [Header("Camera & Mouse Look")]
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private float mouseSensitivity = 0.15f;
    [SerializeField] private float maxLookAngle = 85f;

    [Header("Camera Game Feel")]
    [Tooltip("Activa los efectos de cámara (Head Bob, Strafe Tilt, FOV de Sprint e impacto de salto/aterrizaje).")]
    [SerializeField] private bool enableCameraFeel = true;
    [Tooltip("Frecuencia del balanceo de cabeza al caminar.")]
    [SerializeField] private float bobFrequency = 9f;
    [Tooltip("Amplitud vertical y horizontal del Head Bob al caminar.")]
    [SerializeField] private Vector2 bobAmplitude = new Vector2(0.03f, 0.045f);
    [Tooltip("Multiplicador de velocidad y amplitud del Head Bob al correr.")]
    [SerializeField] private float sprintBobMultiplier = 1.35f;
    [Tooltip("Inclinación lateral (Roll en grados) al moverse hacia los lados (A/D).")]
    [SerializeField] private float strafeTiltAngle = 2f;
    [Tooltip("Rapidez de respuesta de la inclinación y el bobbing.")]
    [SerializeField] private float cameraFeelSharpness = 10f;
    [Tooltip("Compresión vertical máxima de la cámara al aterrizar.")]
    [SerializeField] private float landCamDip = 0.14f;
    [Tooltip("Inclinación de cabeza (Pitch en grados) al saltar y aterrizar.")]
    [SerializeField] private float jumpLandPitch = 3.5f;
    [Tooltip("Velocidad de recuperación tras el impacto de aterrizaje.")]
    [SerializeField] private float landRecoverySpeed = 8f;
    [Tooltip("Aumento de FOV al hacer sprint.")]
    [SerializeField] private float sprintFovBoost = 7f;
    [Tooltip("Reducción de FOV al agacharse.")]
    [SerializeField] private float crouchFovOffset = -3f;
    [Tooltip("Velocidad de transición del FOV.")]
    [SerializeField] private float fovTransitionSpeed = 8f;

    [Header("Visual Meshes / Models")]
    [FormerlySerializedAs("playerMesh")]
    [Tooltip("Modelo o mesh en tercera persona (cuerpo completo visto por otros jugadores y en el lobby).")]
    [SerializeField] private Transform thirdPersonMesh;

    [Tooltip("Animator opcional del modelo en tercera persona para controlar automáticamente caminar, correr, saltar y agacharse tanto en local como en red.")]
    [SerializeField] private Animator thirdPersonAnimator;

    [Tooltip("Modelo o brazos en primera persona (visto únicamente por el jugador local). Se recomienda colocarlo como hijo de la cámara.")]
    [SerializeField] private Transform firstPersonMesh;

    [Tooltip("Controla cómo se visualiza el cuerpo de 3ra persona en la cámara del jugador local (ShadowsOnly oculta el cuerpo pero mantiene sombras realistas).")]
    [SerializeField] private LocalPlayerBodyVisibility localBodyVisibility = LocalPlayerBodyVisibility.ShadowsOnly;

    [Header("UI & Crosshair")]
    [Tooltip("Elemento o GameObject de la mira (Crosshair) en el Canvas de la cámara del jugador local.")]
    [SerializeField] private GameObject crosshairUI;

    [Header("Network Sync")]
    [Tooltip("Velocidad de interpolación visual para jugadores remotos en red.")]
    [SerializeField] private float networkInterpolationSpeed = 15f;

    [Header("Scene Rules")]
    [Tooltip("Escenas donde los controles de juego y movimiento deben mantenerse inactivos.")]
    [SerializeField] private string[] nonGameplaySceneNames = { "Main Menu", "Lobby" };

    public GameObject CrosshairUI
    {
        get => crosshairUI;
        set
        {
            crosshairUI = value;
            hasSearchedCrosshair = false;
        }
    }

    public Transform ThirdPersonMesh
    {
        get => thirdPersonMesh;
        set
        {
            thirdPersonMesh = value;
            CacheRenderers();
            UpdateVisualMeshVisibility();
        }
    }

    public Transform FirstPersonMesh
    {
        get => firstPersonMesh;
        set
        {
            firstPersonMesh = value;
            CacheRenderers();
            UpdateVisualMeshVisibility();
        }
    }

    public Transform PlayerMesh
    {
        get => thirdPersonMesh;
        set => ThirdPersonMesh = value;
    }

    public LocalPlayerBodyVisibility BodyVisibility
    {
        get => localBodyVisibility;
        set
        {
            localBodyVisibility = value;
            UpdateVisualMeshVisibility();
        }
    }

    public Animator ThirdPersonAnimator
    {
        get => thirdPersonAnimator;
        set
        {
            thirdPersonAnimator = value;
            CacheAnimatorParameters();
        }
    }

    public CharacterController Controller => characterController;
    public Camera PlayerCamera => cachedCamera;
    public Transform CameraTransform => cameraTransform;
    public bool IsGrounded => (!IsSpawned || IsOwner) ? frameIsGrounded : netIsGrounded.Value;
    public bool IsCrouching => (!IsSpawned || IsOwner) ? isCrouching : netIsCrouching.Value;
    public bool IsSprinting => (!IsSpawned || IsOwner) ? frameIsSprinting : netIsSprinting.Value;
    public bool IsMoving => (!IsSpawned || IsOwner) ? frameIsMoving : netIsMoving.Value;
    public Vector2 MoveInput => (!IsSpawned || IsOwner) ? frameMoveInput : (netIsMoving.Value ? Vector2.up : Vector2.zero);
    public float CurrentSpeed => IsCrouching ? crouchSpeed : (IsSprinting ? sprintSpeed : walkSpeed);
    public float VerticalVelocity => verticalVelocityY;
    public bool IsGameplaySceneActive => isGameplayScene;
    public bool IsDead => isDead;

    private Transform selfTransform;
    private CharacterController characterController;
    private Camera cachedCamera;
    private AudioListener cachedAudioListener;
    private Canvas[] cachedCanvases;
    private Renderer[] cachedThirdPersonRenderers;
    private Renderer[] cachedFirstPersonRenderers;
    private bool isThirdPersonExternal;
    private bool isFirstPersonExternal;
    private Transform remoteScaleTarget;

    private InputSystem_Actions inputActions;
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction jumpAction;
    private InputAction sprintAction;
    private InputAction crouchAction;

    private Vector3 standingCenter;
    private float verticalVelocityY;
    private float xRotation;
    private float initialCameraLocalYaw;
    private float standingCameraY = 0.5f;
    private float crouchCameraY = 0.05f;
    private float currentCrouchCamY = 0.5f;
    private Vector3 initialCameraLocalPos;
    private float baseFov = 60f;
    private float bobPhase;
    private float bobWeight;
    private float currentStrafeTilt;
    private float camImpactY;
    private float camImpactPitch;
    private float currentCamImpactY;
    private float currentCamImpactPitch;
    private bool wasGroundedForCam = true;
    private float airborneTime;
    private float lastAirVelocityY;
    private Vector2 frameMoveInput;
    private bool frameIsSprinting;
    private bool frameIsMoving;
    private bool frameIsGrounded = true;
    private Vector3 externalVelocity;
    private Vector3 remoteTeleportHoldPos;
    private Quaternion remoteTeleportHoldRot = Quaternion.identity;
    private float remoteTeleportHoldTimer;
    private float jumpVelocity;
    private float crouchScaleRatio;
    private float standCheckDistance;
    private float standCheckRadius;
    private bool isCrouching;
    private bool isDead;
    private bool isGameplayScene;
    private bool hasInitializedGameplayPosition;
    private bool hasSearchedCrosshair;

    // Cache de parámetros existentes en el AnimatorController para 0 warnings y menos llamadas nativas
    private RuntimeAnimatorController cachedAnimatorController;
    private bool hasParamIsMoving;
    private bool hasParamIsSprinting;
    private bool hasParamIsGrounded;
    private bool hasParamIsCrouching;
    private bool hasParamSpeed;
    private bool hasParamVerticalVelocity;

    private readonly NetworkVariable<Vector3> netPosition = new NetworkVariable<Vector3>(
        writePerm: NetworkVariableWritePermission.Owner);

    private readonly NetworkVariable<Quaternion> netRotation = new NetworkVariable<Quaternion>(
        writePerm: NetworkVariableWritePermission.Owner);

    private readonly NetworkVariable<bool> netIsCrouching = new NetworkVariable<bool>(
        writePerm: NetworkVariableWritePermission.Owner);

    private readonly NetworkVariable<bool> netIsMoving = new NetworkVariable<bool>(
        writePerm: NetworkVariableWritePermission.Owner);

    private readonly NetworkVariable<bool> netIsSprinting = new NetworkVariable<bool>(
        writePerm: NetworkVariableWritePermission.Owner);

    private readonly NetworkVariable<bool> netIsGrounded = new NetworkVariable<bool>(
        writePerm: NetworkVariableWritePermission.Owner, value: true);

    private void Awake()
    {
        useGUILayout = false;
        selfTransform = transform;
        characterController = GetComponent<CharacterController>();

        if (characterController != null)
        {
            if (characterController.height > 0f)
            {
                standingHeight = characterController.height;
            }
            standingCenter = characterController.center;
        }

        // Resolver la cámara primero: la auto-detección del modelo en 1ra persona depende de ella
        if (cameraTransform == null)
        {
            Camera cam = GetComponentInChildren<Camera>(true);
            if (cam != null)
            {
                cameraTransform = cam.transform;
            }
        }

        if (thirdPersonMesh == null)
        {
            // Intentar detectar automáticamente el modelo/mesh en los hijos si no fue asignado en el Inspector
            Transform meshChild = transform.Find("Mesh");
            if (meshChild == null) meshChild = transform.Find("CatPlayer");

            if (meshChild != null)
            {
                thirdPersonMesh = meshChild;
            }
            else
            {
                SkinnedMeshRenderer smr = GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (smr != null)
                {
                    thirdPersonMesh = smr.transform;
                }
                else
                {
                    MeshRenderer mr = GetComponentInChildren<MeshRenderer>(true);
                    if (mr != null)
                    {
                        thirdPersonMesh = mr.transform;
                    }
                }
            }
        }

        if (thirdPersonAnimator == null && thirdPersonMesh != null)
        {
            thirdPersonAnimator = thirdPersonMesh.GetComponentInChildren<Animator>(true);
            if (thirdPersonAnimator == null)
            {
                thirdPersonAnimator = GetComponentInChildren<Animator>(true);
            }
        }
        CacheAnimatorParameters();

        if (firstPersonMesh == null && cameraTransform != null)
        {
            Transform fpChild = cameraTransform.Find("FirstPerson");
            if (fpChild == null) fpChild = cameraTransform.Find("Arms");
            if (fpChild == null) fpChild = transform.Find("FirstPerson");
            if (fpChild != null)
            {
                firstPersonMesh = fpChild;
            }
        }

        CacheRenderers();

        if (cameraTransform != null)
        {
            cachedCamera = cameraTransform.GetComponent<Camera>();
            cachedAudioListener = cameraTransform.GetComponent<AudioListener>();
            initialCameraLocalYaw = cameraTransform.localEulerAngles.y;
            initialCameraLocalPos = cameraTransform.localPosition;
            standingCameraY = initialCameraLocalPos.y;
            crouchCameraY = standingCameraY - ((standingHeight - crouchHeight) * 0.5f);
            currentCrouchCamY = standingCameraY;
            if (cachedCamera != null)
            {
                baseFov = cachedCamera.fieldOfView;
            }
        }

        cachedCanvases = GetComponentsInChildren<Canvas>(true);
        FindCrosshairUI();

        jumpVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        crouchScaleRatio = standingHeight > 0.01f ? (crouchHeight / standingHeight) : 0.55f;
        standCheckDistance = standingHeight - crouchHeight;
        standCheckRadius = characterController != null ? (characterController.radius * 0.9f) : 0.45f;

        // El estado de escena (input, cámara, controller) se aplica en OnEnable, que se ejecuta justo después
        isGameplayScene = IsGameplayScene(SceneManager.GetActiveScene().name);
        EnsureInputActionsInitialized();
    }

    private void EnsureInputActionsInitialized()
    {
        if (inputActions != null) return;

        inputActions = new InputSystem_Actions();
        var playerMap = inputActions.Player;
        moveAction = playerMap.Move;
        lookAction = playerMap.Look;
        jumpAction = playerMap.Jump;
        sprintAction = playerMap.Sprint;
        crouchAction = playerMap.Crouch;
    }

    private void EnableLocalInput()
    {
        EnsureInputActionsInitialized();
        inputActions?.Player.Enable();
    }

    private void DisableLocalInput()
    {
        inputActions?.Player.Disable();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // Posicionar instantáneamente en el spawn point en Frame 0 (antes de que se renderice ningún frame en (0,0,0))
        Transform initialSpawn = NetworkPlayerUtility.GetActiveSceneSpawnForClient(OwnerClientId);
        if (initialSpawn != null)
        {
            TeleportTo(initialSpawn.position, initialSpawn.rotation, enableController: isGameplayScene);
        }
        else if (IsOwner)
        {
            netPosition.Value = selfTransform.position;
            netRotation.Value = selfTransform.rotation;
        }

        NetworkPlayerUtility.NotifyPlayerSpawned();

        if (!isGameplayScene)
        {
            SetPlayerComponentsActive(false);
            DisableLocalInput();
        }
        else
        {
            SetPlayerComponentsActive(IsOwner);
            if (IsOwner)
            {
                EnableLocalInput();
            }
            else
            {
                DisableLocalInput();
            }
        }
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;

        // RefreshSceneState habilita/deshabilita el input local según la escena y la propiedad de red
        RefreshSceneState(SceneManager.GetActiveScene().name);
    }

    private void OnDisable()
    {
        DisableLocalInput();
        SceneManager.sceneLoaded -= OnSceneLoaded;

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        inputActions?.Dispose();
        inputActions = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        hasInitializedGameplayPosition = false;
        RefreshSceneState(scene.name);
    }

    private void RefreshSceneState(string sceneName)
    {
        isGameplayScene = IsGameplayScene(sceneName);
        isDead = false;

        if (!isGameplayScene)
        {
            isCrouching = false;
            selfTransform.localScale = Vector3.one;

            if (thirdPersonMesh != null)
            {
                thirdPersonMesh.localScale = Vector3.one;
            }

            if (firstPersonMesh != null)
            {
                firstPersonMesh.localScale = Vector3.one;
            }

            if (characterController != null)
            {
                characterController.height = standingHeight;
                characterController.center = standingCenter;
                characterController.enabled = false;
            }

            SetPlayerComponentsActive(false);
            DisableLocalInput();

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
        else
        {
            if (characterController != null)
            {
                characterController.enabled = true;
                characterController.height = standingHeight;
                characterController.center = standingCenter;
            }

            bool enableLocal = !IsSpawned || IsOwner;
            SetPlayerComponentsActive(enableLocal);

            if (enableLocal)
            {
                EnableLocalInput();
            }
            else
            {
                DisableLocalInput();
            }
        }
    }

    private bool IsGameplayScene(string sceneName)
    {
        if (nonGameplaySceneNames == null || nonGameplaySceneNames.Length == 0) return true;
        for (int i = 0; i < nonGameplaySceneNames.Length; i++)
        {
            if (string.Equals(sceneName, nonGameplaySceneNames[i], System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private void FindCrosshairUI()
    {
        // La búsqueda recorre la jerarquía y genera basura (GetComponentsInChildren):
        // se hace una sola vez aunque no exista crosshair, en lugar de repetirla cada frame.
        if (crosshairUI != null || hasSearchedCrosshair) return;
        hasSearchedCrosshair = true;

        if (cameraTransform != null)
        {
            Transform crosshairChild = cameraTransform.Find("Canvas/Crosshair");
            if (crosshairChild != null)
            {
                crosshairUI = crosshairChild.gameObject;
                return;
            }
        }

        if (cachedCanvases != null)
        {
            for (int i = 0; i < cachedCanvases.Length; i++)
            {
                if (cachedCanvases[i] == null) continue;
                Transform found = cachedCanvases[i].transform.Find("Crosshair");
                if (found != null)
                {
                    crosshairUI = found.gameObject;
                    return;
                }
            }
        }

        Transform[] allChildren = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < allChildren.Length; i++)
        {
            string childName = allChildren[i].name;
            if (childName.IndexOf("crosshair", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                childName.IndexOf("mira", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                childName.IndexOf("reticle", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                crosshairUI = allChildren[i].gameObject;
                return;
            }
        }
    }

    public void SetPlayerComponentsActive(bool active)
    {
        if (cachedCamera != null)
        {
            if (cachedCamera.enabled != active)
            {
                cachedCamera.enabled = active;
            }

            if (cachedCamera.gameObject != gameObject && cachedCamera.gameObject.activeSelf != active)
            {
                cachedCamera.gameObject.SetActive(active);
            }
        }

        if (cachedAudioListener != null && cachedAudioListener.enabled != active)
        {
            cachedAudioListener.enabled = active;
        }

        if (cachedCanvases != null)
        {
            for (int i = 0; i < cachedCanvases.Length; i++)
            {
                Canvas canvas = cachedCanvases[i];
                if (canvas == null) continue;

                if (canvas.transform.localScale == Vector3.zero)
                {
                    canvas.transform.localScale = Vector3.one;
                }

                if (canvas.enabled != active)
                {
                    canvas.enabled = active;
                }

                if (canvas.gameObject != gameObject && canvas.gameObject.activeSelf != active)
                {
                    canvas.gameObject.SetActive(active);
                }
            }
        }

        if (crosshairUI == null)
        {
            FindCrosshairUI();
        }

        if (crosshairUI != null && crosshairUI.activeSelf != active)
        {
            crosshairUI.SetActive(active);
        }

        UpdateVisualMeshVisibility();
    }

    private void Update()
    {
        if (!isGameplayScene)
        {
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            return;
        }

        if (isDead)
        {
            if (!IsSpawned || IsOwner)
            {
                HandleCursorLock();
            }
            return;
        }

        if (characterController != null && !characterController.enabled)
        {
            characterController.enabled = true;
        }

        if (IsSpawned && !IsOwner)
        {
            ApplyRemoteState();
            return;
        }

        if (crosshairUI == null)
        {
            FindCrosshairUI();
        }

        if (crosshairUI != null && !crosshairUI.activeSelf)
        {
            crosshairUI.SetActive(true);
        }

        if (!hasInitializedGameplayPosition)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            if (IsSpawned)
            {
                Transform startSpawn = NetworkPlayerUtility.GetActiveSceneSpawnForClient(OwnerClientId);
                if (startSpawn != null)
                {
                    TeleportTo(startSpawn.position, startSpawn.rotation, enableController: true);
                }
                else
                {
                    netPosition.Value = selfTransform.position;
                    netRotation.Value = selfTransform.rotation;
                }
                netIsCrouching.Value = false;
            }
            hasInitializedGameplayPosition = true;
        }

        HandleCursorLock();
        HandleLook();
        HandleCrouch();
        HandleMovementAndJump();
        UpdateCameraFeel();
        SyncExternalMeshTransform();

        if (IsSpawned)
        {
            SyncOwnerNetworkState();
        }

        UpdateThirdPersonAnimator();
    }

    private void SyncExternalMeshTransform()
    {
        // Si el mesh asignado no es hijo del Player (ej. está desanclado o en la raíz de la escena),
        // mantenemos su posición y rotación sincronizadas con el movimiento y la gravedad del CharacterController.
        if (isThirdPersonExternal && thirdPersonMesh != null)
        {
            thirdPersonMesh.SetPositionAndRotation(selfTransform.position, selfTransform.rotation);
        }

        if (isFirstPersonExternal && firstPersonMesh != null)
        {
            Transform refTarget = cameraTransform != null ? cameraTransform : selfTransform;
            firstPersonMesh.SetPositionAndRotation(refTarget.position, refTarget.rotation);
        }
    }

    public void UpdateVisualMeshVisibility()
    {
        if (cachedThirdPersonRenderers == null && thirdPersonMesh != null)
        {
            CacheRenderers();
        }

        bool isLocalOwner = !IsSpawned || IsOwner;

        if (!isGameplayScene)
        {
            // Escenas fuera de partida (Lobby, Menú): todos ven su modelo de 3ra persona completo
            if (thirdPersonMesh != null)
            {
                ShowThirdPersonMesh(ShadowCastingMode.On);
            }

            if (firstPersonMesh != null)
            {
                SetActiveIfNeeded(firstPersonMesh.gameObject, false);
            }
            return;
        }

        // En partida de gameplay
        if (isLocalOwner)
        {
            // Vista del jugador local (1ra persona): no debe verse su propio cuerpo estorbando la cámara
            if (thirdPersonMesh != null)
            {
                switch (localBodyVisibility)
                {
                    case LocalPlayerBodyVisibility.ShadowsOnly:
                        ShowThirdPersonMesh(ShadowCastingMode.ShadowsOnly);
                        break;

                    case LocalPlayerBodyVisibility.Hidden:
                        SetRenderersShadowMode(cachedThirdPersonRenderers, ShadowCastingMode.Off, false);
                        break;

                    case LocalPlayerBodyVisibility.Visible:
                        ShowThirdPersonMesh(ShadowCastingMode.On);
                        break;
                }
            }

            // Activar modelo de primera persona para el jugador local si está configurado
            if (firstPersonMesh != null)
            {
                SetActiveIfNeeded(firstPersonMesh.gameObject, true);
                SetRenderersShadowMode(cachedFirstPersonRenderers, ShadowCastingMode.On, true);
            }
        }
        else
        {
            // Vista remota (otros jugadores viendo a este personaje en el mapa)
            ShowThirdPersonMesh(ShadowCastingMode.On);

            // Los demás jugadores jamás deben ver las armas/manos en 1ra persona del otro jugador
            if (firstPersonMesh != null)
            {
                SetActiveIfNeeded(firstPersonMesh.gameObject, false);
            }
        }
    }

    private void ShowThirdPersonMesh(ShadowCastingMode mode)
    {
        if (thirdPersonMesh == null) return;

        SetActiveIfNeeded(thirdPersonMesh.gameObject, true);
        SetRenderersShadowMode(cachedThirdPersonRenderers, mode, true);
    }

    private static void SetActiveIfNeeded(GameObject obj, bool active)
    {
        if (obj.activeSelf != active)
        {
            obj.SetActive(active);
        }
    }

    private void CacheRenderers()
    {
        if (selfTransform == null) selfTransform = transform;

        if (thirdPersonMesh != null)
        {
            cachedThirdPersonRenderers = thirdPersonMesh.GetComponentsInChildren<Renderer>(true);
            bool isChild = thirdPersonMesh.IsChildOf(selfTransform);
            isThirdPersonExternal = !isChild;
            remoteScaleTarget = isChild ? thirdPersonMesh : selfTransform;
        }
        else
        {
            cachedThirdPersonRenderers = null;
            isThirdPersonExternal = false;
            remoteScaleTarget = selfTransform;
        }

        if (firstPersonMesh != null)
        {
            cachedFirstPersonRenderers = firstPersonMesh.GetComponentsInChildren<Renderer>(true);
            isFirstPersonExternal = !firstPersonMesh.IsChildOf(selfTransform);
        }
        else
        {
            cachedFirstPersonRenderers = null;
            isFirstPersonExternal = false;
        }
    }

    private static void SetRenderersShadowMode(Renderer[] renderers, ShadowCastingMode mode, bool enableRenderer)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null) continue;

            if (r.enabled != enableRenderer)
            {
                r.enabled = enableRenderer;
            }

            if (r.shadowCastingMode != mode)
            {
                r.shadowCastingMode = mode;
            }
        }
    }

    private void SyncOwnerNetworkState()
    {
        Vector3 currentPos = selfTransform.position;
        if ((netPosition.Value - currentPos).sqrMagnitude > 0.0001f)
        {
            netPosition.Value = currentPos;
        }

        Quaternion currentRot = selfTransform.rotation;
        // Reemplazo de Quaternion.Angle por producto punto para evitar Acos trigonométrico cada frame
        if (Mathf.Abs(Quaternion.Dot(netRotation.Value, currentRot)) < 0.99995f)
        {
            netRotation.Value = currentRot;
        }

        if (netIsCrouching.Value != isCrouching)
        {
            netIsCrouching.Value = isCrouching;
        }

        if (netIsMoving.Value != frameIsMoving)
        {
            netIsMoving.Value = frameIsMoving;
        }

        if (netIsSprinting.Value != frameIsSprinting)
        {
            netIsSprinting.Value = frameIsSprinting;
        }

        if (netIsGrounded.Value != frameIsGrounded)
        {
            netIsGrounded.Value = frameIsGrounded;
        }
    }

    private void HandleCursorLock()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            return;
        }

        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void HandleLook()
    {
        if (Cursor.lockState != CursorLockMode.Locked || lookAction == null)
        {
            return;
        }

        Vector2 lookInput = lookAction.ReadValue<Vector2>();
        if (lookInput.sqrMagnitude < 0.00001f)
        {
            return;
        }

        float mouseX = lookInput.x * mouseSensitivity;
        float mouseY = lookInput.y * mouseSensitivity;

        xRotation = Mathf.Clamp(xRotation - mouseY, -maxLookAngle, maxLookAngle);
        selfTransform.Rotate(0f, mouseX, 0f);
    }

    private void HandleCrouch()
    {
        if (characterController == null || crouchAction == null) return;

        bool wantsToCrouch = crouchAction.IsPressed();

        if (wantsToCrouch)
        {
            isCrouching = true;
        }
        else if (isCrouching && CanStandUp())
        {
            isCrouching = false;
        }

        float targetHeight = isCrouching ? crouchHeight : standingHeight;
        float currentHeight = characterController.height;
        float heightDiff = Mathf.Abs(currentHeight - targetHeight);

        if (heightDiff > 0.001f)
        {
            float dt = Mathf.Clamp01(Time.deltaTime * crouchTransitionSpeed);
            float newHeight = heightDiff < 0.005f ? targetHeight : Mathf.Lerp(currentHeight, targetHeight, dt);
            characterController.height = newHeight;

            float heightDifference = standingHeight - newHeight;
            characterController.center = new Vector3(standingCenter.x, standingCenter.y - (heightDifference * 0.5f), standingCenter.z);
        }

        float targetCamY = isCrouching ? crouchCameraY : standingCameraY;
        float camDiff = Mathf.Abs(currentCrouchCamY - targetCamY);
        if (camDiff > 0.001f)
        {
            float dt = Mathf.Clamp01(Time.deltaTime * crouchTransitionSpeed);
            currentCrouchCamY = camDiff < 0.005f ? targetCamY : Mathf.Lerp(currentCrouchCamY, targetCamY, dt);
        }
    }

    private bool CanStandUp()
    {
        Vector3 origin = selfTransform.position;
        origin.y += crouchHeight * 0.5f;

        return !Physics.SphereCast(origin, standCheckRadius, Vector3.up, out _, standCheckDistance, obstacleLayers, QueryTriggerInteraction.Ignore);
    }

    private void HandleMovementAndJump()
    {
        if (characterController == null) return;

        bool isGrounded = characterController.isGrounded;

        if (isGrounded && verticalVelocityY < 0f)
        {
            verticalVelocityY = groundedStickForce;
        }

        Vector3 moveDirection = Vector3.zero;
        frameMoveInput = Vector2.zero;

        if (moveAction != null)
        {
            Vector2 moveInput = moveAction.ReadValue<Vector2>();
            frameMoveInput = moveInput;

            // Fast-path: Si el usuario no está presionando teclas de movimiento, omitir proyecciones y raíces cuadradas
            if (moveInput.sqrMagnitude > 0.0001f)
            {
                Transform refTransform = selfTransform;
                Vector3 forward = refTransform.forward;
                forward.y = 0f;
                float fwdSqr = forward.sqrMagnitude;
                if (fwdSqr > 0.0001f)
                {
                    forward *= 1f / Mathf.Sqrt(fwdSqr);
                }

                Vector3 right = refTransform.right;
                right.y = 0f;
                float rightSqr = right.sqrMagnitude;
                if (rightSqr > 0.0001f)
                {
                    right *= 1f / Mathf.Sqrt(rightSqr);
                }

                moveDirection = right * moveInput.x + forward * moveInput.y;
                float moveSqr = moveDirection.sqrMagnitude;
                if (moveSqr > 1f)
                {
                    moveDirection *= 1f / Mathf.Sqrt(moveSqr);
                }
            }
        }

        bool isSprinting = sprintAction != null && sprintAction.IsPressed() && !isCrouching && frameMoveInput.sqrMagnitude > 0.001f;
        frameIsSprinting = isSprinting;
        float currentSpeed = isCrouching ? crouchSpeed : (isSprinting ? sprintSpeed : walkSpeed);

        if (jumpAction != null && jumpAction.WasPressedThisFrame() && isGrounded && !isCrouching)
        {
            verticalVelocityY = jumpVelocity;
            camImpactPitch = -jumpLandPitch * 0.6f;
        }

        float dt = Time.deltaTime;
        verticalVelocityY += gravity * dt;

        if (externalVelocity.sqrMagnitude > 0.0001f)
        {
            externalVelocity = Vector3.Lerp(externalVelocity, Vector3.zero, Mathf.Clamp01(dt * 9f));
        }
        else
        {
            externalVelocity = Vector3.zero;
        }

        Vector3 totalDisplacement = (moveDirection * currentSpeed + externalVelocity + Vector3.up * verticalVelocityY) * dt;
        CollisionFlags flags = characterController.Move(totalDisplacement);

        // Si choca con el techo durante un salto, anular la velocidad vertical hacia arriba
        if ((flags & CollisionFlags.Above) != 0 && verticalVelocityY > 0f)
        {
            verticalVelocityY = 0f;
        }

        // Actualizar flags cacheados una sola vez por frame tras mover el CharacterController
        frameIsGrounded = characterController.isGrounded;
        Vector3 vel = characterController.velocity;
        float horizSpeedSqr = vel.x * vel.x + vel.z * vel.z;
        frameIsMoving = horizSpeedSqr > 0.04f || (frameMoveInput.sqrMagnitude > 0.001f && horizSpeedSqr > 0.0025f);
        frameIsSprinting = frameIsMoving && !isCrouching && sprintAction != null && sprintAction.IsPressed();
    }

    private void UpdateCameraFeel()
    {
        if (cameraTransform == null) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        if (!enableCameraFeel)
        {
            cameraTransform.SetLocalPositionAndRotation(
                new Vector3(initialCameraLocalPos.x, currentCrouchCamY, initialCameraLocalPos.z),
                Quaternion.Euler(xRotation, initialCameraLocalYaw, 0f));

            if (cachedCamera != null && Mathf.Abs(cachedCamera.fieldOfView - baseFov) > 0.01f)
            {
                cachedCamera.fieldOfView = baseFov;
            }
            return;
        }

        bool grounded = frameIsGrounded;
        bool movingOnGround = grounded && frameIsMoving;

        // 1. Impacto de aterrizaje y salto (con umbral de tiempo/velocidad en el aire para ignorar costuras del suelo o rampas)
        if (!wasGroundedForCam && grounded)
        {
            if (airborneTime >= 0.08f && lastAirVelocityY < -3.2f)
            {
                float impact = Mathf.Clamp01((Mathf.Abs(lastAirVelocityY) - 2.5f) / 12f);
                camImpactY = Mathf.Min(camImpactY, -landCamDip * impact);
                camImpactPitch = Mathf.Max(camImpactPitch, jumpLandPitch * impact);
            }
            airborneTime = 0f;
            lastAirVelocityY = 0f;
        }
        else if (!grounded)
        {
            airborneTime += dt;
            if (verticalVelocityY < lastAirVelocityY)
            {
                lastAirVelocityY = verticalVelocityY;
            }
        }
        wasGroundedForCam = grounded;

        // Amortiguación de doble etapa: el objetivo decae hacia 0 y el valor actual lo sigue suavemente (sin saltos de 1 frame)
        float recoveryFactor = 1f - Mathf.Exp(-landRecoverySpeed * dt);
        float attackFactor = 1f - Mathf.Exp(-landRecoverySpeed * 3.2f * dt);
        camImpactY = Mathf.Lerp(camImpactY, 0f, recoveryFactor);
        camImpactPitch = Mathf.Lerp(camImpactPitch, 0f, recoveryFactor);
        currentCamImpactY = Mathf.Lerp(currentCamImpactY, camImpactY, attackFactor);
        currentCamImpactPitch = Mathf.Lerp(currentCamImpactPitch, camImpactPitch, attackFactor);

        // 2. Head Bob simétrico en forma de U (ambos pasos pisan abajo por igual, 2 rebotes verticales por ciclo lateral)
        float targetBobWeight = movingOnGround ? 1f : 0f;
        bobWeight = Mathf.MoveTowards(bobWeight, targetBobWeight, dt * cameraFeelSharpness);

        float bobX = 0f;
        float bobY = 0f;
        float bobRoll = 0f;

        if (bobWeight > 0.001f)
        {
            float stateMult = frameIsSprinting ? sprintBobMultiplier : (isCrouching ? 0.6f : 1f);
            bobPhase = MenuSharedUtility.WrapPhase(bobPhase + dt * bobFrequency * stateMult);

            float amp = bobWeight * stateMult;
            float s = Mathf.Sin(bobPhase);
            bobX = s * bobAmplitude.x * amp;
            // Identidad trigonométrica: -Cos(2x) = 2*Sin²(x) - 1 (rebote vertical simétrico en pie izquierdo y derecho)
            bobY = ((2f * s * s) - 1f) * bobAmplitude.y * amp;
            bobRoll = -s * 0.35f * amp;
        }
        else
        {
            bobPhase = 0f;
        }

        // 3. Strafe Tilt (inclinación lateral independiente del framerate al moverse con A/D)
        float feelFactor = 1f - Mathf.Exp(-cameraFeelSharpness * dt);
        float targetTilt = -frameMoveInput.x * strafeTiltAngle * (frameIsSprinting ? 1.25f : 1f);
        currentStrafeTilt = Mathf.Lerp(currentStrafeTilt, targetTilt, feelFactor);

        // 4. FOV dinámico (Sprint / Crouch)
        if (cachedCamera != null)
        {
            float targetFov = baseFov + (frameIsSprinting ? sprintFovBoost : (isCrouching ? crouchFovOffset : 0f));
            float currentFov = cachedCamera.fieldOfView;
            if (Mathf.Abs(currentFov - targetFov) > 0.01f)
            {
                float fovFactor = 1f - Mathf.Exp(-fovTransitionSpeed * dt);
                cachedCamera.fieldOfView = Mathf.Lerp(currentFov, targetFov, fovFactor);
            }
        }

        // 5. Aplicar posición y rotación en una sola llamada
        Vector3 finalPos = new Vector3(
            initialCameraLocalPos.x + bobX,
            currentCrouchCamY + bobY + currentCamImpactY,
            initialCameraLocalPos.z);

        float finalPitch = Mathf.Clamp(xRotation + currentCamImpactPitch, -maxLookAngle, maxLookAngle);
        Quaternion finalRot = Quaternion.Euler(finalPitch, initialCameraLocalYaw, currentStrafeTilt + bobRoll);

        cameraTransform.SetLocalPositionAndRotation(finalPos, finalRot);
    }

    /// <summary>
    /// Aplica un impulso físico de empuje (knockback) al jugador y una sacudida en la cámara.
    /// </summary>
    public void ApplyKnockback(Vector3 direction, float horizontalForce, float upwardForce = 3f)
    {
        direction.y = 0f;
        float sqr = direction.sqrMagnitude;
        if (sqr > 0.0001f)
        {
            direction *= 1f / Mathf.Sqrt(sqr);
        }

        externalVelocity += direction * horizontalForce;

        if (upwardForce > 0f && verticalVelocityY < upwardForce)
        {
            verticalVelocityY = upwardForce;
        }

        // Sacudida de cámara hacia atrás al recibir el impacto
        camImpactPitch = -jumpLandPitch * 1.3f;
        camImpactY = -landCamDip * 0.5f;
    }

    private void ResetCameraFeelState()
    {
        bobPhase = 0f;
        bobWeight = 0f;
        currentStrafeTilt = 0f;
        camImpactY = 0f;
        camImpactPitch = 0f;
        currentCamImpactY = 0f;
        currentCamImpactPitch = 0f;
        airborneTime = 0f;
        lastAirVelocityY = 0f;
        wasGroundedForCam = true;
    }

    /// <summary>
    /// Teletransporta al jugador de forma segura reiniciando inercias y sincronizando el estado de red si es Owner.
    /// </summary>
    public void TeleportTo(Vector3 position, Quaternion rotation, bool enableController)
    {
        if (selfTransform == null) selfTransform = transform;

        externalVelocity = Vector3.zero;
        verticalVelocityY = 0f;
        ResetCameraFeelState();

        if (characterController != null)
        {
            characterController.enabled = false;
            selfTransform.SetPositionAndRotation(position, rotation);
            characterController.enabled = enableController;
        }
        else
        {
            selfTransform.SetPositionAndRotation(position, rotation);
        }

        SyncExternalMeshTransform();

        if (IsSpawned)
        {
            if (IsOwner)
            {
                netPosition.Value = position;
                netRotation.Value = rotation;
            }
            else
            {
                // Mantener al jugador remoto fijo en el spawn mientras su cliente termina de cargar escena y sincroniza netPosition
                remoteTeleportHoldPos = position;
                remoteTeleportHoldRot = rotation;
                remoteTeleportHoldTimer = 2.5f;
            }
        }
    }

    /// <summary>
    /// Activa o desactiva el estado de muerte (deshabilita colisión, movimiento y oculta los modelos hasta el respawn).
    /// </summary>
    public void SetDeadState(bool dead)
    {
        isDead = dead;
        externalVelocity = Vector3.zero;
        verticalVelocityY = 0f;
        frameIsMoving = false;
        frameIsSprinting = false;
        frameMoveInput = Vector2.zero;
        ResetCameraFeelState();

        if (IsSpawned && IsOwner)
        {
            if (netIsMoving.Value) netIsMoving.Value = false;
            if (netIsSprinting.Value) netIsSprinting.Value = false;
        }

        if (dead)
        {
            UpdateThirdPersonAnimator();

            if (characterController != null)
            {
                characterController.enabled = false;
            }

            if (thirdPersonMesh != null)
            {
                SetRenderersShadowMode(cachedThirdPersonRenderers, ShadowCastingMode.Off, false);
            }

            if (firstPersonMesh != null)
            {
                SetActiveIfNeeded(firstPersonMesh.gameObject, false);
            }

            if (crosshairUI != null && crosshairUI.activeSelf)
            {
                crosshairUI.SetActive(false);
            }
        }
        else
        {
            isCrouching = false;
            currentCrouchCamY = standingCameraY;

            if (IsSpawned && IsOwner && netIsCrouching.Value)
            {
                netIsCrouching.Value = false;
            }

            if (characterController != null)
            {
                characterController.height = standingHeight;
                characterController.center = standingCenter;
                characterController.enabled = isGameplayScene;
            }

            UpdateVisualMeshVisibility();

            bool isLocalOwner = !IsSpawned || IsOwner;
            if (isGameplayScene && isLocalOwner && crosshairUI != null && !crosshairUI.activeSelf)
            {
                crosshairUI.SetActive(true);
            }
        }
    }

    private void ApplyRemoteState()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 targetPos = netPosition.Value;
        Quaternion targetRot = netRotation.Value;

        // Si acabamos de posicionar este jugador remoto en su spawn local, ignorar posiciones viejas
        // de la escena anterior hasta que el Owner sincronice su nueva posición cercana al spawn
        if (remoteTeleportHoldTimer > 0f)
        {
            remoteTeleportHoldTimer -= dt;
            if ((targetPos - remoteTeleportHoldPos).sqrMagnitude <= 2.25f)
            {
                remoteTeleportHoldTimer = 0f;
            }
            else
            {
                targetPos = remoteTeleportHoldPos;
                targetRot = remoteTeleportHoldRot;
            }
        }
        else if (targetPos.sqrMagnitude < 0.0001f)
        {
            // Evitar arrastrar al jugador hacia (0,0,0) si su NetworkVariable aún no recibió el primer valor del Owner
            SyncExternalMeshTransform();
            UpdateThirdPersonAnimator();
            return;
        }

        Vector3 currentPos = selfTransform.position;
        Quaternion currentRot = selfTransform.rotation;
        float distSqr = (currentPos - targetPos).sqrMagnitude;

        if (distSqr > 6.25f)
        {
            // Si la distancia es mayor a 2.5m (spawn inicial o teletransporte), hacer snap instantáneo sin deslizarlo por el mapa
            selfTransform.SetPositionAndRotation(targetPos, targetRot);
            verticalVelocityY = 0f;
        }
        else if (distSqr > 0.0001f || Mathf.Abs(Quaternion.Dot(currentRot, targetRot)) < 0.99995f)
        {
            float lerpFactor = Mathf.Clamp01(dt * networkInterpolationSpeed);
            Vector3 nextPos = Vector3.Lerp(currentPos, targetPos, lerpFactor);
            Quaternion nextRot = Quaternion.Slerp(currentRot, targetRot, lerpFactor);
            verticalVelocityY = (nextPos.y - currentPos.y) / dt;
            selfTransform.SetPositionAndRotation(nextPos, nextRot);
        }
        else
        {
            verticalVelocityY = 0f;
        }

        bool remoteCrouching = netIsCrouching.Value;
        float targetScaleY = remoteCrouching ? crouchScaleRatio : 1f;
        Transform visualTarget = remoteScaleTarget != null ? remoteScaleTarget : selfTransform;
        Vector3 currentScale = visualTarget.localScale;
        if (Mathf.Abs(currentScale.y - targetScaleY) > 0.001f)
        {
            float dtFactor = Mathf.Clamp01(dt * crouchTransitionSpeed);
            currentScale.y = Mathf.Lerp(currentScale.y, targetScaleY, dtFactor);
            visualTarget.localScale = currentScale;
        }

        // Sincronizar también la altura del CharacterController remoto para que el hitbox coincida al agacharse
        if (characterController != null && visualTarget != selfTransform)
        {
            float targetHeight = remoteCrouching ? crouchHeight : standingHeight;
            float currentHeight = characterController.height;
            float heightDiff = Mathf.Abs(currentHeight - targetHeight);
            if (heightDiff > 0.001f)
            {
                float dtFactor = Mathf.Clamp01(dt * crouchTransitionSpeed);
                float newHeight = heightDiff < 0.005f ? targetHeight : Mathf.Lerp(currentHeight, targetHeight, dtFactor);
                characterController.height = newHeight;
                float heightDifference = standingHeight - newHeight;
                characterController.center = new Vector3(standingCenter.x, standingCenter.y - (heightDifference * 0.5f), standingCenter.z);
            }
        }

        SyncExternalMeshTransform();
        UpdateThirdPersonAnimator();
    }

    private static readonly int AnimHashIsMoving = Animator.StringToHash("IsMoving");
    private static readonly int AnimHashIsSprinting = Animator.StringToHash("IsSprinting");
    private static readonly int AnimHashIsGrounded = Animator.StringToHash("IsGrounded");
    private static readonly int AnimHashIsCrouching = Animator.StringToHash("IsCrouching");
    private static readonly int AnimHashSpeed = Animator.StringToHash("Speed");
    private static readonly int AnimHashVerticalVelocity = Animator.StringToHash("VerticalVelocity");

    private void CacheAnimatorParameters()
    {
        hasParamIsMoving = false;
        hasParamIsSprinting = false;
        hasParamIsGrounded = false;
        hasParamIsCrouching = false;
        hasParamSpeed = false;
        hasParamVerticalVelocity = false;

        if (thirdPersonAnimator == null)
        {
            cachedAnimatorController = null;
            return;
        }

        cachedAnimatorController = thirdPersonAnimator.runtimeAnimatorController;
        if (cachedAnimatorController == null) return;

        AnimatorControllerParameter[] parameters = thirdPersonAnimator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            int hash = parameters[i].nameHash;
            if (hash == AnimHashIsMoving) hasParamIsMoving = true;
            else if (hash == AnimHashIsSprinting) hasParamIsSprinting = true;
            else if (hash == AnimHashIsGrounded) hasParamIsGrounded = true;
            else if (hash == AnimHashIsCrouching) hasParamIsCrouching = true;
            else if (hash == AnimHashSpeed) hasParamSpeed = true;
            else if (hash == AnimHashVerticalVelocity) hasParamVerticalVelocity = true;
        }
    }

    private void UpdateThirdPersonAnimator()
    {
        if (thirdPersonAnimator == null || !thirdPersonAnimator.isActiveAndEnabled) return;

        RuntimeAnimatorController currentController = thirdPersonAnimator.runtimeAnimatorController;
        if (currentController == null) return;
        if (currentController != cachedAnimatorController)
        {
            CacheAnimatorParameters();
        }

        bool moving = IsMoving;
        if (hasParamIsMoving) thirdPersonAnimator.SetBool(AnimHashIsMoving, moving);
        if (hasParamIsSprinting) thirdPersonAnimator.SetBool(AnimHashIsSprinting, IsSprinting);
        if (hasParamIsGrounded) thirdPersonAnimator.SetBool(AnimHashIsGrounded, IsGrounded);
        if (hasParamIsCrouching) thirdPersonAnimator.SetBool(AnimHashIsCrouching, IsCrouching);
        if (hasParamSpeed) thirdPersonAnimator.SetFloat(AnimHashSpeed, moving ? CurrentSpeed : 0f);
        if (hasParamVerticalVelocity) thirdPersonAnimator.SetFloat(AnimHashVerticalVelocity, verticalVelocityY);
    }
}
