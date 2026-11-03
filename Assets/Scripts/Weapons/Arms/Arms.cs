using UnityEngine;

/// <summary>
/// Datos de locomoción compartidos que FirstPersonArms calcula cada frame y entrega a los brazos equipados.
/// </summary>
public struct ArmContext
{
    public float WalkSwing;
    public float WalkBounce;
    public float WalkAmp;
    public float BreathY;
    public float BreathPitch;
    public float JumpY;
    public float JumpPitch;
    public float SwayRotX;
    public float SwayRotY;
    public Vector2 Sway;
}

/// <summary>
/// Objeto que se equipa en un brazo. Cada objeto maneja su propia animación
/// (locomoción + golpe) y su detección de daño; FirstPersonArms solo decide qué está equipado y le pasa el contexto.
/// </summary>
public class Arms : MonoBehaviour
{
    public enum ArmSide
    {
        Left = 0,
        Right = 1
    }

    [Header("Walk Motion")]
    [Tooltip("Amplitud del braceo al caminar: X (cierre lateral), Y (arco vertical), Z (adelante/atrás).")]
    [SerializeField] protected Vector3 walkSwing = new Vector3(0.025f, 0.045f, 0.085f);

    [Tooltip("Ángulo de inclinación vertical (Pitch) del brazo al bracear.")]
    [SerializeField] protected float walkPitchAngle = 8f;

    [Header("Punch Animation")]
    [Tooltip("Distancia que avanza el puño hacia adelante (+Z).")]
    [SerializeField] protected float punchDistance = 0.38f;

    [Tooltip("Desplazamiento del puño hacia el centro de la pantalla (X).")]
    [SerializeField] protected float punchInward = 0.12f;

    [Tooltip("Elevación ligera del puño al golpear (+Y).")]
    [SerializeField] protected float punchVertical = 0.04f;

    [Tooltip("Rotación e inclinación del puño al extenderse.")]
    [SerializeField] protected float punchTiltAngle = 12f;

    [Header("Combat & Damage")]
    [Tooltip("Daño que inflige este brazo al impactar.")]
    [SerializeField] protected float punchDamage = 15f;

    [Tooltip("Fuerza de empuje (knockback) que aplica este brazo al golpear.")]
    [SerializeField] protected float punchKnockback = 6.5f;

    [Tooltip("Alcance máximo del golpe desde la cámara.")]
    [SerializeField] protected float attackRange = 2.5f;

    [Tooltip("Radio del SphereCast del puñetazo (0 = Raycast exacto).")]
    [SerializeField] protected float attackRadius = 0.35f;

    [Tooltip("Capas que pueden recibir el impacto del golpe.")]
    [SerializeField] protected LayerMask hitLayers = ~0;

    // Buffers estáticos preasignados para 0 GC Alloc en cada golpe
    private static readonly RaycastHit[] hitBuffer = new RaycastHit[16];
    private static readonly Collider[] overlapBuffer = new Collider[16];
    private static int lastPhysicsSyncFrame = -1;

    private Vector3 defaultPos;
    private Quaternion defaultRot;
    private float inwardSign = 1f;
    private bool cached;
    private Transform cachedTransform;
    private Transform ownerRoot;
    private Transform attackOrigin;

    private bool isPunching;
    private bool hasDealtDamageInCurrentPunch;
    private float punchWeight;

    /// <summary>+1 si el brazo bracea en fase, -1 si en contrafase (los brazos se mueven opuestos).</summary>
    private float swingDirection = 1f;

    public bool IsPunching => isPunching;
    public float PunchDamage => punchDamage;
    public float AttackRange => attackRange;

    protected virtual void Awake()
    {
        useGUILayout = false;
        CacheDefaults();
    }

    /// <summary>
    /// Permite a FirstPersonArms inyectar explícitamente el jugador dueño y la cámara de origen del ataque.
    /// </summary>
    public void SetOwner(PlayerMovement pm)
    {
        if (pm == null) return;
        ownerRoot = pm.transform;
        attackOrigin = pm.CameraTransform != null ? pm.CameraTransform : pm.transform;
    }

