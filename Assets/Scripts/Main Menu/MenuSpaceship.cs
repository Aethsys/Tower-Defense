using UnityEngine;

/// <summary>
/// Controla la nave en el menú principal:
/// Movimiento puramente cinemático y autónomo en el espacio.
/// Combina flotación de posición (bobbing), balanceo sutil en gravedad cero (wobble)
/// y un giro continuo suave (idle spin).
/// Máxima eficiencia: sin allocations, con límites de precisión de tiempo y matrices optimizadas.
/// </summary>
#pragma warning disable CS0649
[SelectionBase]
[DisallowMultipleComponent]
public class MenuSpaceship : MonoBehaviour
{
    private const float TWO_PI = Mathf.PI * 2f;

    [Header("Tiempo")]
    [Tooltip("Usa tiempo no escalado para que siga animándose aunque Time.timeScale sea 0.")]
    [SerializeField] private bool useUnscaledTime = true;

    [Header("Giro Continuo (Idle Spin)")]
    [Tooltip("Activa el giro lento y continuo de la nave en el espacio.")]
    [SerializeField] private bool enableIdleSpin = true;
    [Tooltip("Velocidad de giro continuo en grados por segundo para cada eje (X = Pitch, Y = Yaw, Z = Roll).")]
    [SerializeField] private Vector3 spinSpeed = new Vector3(0f, 3.0f, 0.5f);

    [Header("Flotación de Posición (Bobbing Espacial)")]
    [Tooltip("La nave se desplaza y flota suavemente en el espacio.")]
    [SerializeField] private bool enablePositionFloating = true;
    [Tooltip("Amplitud de movimiento en cada eje (X = lateral, Y = vertical, Z = profundidad).")]
    [SerializeField] private Vector3 positionAmplitude = new Vector3(0.08f, 0.22f, 0.05f);
    [Tooltip("Velocidad de oscilación en cada eje.")]
    [SerializeField] private Vector3 positionFrequency = new Vector3(0.45f, 0.7f, 0.35f);

    [Header("Balanceo Angular Automático (Zero-G Wobble)")]
    [Tooltip("Vaivén angular orgánico simulando inercia en gravedad cero.")]
    [SerializeField] private bool enableRotationWobble = true;
    [Tooltip("Grados máximos de balanceo en cada eje (X = Pitch, Y = Yaw, Z = Roll).")]
    [SerializeField] private Vector3 wobbleAmplitude = new Vector3(2.5f, 2.0f, 3.5f);
    [Tooltip("Frecuencia del balanceo en cada eje.")]
    [SerializeField] private Vector3 wobbleFrequency = new Vector3(0.5f, 0.35f, 0.6f);

    // Cache de transform
    private Transform cachedTransform;

    // Estados base
    private Vector3 initialLocalPos;
    private Quaternion initialLocalRot;
    private Vector3 currentSpinAngles;

    // Fase acumulada por eje (radianes, envuelta en [0, 2π)): continua aunque cambien las frecuencias
    // en runtime y sin pérdida de precisión en sesiones largas. Arranca con desfase aleatorio.
    private Vector3 floatPhase;
    private Vector3 wobblePhase;

    private void Awake()
    {
        useGUILayout = false;
        cachedTransform = transform;

        initialLocalPos = cachedTransform.localPosition;
        initialLocalRot = cachedTransform.localRotation;

        RandomizePhases();
    }

    private void RandomizePhases()
    {
        floatPhase = new Vector3(Random.Range(0f, TWO_PI), Random.Range(0f, TWO_PI), Random.Range(0f, TWO_PI));
        wobblePhase = new Vector3(Random.Range(0f, TWO_PI), Random.Range(0f, TWO_PI), Random.Range(0f, TWO_PI));
    }

    private void Update()
    {
        if (!enablePositionFloating && !enableIdleSpin && !enableRotationWobble)
        {
            return;
        }

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

        // 1. Cálculo de posición con oscilación suave
        Vector3 targetPos = initialLocalPos;
        if (enablePositionFloating)
        {
            floatPhase.x = MenuSharedUtility.WrapPhase(floatPhase.x + positionFrequency.x * dt);
            floatPhase.y = MenuSharedUtility.WrapPhase(floatPhase.y + positionFrequency.y * dt);
            floatPhase.z = MenuSharedUtility.WrapPhase(floatPhase.z + positionFrequency.z * dt);

            targetPos.x += Mathf.Sin(floatPhase.x) * positionAmplitude.x;
            targetPos.y += Mathf.Sin(floatPhase.y) * positionAmplitude.y;
            targetPos.z += Mathf.Cos(floatPhase.z) * positionAmplitude.z;
        }

        // 2. Cálculo de rotación (Giro continuo estable + Vaivén orgánico)
        Quaternion targetRot = initialLocalRot;

        if (enableIdleSpin)
        {
            currentSpinAngles += spinSpeed * dt;
            currentSpinAngles.x %= 360f;
            currentSpinAngles.y %= 360f;
            currentSpinAngles.z %= 360f;
            targetRot *= Quaternion.Euler(currentSpinAngles);
        }

        if (enableRotationWobble)
        {
            wobblePhase.x = MenuSharedUtility.WrapPhase(wobblePhase.x + wobbleFrequency.x * dt);
            wobblePhase.y = MenuSharedUtility.WrapPhase(wobblePhase.y + wobbleFrequency.y * dt);
            wobblePhase.z = MenuSharedUtility.WrapPhase(wobblePhase.z + wobbleFrequency.z * dt);

            float p = Mathf.Sin(wobblePhase.x) * wobbleAmplitude.x;
            float y = Mathf.Sin(wobblePhase.y) * wobbleAmplitude.y;
            float r = Mathf.Cos(wobblePhase.z) * wobbleAmplitude.z;
            targetRot *= Quaternion.Euler(p, y, r);
        }

        // 3. Aplicar en una única llamada interna al motor nativo
        cachedTransform.SetLocalPositionAndRotation(targetPos, targetRot);
    }

    /// <summary>
    /// Restablece la rotación y posición a las iniciales de la escena.
    /// </summary>
    public void ResetTransform()
    {
        if (cachedTransform == null) cachedTransform = transform;
        currentSpinAngles = Vector3.zero;
        RandomizePhases();
        cachedTransform.SetLocalPositionAndRotation(initialLocalPos, initialLocalRot);
    }
}
