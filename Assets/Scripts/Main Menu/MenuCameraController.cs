using UnityEngine;

/// <summary>
/// Controla la cámara del menú principal en el espacio.
/// Aplica una flotación suave y continua en gravedad cero (zero-g drift).
/// Cuenta con un sistema de zoom cinemático real (avance de posición hacia la nave
/// y compresión óptica de FOV) que no interrumpe la flotación ambiental.
/// Totalmente optimizado: matrices cacheadas, pre-cálculos ópticos y 0 GC Alloc.
/// </summary>
#pragma warning disable CS0649
[RequireComponent(typeof(Camera))]
[DisallowMultipleComponent]
public class MenuCameraController : MonoBehaviour
{
    private const float TWO_PI = Mathf.PI * 2f;

    [Header("Tiempo")]
    [Tooltip("Usa tiempo no escalado para garantizar fluidez independientemente de Time.timeScale.")]
    [SerializeField] private bool useUnscaledTime = true;

    [Header("Flotación de Posición (Zero-G Drift)")]
    [Tooltip("Activa el movimiento de flotación suave y continuo de la cámara en el espacio.")]
    [SerializeField] private bool enableAmbientDrift = true;
    [Tooltip("Amplitud de desplazamiento (X = horizontal, Y = vertical, Z = profundidad).")]
    [SerializeField] private Vector3 positionAmplitude = new Vector3(0.12f, 0.16f, 0.08f);
    [Tooltip("Velocidad de oscilación en cada eje.")]
    [SerializeField] private Vector3 positionFrequency = new Vector3(0.25f, 0.35f, 0.2f);

    [Header("Balanceo Angular Ambiental")]
    [Tooltip("Activa la inclinación sutil de la cámara.")]
    [SerializeField] private bool enableRotationDrift = true;
    [Tooltip("Grados máximos de balanceo angular (X = Pitch, Y = Yaw, Z = Roll).")]
    [SerializeField] private Vector3 rotationAmplitude = new Vector3(0.6f, 1.0f, 0.4f);
    [Tooltip("Frecuencia del balanceo angular en cada eje.")]
    [SerializeField] private Vector3 rotationFrequency = new Vector3(0.2f, 0.28f, 0.18f);

    // Cache de componentes
    private Transform cachedTransform;
    private Camera camComponent;

    // Estados base iniciales
    private Vector3 initialPos;
    private Quaternion initialRot;
    private float initialFov;

    // Zoom cinemático real
    private bool isZooming;
    public bool IsZooming => isZooming;

    private Transform zoomTarget;
    private float zoomSpeed = 1.0f;
    private float zoomCloseRatio = 0.7f;
    private float cachedTargetFov;
    private float zoomT;

    private Vector3 zoomOriginPos;
    private Quaternion zoomOriginRot;
    private float zoomOriginFov;
    private Vector3 currentBasePos;
    private Quaternion currentBaseRot;

    // Fase acumulada por eje (radianes, envuelta en [0, 2π)): continua aunque cambien las frecuencias
    // en runtime y sin pérdida de precisión en sesiones largas. Arranca con desfase aleatorio.
    private Vector3 posPhase;
    private Vector3 rotPhase;

    private void Awake()
    {
        useGUILayout = false;
        cachedTransform = transform;
        camComponent = GetComponent<Camera>();

        initialPos = cachedTransform.position;
        initialRot = cachedTransform.rotation;
        initialFov = camComponent != null ? camComponent.fieldOfView : 60f;

        currentBasePos = initialPos;
        currentBaseRot = initialRot;

        posPhase = new Vector3(Random.Range(0f, TWO_PI), Random.Range(0f, TWO_PI), Random.Range(0f, TWO_PI));
        rotPhase = new Vector3(Random.Range(0f, TWO_PI), Random.Range(0f, TWO_PI), Random.Range(0f, TWO_PI));
    }