    public void CacheDefaults()
    {
        if (cachedTransform == null) cachedTransform = transform;
        defaultPos = cachedTransform.localPosition;
        defaultRot = cachedTransform.localRotation;

        ResolveAttackOrigin();
        cached = true;
    }

    private void ResolveAttackOrigin()
    {
        // includeInactive: true es crítico porque en el Lobby la cámara y brazos están desactivados al instanciar
        PlayerMovement pm = GetComponentInParent<PlayerMovement>(true);
        if (pm != null)
        {
            ownerRoot = pm.transform;
            if (pm.CameraTransform != null)
            {
                attackOrigin = pm.CameraTransform;
                return;
            }

            Camera pmCam = pm.GetComponentInChildren<Camera>(true);
            if (pmCam != null)
            {
                attackOrigin = pmCam.transform;
                return;
            }

            attackOrigin = pm.transform;
            return;
        }

        ownerRoot = cachedTransform.root;
        Camera cam = GetComponentInParent<Camera>(true);
        attackOrigin = cam != null ? cam.transform : cachedTransform;
    }

    /// <summary>
    /// Llamado por FirstPersonArms cada frame mientras el objeto está equipado.
    /// side: lado según el slot de FirstPersonArms. punching: este brazo participa del golpe actual. punchProgress: 0..1 del golpe.
    /// </summary>
    public void Tick(in ArmContext ctx, ArmSide side, bool punching, float punchProgress)
    {
        // Izquierdo: hacia adentro es +X. Derecho: -X. Independiente de la posición del objeto.
        swingDirection = side == ArmSide.Left ? 1f : -1f;
        inwardSign = swingDirection;
        if (!cached) CacheDefaults();

        if (!punching)
        {
            hasDealtDamageInCurrentPunch = false;
        }
        else if (!hasDealtDamageInCurrentPunch && punchProgress >= 0.12f && punchProgress <= 0.75f)
        {
            // Ventana activa del puñetazo: intenta conectar una única vez mientras el puño está extendiéndose
            if (DealDamage())
            {
                hasDealtDamageInCurrentPunch = true;
            }
        }

        isPunching = punching;
        punchWeight = punching ? EvaluatePunchWeight(punchProgress) : 0f;
        Apply(in ctx);
    }

    /// <summary>
    /// Realiza la detección de impacto frente a la cámara y aplica daño al objetivo alcanzado (PlayerHealth).
    /// Devuelve true si golpeó un objetivo válido.
    /// </summary>
    public virtual bool DealDamage()
    {
        if (punchDamage <= 0f) return false;
        if (attackOrigin == null || attackOrigin == cachedTransform)
        {
            ResolveAttackOrigin();
            if (attackOrigin == null) return false;
        }

        // Sincronizar transforms con PhysX máximo una vez por frame (en ProjectSettings m_AutoSyncTransforms está en 0
        // y los jugadores remotos se interpolan por Transform en Update).
        int frame = Time.frameCount;
        if (lastPhysicsSyncFrame != frame)
        {
            lastPhysicsSyncFrame = frame;
            Physics.SyncTransforms();
        }

        Vector3 origin = attackOrigin.position;
        Vector3 direction = attackOrigin.forward;
        float radius = Mathf.Max(0.05f, attackRadius);

        PlayerHealth bestTarget = null;
        float bestDistSqr = float.MaxValue;

        // 1. Chequeo a quemarropa (OverlapSphere): en PhysX, SphereCast ignora colliders que ya solapan la esfera inicial
        Vector3 closeCenter = origin + direction * (radius * 0.75f);
        int overlapCount = Physics.OverlapSphereNonAlloc(closeCenter, radius, overlapBuffer, hitLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < overlapCount; i++)
        {
            Collider col = overlapBuffer[i];
            if (col == null) continue;

            PlayerHealth health = FindValidTargetHealth(col);
            if (health == null) continue;

            Vector3 toTarget = health.transform.position - origin;
            float distSqr = toTarget.sqrMagnitude;
            if (distSqr < bestDistSqr)
            {
                bestDistSqr = distSqr;
                bestTarget = health;
            }
        }

        // 2. Barrido frontal (SphereCast): retrocedemos ligeramente el origen para no perder objetivos pegados a la cámara
        if (bestTarget == null)
        {
            const float backOffset = 0.2f;
            Vector3 castOrigin = origin - direction * backOffset;
            float castDistance = attackRange + backOffset;

            int hitCount = Physics.SphereCastNonAlloc(castOrigin, radius, direction, hitBuffer, castDistance, hitLayers, QueryTriggerInteraction.Ignore);
            float closestHitDist = float.MaxValue;

            for (int i = 0; i < hitCount; i++)
            {
                Collider col = hitBuffer[i].collider;
                if (col == null) continue;

                // Filtrar por PlayerHealth dentro del loop para que el suelo o paredes no bloqueen el golpe
                PlayerHealth health = FindValidTargetHealth(col);
                if (health == null) continue;

                float dist = hitBuffer[i].distance;
                if (dist < closestHitDist)
                {
                    closestHitDist = dist;
                    bestTarget = health;
                }
            }
        }

        if (bestTarget != null)
        {
            bestTarget.TakeDamage(punchDamage, direction, punchKnockback);
            return true;
        }

        return false;
    }

