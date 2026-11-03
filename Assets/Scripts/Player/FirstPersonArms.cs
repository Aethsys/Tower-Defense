using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

#pragma warning disable CS0649
/// <summary>
/// Gestiona qué está equipado en el brazo izquierdo y derecho, calcula el movimiento compartido
/// (caminar, salto, respiración, inercia del mouse) y se lo entrega a cada objeto equipado.
/// La animación de cada brazo la maneja su propio script (<see cref="Arms"/>, configurable como izquierdo o derecho).
/// </summary>
[DisallowMultipleComponent]
[SelectionBase]
public class FirstPersonArms : MonoBehaviour
{
    public enum PunchMode
    {
        None = 0,
        LeftArm = 1,
        RightArm = 2,
        BothArms = 3
    }

    [Header("Equipped")]
    [Tooltip("Objeto equipado en el brazo izquierdo (Click Izquierdo). Se auto-detecta si se deja vacío.")]
    [SerializeField] private Arms leftArm;

    [Tooltip("Objeto equipado en el brazo derecho (Click Derecho). Se auto-detecta si se deja vacío.")]
    [SerializeField] private Arms rightArm;

    [Tooltip("Invierte qué brazo ataca con cada click.")]
    [SerializeField] private bool swapAttackClicks;

    [Header("Player Reference")]
    [Tooltip("Referencia a PlayerMovement (se auto-detecta en el padre si se deja vacío).")]
    [SerializeField] private PlayerMovement playerMovement;

    [Header("Walk & Sprint Motion")]
    [Tooltip("Velocidad del ciclo de pasos al caminar.")]
    [SerializeField] private float walkFrequency = 9f;

    [Tooltip("Multiplicador de velocidad y amplitud al correr (Sprint).")]
    [SerializeField] private float sprintMultiplier = 1.35f;

    [Tooltip("Rapidez de transición entre reposo y caminata.")]
    [SerializeField] private float transitionSpeed = 10f;

    [Header("Jump & Land Motion")]
    [Tooltip("Impulso vertical de los brazos al saltar y flotar en el aire.")]
    [SerializeField] private float jumpMotionAmount = 0.05f;

    [Tooltip("Compresión máxima de los brazos hacia abajo al aterrizar.")]
    [SerializeField] private float landImpactAmount = 0.09f;

    [Tooltip("Velocidad de recuperación tras saltar o aterrizar.")]
    [SerializeField] private float jumpRecoverySpeed = 9f;

    [Header("Idle & Mouse Sway")]
    [Tooltip("Velocidad de respiración en reposo.")]
    [SerializeField] private float idleSpeed = 1.6f;

    [Tooltip("Amplitud vertical de respiración en reposo.")]
    [SerializeField] private float idleAmount = 0.007f;

    [Tooltip("Inercia de los brazos al mover el mouse.")]
    [SerializeField] private float mouseSwayAmount = 0.012f;

    [Tooltip("Límite máximo de desplazamiento por inercia del mouse.")]
    [SerializeField] private float maxMouseSway = 0.04f;

    [Header("Punch (Left, Right & Both)")]
    [Tooltip("Duración total de la animación del golpe en segundos.")]
    [SerializeField] private float punchDuration = 0.22f;

    [Tooltip("Cooldown tras finalizar un golpe (aplica a ambos brazos) antes de poder lanzar el siguiente.")]
    [SerializeField] private float punchDelay = 0.15f;

    [Header("Crosshair Cooldown")]
    [Tooltip("Muestra un anillo alrededor del crosshair que se llena mientras los puños están en cooldown.")]
    [SerializeField] private bool showCooldownOnCrosshair = true;

    [Tooltip("Imagen radial opcional. Si se deja vacía, se crea automáticamente como hijo del crosshair.")]
    [SerializeField] private Image cooldownImage;

    [Tooltip("Tamaño en píxeles del anillo generado automáticamente.")]
    [SerializeField] private float cooldownRingSize = 48f;

    [Tooltip("Color del anillo mientras se recarga.")]
    [SerializeField] private Color cooldownColor = new Color(1f, 1f, 1f, 0.85f);