    private void LateUpdate()
    {
        bool hasDrift = enableAmbientDrift || enableRotationDrift;
        if (!isZooming && !hasDrift)
        {
            return;
        }

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

        // 1. Zoom cinemático real hacia la nave (actualiza currentBasePos y currentBaseRot)
        if (isZooming)
        {
            if (zoomTarget == null)
            {
                isZooming = false;
            }
            else
            {
                zoomT = Mathf.MoveTowards(zoomT, 1f, dt * zoomSpeed);
                float smoothCurve = Mathf.SmoothStep(0f, 1f, zoomT);

                // Vector hacia la nave y desplazamiento óptico directo
                Vector3 toTarget = zoomTarget.position - zoomOriginPos;
                currentBasePos = zoomOriginPos + (toTarget * (zoomCloseRatio * smoothCurve));

                if (toTarget.sqrMagnitude > 0.001f)
                {
                    Quaternion targetLook = Quaternion.LookRotation(toTarget);
                    currentBaseRot = Quaternion.Slerp(zoomOriginRot, targetLook, smoothCurve);
                }

                if (camComponent != null)
                {
                    camComponent.fieldOfView = Mathf.Lerp(zoomOriginFov, cachedTargetFov, smoothCurve);
                }

                // Al completar el zoom al 100%, desactivar para ahorrar cálculo en frames posteriores
                if (zoomT >= 1f)
                {
                    isZooming = false;
                }
            }
        }

        // 2. Flotación ambiental continua en gravedad cero sobre la posición base actualizada
        Vector3 finalPos = currentBasePos;
        if (enableAmbientDrift)
        {
            posPhase.x = MenuSharedUtility.WrapPhase(posPhase.x + positionFrequency.x * dt);
            posPhase.y = MenuSharedUtility.WrapPhase(posPhase.y + positionFrequency.y * dt);
            posPhase.z = MenuSharedUtility.WrapPhase(posPhase.z + positionFrequency.z * dt);

            finalPos.x += Mathf.Sin(posPhase.x) * positionAmplitude.x;
            finalPos.y += Mathf.Sin(posPhase.y) * positionAmplitude.y;
            finalPos.z += Mathf.Cos(posPhase.z) * positionAmplitude.z;
        }

        Quaternion finalRot = currentBaseRot;
        if (enableRotationDrift)
        {
            rotPhase.x = MenuSharedUtility.WrapPhase(rotPhase.x + rotationFrequency.x * dt);
            rotPhase.y = MenuSharedUtility.WrapPhase(rotPhase.y + rotationFrequency.y * dt);
            rotPhase.z = MenuSharedUtility.WrapPhase(rotPhase.z + rotationFrequency.z * dt);

            float rPitch = Mathf.Sin(rotPhase.x) * rotationAmplitude.x;
            float rYaw   = Mathf.Cos(rotPhase.y) * rotationAmplitude.y;
            float rRoll  = Mathf.Sin(rotPhase.z) * rotationAmplitude.z;
            finalRot = currentBaseRot * Quaternion.Euler(rPitch, rYaw, rRoll);
        }

        // 3. Aplicar en una única llamada nativa
        cachedTransform.SetPositionAndRotation(finalPos, finalRot);
    }

    /// <summary>
    /// Inicia un zoom real hacia la nave cerrando la distancia y reduciendo el FOV.
    /// </summary>
    public void StartZoom(Transform target, float speed, float closeRatio = 0.7f, float fovRatio = 0.45f)
    {
        zoomTarget = target;
        zoomSpeed = Mathf.Max(0.01f, speed);
        zoomCloseRatio = Mathf.Clamp(closeRatio, 0.1f, 0.95f);
        float clampedFovRatio = Mathf.Clamp(fovRatio, 0.1f, 1f);

        zoomOriginPos = currentBasePos;
        zoomOriginRot = currentBaseRot;
        zoomOriginFov = camComponent != null ? camComponent.fieldOfView : initialFov;
        cachedTargetFov = zoomOriginFov * clampedFovRatio;
        zoomT = 0f;

        isZooming = true;
    }

    /// <summary>
    /// Restablece el zoom a la vista normal del menú principal.
    /// </summary>
    public void ResetZoom()
    {
        if (cachedTransform == null) cachedTransform = transform;
        isZooming = false;
        zoomTarget = null;
        zoomT = 0f;
        currentBasePos = initialPos;
        currentBaseRot = initialRot;

        if (camComponent != null)
        {
            camComponent.fieldOfView = initialFov;
        }

        if (cachedTransform != null)
        {
            cachedTransform.SetPositionAndRotation(initialPos, initialRot);
        }
    }

    /// <summary>
    /// Reestablece la posición y rotación base de la cámara a la actual.
    /// </summary>
    public void ResetBasePosition()
    {
        if (cachedTransform == null) cachedTransform = transform;
        initialPos = cachedTransform.position;
        initialRot = cachedTransform.rotation;
        currentBasePos = initialPos;
        currentBaseRot = initialRot;
    }
}