    private PlayerHealth FindValidTargetHealth(Collider col)
    {
        Transform t = col.transform;
        if (ownerRoot != null && (t == ownerRoot || t.IsChildOf(ownerRoot)))
        {
            return null;
        }

        if (!col.TryGetComponent(out PlayerHealth health))
        {
            health = col.GetComponentInParent<PlayerHealth>();
        }

        if (health == null || health.IsDead)
        {
            return null;
        }

        if (ownerRoot != null && health.transform == ownerRoot)
        {
            return null;
        }

        return health;
    }

    /// <summary>Curva del golpe; cada objeto puede sobrescribirla.</summary>
    protected virtual float EvaluatePunchWeight(float t)
    {
        t = Mathf.Clamp01(t);
        if (t < 0.25f)
        {
            float u = 1f - (t * 4f);
            return 1f - (u * u); // Impacto explosivo (Ease-Out)
        }
        else
        {
            float u = (t - 0.25f) * 1.333333f;
            return 1f - (u * u * (3f - 2f * u)); // Retorno suave (Smoothstep)
        }
    }

    private void Apply(in ArmContext ctx)
    {
        float armSwing = ctx.WalkSwing * swingDirection;

        Vector3 locoPos = new Vector3(ctx.Sway.x, ctx.BreathY + ctx.JumpY + ctx.Sway.y, 0f);
        Vector3 locoRot = new Vector3(ctx.BreathPitch + ctx.JumpPitch + ctx.SwayRotX, ctx.SwayRotY, 0f);

        if (ctx.WalkAmp > 0.001f)
        {
            locoPos.x += inwardSign * armSwing * walkSwing.x * ctx.WalkAmp;
            locoPos.y += (armSwing * 0.65f + ctx.WalkBounce * 0.35f) * walkSwing.y * ctx.WalkAmp;
            locoPos.z += armSwing * walkSwing.z * ctx.WalkAmp;

            locoRot.x -= armSwing * walkPitchAngle * ctx.WalkAmp;
            locoRot.y += inwardSign * armSwing * (walkPitchAngle * 0.4f) * ctx.WalkAmp;
        }

        if (isPunching && punchWeight > 0.0001f)
        {
            Vector3 punchPos = new Vector3(
                inwardSign * punchInward + ctx.Sway.x * 0.3f,
                punchVertical + ctx.Sway.y * 0.3f,
                punchDistance);

            Vector3 punchRot = new Vector3(
                -punchTiltAngle,
                inwardSign * (punchTiltAngle * 0.7f),
                inwardSign * (punchTiltAngle * 1.4f));

            locoPos = Vector3.LerpUnclamped(locoPos, punchPos, punchWeight);
            locoRot = Vector3.LerpUnclamped(locoRot, punchRot, punchWeight);
        }

        cachedTransform.SetLocalPositionAndRotation(defaultPos + locoPos, Quaternion.Euler(locoRot) * defaultRot);
    }

    public void ResetToDefault()
    {
        isPunching = false;
        hasDealtDamageInCurrentPunch = false;
        punchWeight = 0f;

        if (!cached) return;
        cachedTransform.SetLocalPositionAndRotation(defaultPos, defaultRot);
    }
}