    // Ventana de tolerancia (50 ms) para detectar cuando el jugador presiona ambos clicks al mismo tiempo
    private const float DualClickGraceWindow = 0.05f;

    // Estado de golpe compartido (3 modos + cooldown común a ambos brazos)
    private PunchMode currentPunchMode = PunchMode.None;
    private float punchTimer;
    private float delayTimer;

    // Estado de locomoción
    private float walkPhase;
    private float breathPhase;
    private float walkWeight;

    // Estado de salto y aterrizaje
    private bool wasGrounded = true;
    private float lastAirVelocityY;
    private float jumpRecoilY;
    private float currentAirY;

    // Inercia del mouse y bloqueo de cursor
    private Vector2 currentSway;
    private bool wasCursorLockedLastFrame;

    // Contexto compartido del frame
    private ArmContext ctx;
    private bool frameGrounded = true;

    public Arms LeftArm => leftArm;
    public Arms RightArm => rightArm;

    public PunchMode CurrentPunch => currentPunchMode;
    public bool IsLeftPunching => currentPunchMode == PunchMode.LeftArm || currentPunchMode == PunchMode.BothArms;
    public bool IsRightPunching => currentPunchMode == PunchMode.RightArm || currentPunchMode == PunchMode.BothArms;

    /// <summary>
    /// Progreso del cooldown de los puños: 0 = recién golpeado, 1 = listo para golpear.
    /// Es común a ambos brazos sin importar con cuál (o ambos) se golpeó.
    /// </summary>
    public float CooldownProgress
    {
        get
        {
            float total = punchDuration + punchDelay;
            if (total <= 0.0001f) return 1f;

            if (currentPunchMode != PunchMode.None) return Mathf.Clamp01(punchTimer / total);
            if (delayTimer > 0f) return Mathf.Clamp01((punchDuration + (punchDelay - delayTimer)) / total);
            return 1f;
        }
    }

    public void EquipLeft(Arms arm)
    {
        if (leftArm == arm) return;
        if (leftArm != null) leftArm.ResetToDefault();
        leftArm = arm;
        DuplicateIfSharedSlot();
        if (leftArm != null)
        {
            leftArm.SetOwner(playerMovement);
            leftArm.CacheDefaults();
        }
    }

    public void EquipRight(Arms arm)
    {
        if (rightArm == arm) return;
        if (rightArm != null) rightArm.ResetToDefault();
        rightArm = arm;
        DuplicateIfSharedSlot();
        if (rightArm != null)
        {
            rightArm.SetOwner(playerMovement);
            rightArm.CacheDefaults();
        }
    }

    private void Awake()
    {
        useGUILayout = false;
        AutoDetectReferences();
        DuplicateIfSharedSlot();
        if (leftArm != null) leftArm.SetOwner(playerMovement);
        if (rightArm != null) rightArm.SetOwner(playerMovement);
    }

    private void Reset()
    {
        AutoDetectReferences();
    }

    private static bool IsLeftName(string n)
    {
        return n.IndexOf(".l", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
               n.IndexOf("_l", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
               n.IndexOf("left", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
               n.IndexOf("izq", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsRightName(string n)
    {
        return n.IndexOf(".r", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
               n.IndexOf("_r", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
               n.IndexOf("right", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
               n.IndexOf("der", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void AutoDetectReferences()
    {
        if (playerMovement == null)
        {
            playerMovement = GetComponentInParent<PlayerMovement>(true);
            if (playerMovement == null)
            {
                playerMovement = FindAnyObjectByType<PlayerMovement>();
            }
        }

        if (leftArm == null || rightArm == null)
        {
            Arms[] foundArms = GetComponentsInChildren<Arms>(true);
            // Primero asignar por coincidencia explícita de nombre (evita invertir brazos si Right está antes en la jerarquía)
            for (int i = 0; i < foundArms.Length; i++)
            {
                Arms found = foundArms[i];
                if (leftArm == null && found != rightArm && IsLeftName(found.name)) leftArm = found;
                else if (rightArm == null && found != leftArm && IsRightName(found.name)) rightArm = found;
            }
            // Respaldo por orden de aparición si los nombres no especifican lado
            for (int i = 0; i < foundArms.Length; i++)
            {
                Arms found = foundArms[i];
                if (leftArm == null && found != rightArm) leftArm = found;
                else if (rightArm == null && found != leftArm) rightArm = found;
            }
        }

        if (leftArm != null && rightArm != null) return;

        // Migración: si no hay scripts de brazo, se buscan los transforms por nombre y se les agrega el script.
        Transform leftT = null;
        Transform rightT = null;
        Transform[] children = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            Transform t = children[i];
            if (t == transform) continue;

            string n = t.name;
            if (leftArm == null && leftT == null && IsLeftName(n))
            {
                leftT = t;
            }
            else if (rightArm == null && rightT == null && IsRightName(n))
            {
                rightT = t;
            }
        }

        if (leftArm == null && rightArm == null && leftT == null && rightT == null && transform.childCount >= 2)
        {
            leftT = transform.GetChild(0);
            rightT = transform.GetChild(1);
        }

        if (leftArm == null && leftT != null) leftArm = AddArmScript(leftT);
        if (rightArm == null && rightT != null) rightArm = AddArmScript(rightT);
    }

    /// <summary>
    /// Si el mismo objeto está en ambos slots, crea una sola vez una copia espejada (eje X) para el derecho.
    /// No cuesta nada por frame: es un único Instantiate al iniciar o equipar.
    /// </summary>
    private void DuplicateIfSharedSlot()
    {
        if (leftArm == null || leftArm != rightArm) return;

        Arms source = leftArm;

        // Si el objeto es un asset de prefab (no está en la escena), el clon se crea como hijo de este objeto
        Transform parent = source.gameObject.scene.IsValid() ? source.transform.parent : transform;
        Arms clone = Instantiate(source, parent, false);
        clone.gameObject.SetActive(true);
        clone.name = source.name + " (R)";

        Transform ct = clone.transform;
        Vector3 pos = source.transform.localPosition;
        Quaternion rot = source.transform.localRotation;
        Vector3 scale = source.transform.localScale;

        ct.localPosition = new Vector3(-pos.x, pos.y, pos.z);
        ct.localRotation = new Quaternion(rot.x, -rot.y, -rot.z, rot.w);
        ct.localScale = new Vector3(-scale.x, scale.y, scale.z);

        clone.SetOwner(playerMovement);
        clone.CacheDefaults();
        rightArm = clone;
    }

    private static Arms AddArmScript(Transform t)
    {
        if (!t.TryGetComponent(out Arms arm))
        {
#if UNITY_EDITOR
            arm = !Application.isPlaying ? UnityEditor.Undo.AddComponent<Arms>(t.gameObject) : t.gameObject.AddComponent<Arms>();
#else
            arm = t.gameObject.AddComponent<Arms>();
#endif
        }

        return arm;
    }

    private void Update()
    {
        if (playerMovement != null)
        {
            if (playerMovement.IsSpawned && !playerMovement.IsOwner) return;
            if (!playerMovement.IsGameplaySceneActive || playerMovement.IsDead)
            {
                wasCursorLockedLastFrame = false;
                return;
            }
        }

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // Ignorar el click del mismo frame en que se re-bloquea el cursor tras haber estado en pausa/desbloqueado
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        Mouse mouse = (locked && wasCursorLockedLastFrame) ? Mouse.current : null;
        wasCursorLockedLastFrame = locked;

        UpdateJumpState(dt);
        UpdateSwayState(dt, mouse);
        UpdateLocomotion(dt);
        UpdatePunchState(dt, mouse);

        float punchProgress = punchDuration > 0.0001f ? Mathf.Clamp01(punchTimer / punchDuration) : 1f;
        if (leftArm != null && leftArm.isActiveAndEnabled) leftArm.Tick(in ctx, Arms.ArmSide.Left, IsLeftPunching, punchProgress);
        if (rightArm != null && rightArm.isActiveAndEnabled) rightArm.Tick(in ctx, Arms.ArmSide.Right, IsRightPunching, punchProgress);

        UpdateCooldownIndicator();
    }

    private void UpdatePunchState(float dt, Mouse mouse)
    {
        if (delayTimer > 0f) delayTimer -= dt;

        bool leftClick = false;
        bool rightClick = false;

        if (mouse != null)
        {
            leftClick = mouse.leftButton.wasPressedThisFrame;
            rightClick = mouse.rightButton.wasPressedThisFrame;

            if (swapAttackClicks)
            {
                (leftClick, rightClick) = (rightClick, leftClick);
            }
        }

        // Los brazos no equipados no pueden golpear
        if (leftArm == null) leftClick = false;
        if (rightArm == null) rightClick = false;

        if (currentPunchMode != PunchMode.None)
        {
            // Si el otro click llega dentro de la ventana de gracia, el golpe pasa a ser con ambos brazos
            if (punchTimer <= DualClickGraceWindow)
            {
                if ((currentPunchMode == PunchMode.LeftArm && rightClick) ||
                    (currentPunchMode == PunchMode.RightArm && leftClick))
                {
                    currentPunchMode = PunchMode.BothArms;
                }
            }

            punchTimer += dt;
            if (punchTimer >= punchDuration)
            {
                currentPunchMode = PunchMode.None;
                punchTimer = 0f;
                delayTimer = punchDelay;
            }
            return;
        }

        if (delayTimer <= 0f)
        {
            if (leftClick && rightClick) StartPunch(PunchMode.BothArms);
            else if (leftClick) StartPunch(PunchMode.LeftArm);
            else if (rightClick) StartPunch(PunchMode.RightArm);
        }
    }

    private void StartPunch(PunchMode mode)
    {
        currentPunchMode = mode;
        punchTimer = 0f;
    }

    private static Sprite ringSprite;

    private void UpdateCooldownIndicator()
    {
        if (!showCooldownOnCrosshair) return;

        // En reposo y con el anillo ya oculto no hay nada que actualizar
        if (cooldownImage != null && !cooldownImage.enabled && currentPunchMode == PunchMode.None && delayTimer <= 0f) return;

        if (cooldownImage == null)
        {
            // El crosshair puede resolverse tras el primer frame: reintentar hasta encontrarlo
            GameObject crosshair = playerMovement != null ? playerMovement.CrosshairUI : null;
            if (crosshair == null || !crosshair.TryGetComponent(out RectTransform crosshairRect)) return;

            cooldownImage = CreateCooldownRing(crosshairRect);
        }

        float progress = CooldownProgress;
        bool cooling = progress < 0.999f;

        if (cooldownImage.enabled != cooling)
        {
            cooldownImage.enabled = cooling;
        }

        if (cooling)
        {
            cooldownImage.fillAmount = progress;
        }
    }

    private Image CreateCooldownRing(RectTransform parent)
    {
        var obj = new GameObject("PunchCooldownRing", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var rect = (RectTransform)obj.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(cooldownRingSize, cooldownRingSize);

        var img = obj.GetComponent<Image>();
        img.sprite = GetRingSprite();
        img.type = Image.Type.Filled;
        img.fillMethod = Image.FillMethod.Radial360;
        img.fillOrigin = (int)Image.Origin360.Top;
        img.fillClockwise = true;
        img.color = cooldownColor;
        img.raycastTarget = false;
        img.enabled = false;
        return img;
    }

    private static Sprite GetRingSprite()
    {
        if (ringSprite != null) return ringSprite;

        const int size = 64;
        const float outer = size * 0.5f - 1f;
        const float inner = outer - 4f;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };

        var pixels = new Color32[size * size];
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float a = Mathf.Clamp01(outer - d + 0.5f) * Mathf.Clamp01(d - inner + 0.5f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        ringSprite.hideFlags = HideFlags.HideAndDontSave;
        return ringSprite;
    }

    private void UpdateJumpState(float dt)
    {
        bool isGrounded = playerMovement == null || playerMovement.IsGrounded;
        float vertVel = playerMovement != null ? playerMovement.VerticalVelocity : 0f;
        frameGrounded = isGrounded;

        if (wasGrounded && !isGrounded && vertVel > 0.5f)
        {
            jumpRecoilY = -jumpMotionAmount;
        }
        else if (!wasGrounded && isGrounded)
        {
            float fallImpact = Mathf.Clamp(Mathf.Abs(lastAirVelocityY) * 0.007f, jumpMotionAmount * 0.5f, landImpactAmount);
            jumpRecoilY = -fallImpact;
        }

        if (!isGrounded)
        {
            lastAirVelocityY = vertVel;
        }

        wasGrounded = isGrounded;

        float targetAirY = isGrounded ? 0f : Mathf.Clamp(-vertVel * 0.004f, -jumpMotionAmount, jumpMotionAmount);
        currentAirY = Mathf.Lerp(currentAirY, targetAirY, dt * transitionSpeed);
        jumpRecoilY = Mathf.Lerp(jumpRecoilY, 0f, dt * jumpRecoverySpeed);

        ctx.JumpY = currentAirY + jumpRecoilY;
        ctx.JumpPitch = -ctx.JumpY * 80f;
    }

    private void UpdateSwayState(float dt, Mouse mouse)
    {
        Vector2 targetSway = Vector2.zero;

        if (mouse != null)
        {
            Vector2 delta = mouse.delta.ReadValue() * (0.1f * mouseSwayAmount);
            targetSway.x = Mathf.Clamp(-delta.x, -maxMouseSway, maxMouseSway);
            targetSway.y = Mathf.Clamp(-delta.y, -maxMouseSway, maxMouseSway);
        }

        currentSway = Vector2.Lerp(currentSway, targetSway, dt * 12f);
        ctx.Sway = currentSway;
        ctx.SwayRotX = currentSway.y * 60f;
        ctx.SwayRotY = -currentSway.x * 60f;
    }

    private void UpdateLocomotion(float dt)
    {
        bool isMoving = playerMovement != null && playerMovement.IsMoving;
        bool isGrounded = frameGrounded;
        bool isSprinting = playerMovement != null && playerMovement.IsSprinting;
        bool isCrouching = playerMovement != null && playerMovement.IsCrouching;

        float targetWeight = (isMoving && isGrounded) ? 1f : 0f;
        walkWeight = Mathf.MoveTowards(walkWeight, targetWeight, dt * transitionSpeed);

        float stateMult = isSprinting ? sprintMultiplier : (isCrouching ? 0.65f : 1f);
        ctx.WalkAmp = walkWeight * stateMult;

        if (ctx.WalkAmp > 0.001f)
        {
            walkPhase = MenuSharedUtility.WrapPhase(walkPhase + dt * walkFrequency * stateMult);

            float s = Mathf.Sin(walkPhase);
            ctx.WalkSwing = s;
            // Identidad trigonométrica: -Cos(2x) = 2*Sin²(x) - 1 (evita una llamada extra a Mathf.Cos por frame)
            ctx.WalkBounce = (2f * s * s) - 1f;
        }
        else
        {
            ctx.WalkSwing = 0f;
            ctx.WalkBounce = 0f;
        }

        breathPhase = MenuSharedUtility.WrapPhase(breathPhase + dt * idleSpeed);
        float breath = Mathf.Sin(breathPhase);
        ctx.BreathY = breath * idleAmount;
        ctx.BreathPitch = -breath * 0.8f;
    }

    private void OnDisable()
    {
        wasCursorLockedLastFrame = false;
        if (cooldownImage != null && cooldownImage.enabled)
        {
            cooldownImage.enabled = false;
        }
    }

    [ContextMenu("Reset Arms To Default")]
    public void ResetArmsToDefault()
    {
        currentPunchMode = PunchMode.None;
        punchTimer = 0f;
        delayTimer = 0f;
        if (cooldownImage != null && cooldownImage.enabled)
        {
            cooldownImage.enabled = false;
        }
        if (leftArm != null) leftArm.ResetToDefault();
        if (rightArm != null) rightArm.ResetToDefault();
    }
}
