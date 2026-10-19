using System;
using UnityEngine;

/// <summary>
/// Game feel de la cámara en primera persona (solo para el jugador local):
/// head bob al caminar/correr, dip al saltar/aterrizar, inclinación al hacer strafe,
/// FOV dinámico al correr y camera shake / recoil para el combate.
///
/// Se aplica en LateUpdate encima de la rotación base que calcula <see cref="PlayerMovement"/>,
/// así que si este componente se desactiva la cámara sigue funcionando normal.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)]
public class PlayerCameraFeel : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PlayerMovement movement;
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private Camera targetCamera;

    [Header("Head Bob")]
    [SerializeField] private bool enableHeadBob = true;
    [Tooltip("Pasos por segundo caminando.")]
    [SerializeField] private float walkStepFrequency = 1.9f;
    [Tooltip("Pasos por segundo corriendo.")]
    [SerializeField] private float sprintStepFrequency = 2.5f;
    [Tooltip("Amplitud vertical del bob caminando (metros).")]
    [SerializeField] private float verticalAmplitude = 0.028f;
    [Tooltip("Amplitud lateral del bob caminando (metros).")]
    [SerializeField] private float horizontalAmplitude = 0.016f;
    [Tooltip("Balanceo (roll) del bob caminando (grados).")]
    [SerializeField] private float rollAmplitude = 0.3f;
    [Tooltip("Multiplicador de amplitud al correr.")]
    [SerializeField] private float sprintAmplitudeMultiplier = 1.45f;
    [Tooltip("Velocidad con la que el bob entra/sale al empezar/parar de moverse.")]
    [SerializeField] private float bobBlendSpeed = 7f;

    [Header("Jump & Landing")]
    [Tooltip("Impulso hacia abajo de la cámara al aterrizar por cada m/s de velocidad de caída.")]
    [SerializeField] private float landingDipPerSpeed = 0.18f;
    [SerializeField] private float maxLandingDipImpulse = 2.2f;
    [Tooltip("Cabeceo (grados/s) al aterrizar por cada m/s de velocidad de caída.")]
    [SerializeField] private float landingPitchPerSpeed = 3.2f;
    [SerializeField] private float maxLandingPitchImpulse = 38f;
    [Tooltip("Velocidad de caída mínima para que el aterrizaje se note.")]
    [SerializeField] private float minLandingSpeed = 2.5f;
    [Tooltip("Pequeño cabeceo hacia arriba al saltar (grados/s).")]
    [SerializeField] private float jumpPitchImpulse = -10f;
    [Tooltip("Pequeño impulso vertical de la cámara al saltar.")]
    [SerializeField] private float jumpPositionImpulse = -0.35f;

    [Header("Spring (jump/land/recoil)")]
    [SerializeField] private float springStiffness = 120f;
    [SerializeField] private float springDamping = 14f;

    [Header("Strafe Tilt")]
    [SerializeField] private bool enableStrafeTilt = true;
    [Tooltip("Inclinación máxima (grados) al moverse lateralmente a velocidad de sprint.")]
    [SerializeField] private float maxStrafeTilt = 1.1f;
    [SerializeField] private float strafeTiltSpeed = 8f;

    [Header("Sprint FOV")]
    [SerializeField] private bool enableSprintFov = true;
    [SerializeField] private float sprintFovIncrease = 5f;
    [SerializeField] private float fovBlendSpeed = 6f;

    [Header("Shake")]
    [Tooltip("Rotación máxima (grados) con trauma = 1.")]
    [SerializeField] private float maxShakeAngle = 2.2f;
    [Tooltip("Desplazamiento máximo (m) con trauma = 1.")]
    [SerializeField] private float maxShakeOffset = 0.035f;
    [SerializeField] private float shakeFrequency = 22f;
    [Tooltip("Trauma que se pierde por segundo.")]
    [SerializeField] private float traumaDecay = 1.8f;

    /// <summary>Se dispara en cada paso (punto más bajo del bob). Útil para sonidos de pasos.</summary>
    public event Action Footstep;

    private Vector3 baseLocalPosition;
    private float baseFov;
    private bool hasBasePose;
    private bool wasApplying;

    private float bobPhase;
    private float bobIntensity;
    private float bobAmplitudeScale = 1f;
    private int lastStepIndex;

    private float springPos;
    private float springPosVel;
    private float springPitch;
    private float springPitchVel;

    private float strafeTilt;
    private float currentFovOffset;

    private float trauma;
    private float noiseSeed;

    public float Trauma => trauma;

    private void Awake()
    {
        useGUILayout = false;

        if (movement == null)
        {
            movement = GetComponent<PlayerMovement>();
        }

        if (cameraTransform == null && movement != null)
        {
            cameraTransform = movement.CameraTransform;
        }

        if (cameraTransform == null)
        {
            Camera cam = GetComponentInChildren<Camera>(true);
            if (cam != null)
            {
                cameraTransform = cam.transform;
            }
        }

        if (targetCamera == null && cameraTransform != null)
        {
            targetCamera = cameraTransform.GetComponent<Camera>();
        }

        CaptureBasePose();
        noiseSeed = UnityEngine.Random.Range(0f, 1000f);
    }

    private void OnEnable()
    {
        if (movement != null)
        {
            movement.Jumped += HandleJumped;
            movement.Landed += HandleLanded;
        }
    }

    private void OnDisable()
    {
        if (movement != null)
        {
            movement.Jumped -= HandleJumped;
            movement.Landed -= HandleLanded;
        }

        RestoreBasePose();
    }

    private void CaptureBasePose()
    {
        if (cameraTransform == null)
        {
            return;
        }

        baseLocalPosition = cameraTransform.localPosition;
        baseFov = targetCamera != null ? targetCamera.fieldOfView : 60f;
        hasBasePose = true;
    }

    private void RestoreBasePose()
    {
        if (!hasBasePose || cameraTransform == null)
        {
            return;
        }

        cameraTransform.localPosition = baseLocalPosition;
        if (targetCamera != null)
        {
            targetCamera.fieldOfView = baseFov;
        }

        if (movement != null)
        {
            cameraTransform.localRotation = Quaternion.Euler(movement.CameraPitch, movement.CameraBaseYaw, 0f);
        }

        ResetDynamics();
    }

    private void ResetDynamics()
    {
        bobIntensity = 0f;
        springPos = springPosVel = 0f;
        springPitch = springPitchVel = 0f;
        strafeTilt = 0f;
        currentFovOffset = 0f;
        trauma = 0f;
    }

    // ---------------------------------------------------------------- Public API

    /// <summary>Añade trauma (0-1). El shake es proporcional a trauma², así que valores pequeños son sutiles.</summary>
    public void AddTrauma(float amount)
    {
        trauma = Mathf.Clamp01(trauma + Mathf.Max(0f, amount));
    }

    /// <summary>Impulso de cabeceo (grados/s). Negativo = mira hacia arriba.</summary>
    public void AddPitchImpulse(float degreesPerSecond)
    {
        springPitchVel += degreesPerSecond;
    }

    /// <summary>Impulso vertical de cámara (m/s). Negativo = baja.</summary>
    public void AddPositionImpulse(float metersPerSecond)
    {
        springPosVel += metersPerSecond;
    }

    // ---------------------------------------------------------------- Movement events

    private void HandleJumped()
    {
        springPitchVel += jumpPitchImpulse;
        springPosVel += jumpPositionImpulse;
    }

    private void HandleLanded(float fallSpeed)
    {
        float effective = fallSpeed - minLandingSpeed;
        if (effective <= 0f)
        {
            return;
        }

        springPosVel -= Mathf.Min(effective * landingDipPerSpeed, maxLandingDipImpulse);
        springPitchVel += Mathf.Min(effective * landingPitchPerSpeed, maxLandingPitchImpulse);
    }

    // ---------------------------------------------------------------- Update

    private void LateUpdate()
    {
        if (cameraTransform == null || movement == null)
        {
            return;
        }

        if (!hasBasePose)
        {
            CaptureBasePose();
        }

        bool shouldApply = movement.IsLocallyControlled && (targetCamera == null || targetCamera.enabled);
        if (!shouldApply)
        {
            if (wasApplying)
            {
                RestoreBasePose();
                wasApplying = false;
            }
            return;
        }

        wasApplying = true;

        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        if (dt <= 0f)
        {
            return;
        }

        Vector3 horizontalVelocity = movement.HorizontalVelocity;
        float speed = horizontalVelocity.magnitude;
        bool grounded = movement.IsGrounded;

        UpdateHeadBob(dt, speed, grounded);
        UpdateSprings(dt);
        UpdateStrafeTilt(dt, horizontalVelocity);
        UpdateFov(dt, speed, grounded);
        trauma = Mathf.Max(0f, trauma - traumaDecay * dt);

        Apply();
    }

    private void UpdateHeadBob(float dt, float speed, bool grounded)
    {
        float walkSpeed = Mathf.Max(0.1f, movement.WalkSpeed);
        float sprintSpeed = Mathf.Max(walkSpeed + 0.01f, movement.SprintSpeed);

        float targetIntensity = enableHeadBob && grounded ? Mathf.Clamp01(speed / walkSpeed) : 0f;
        bobIntensity = Mathf.MoveTowards(bobIntensity, targetIntensity, bobBlendSpeed * dt);

        float sprintBlend = Mathf.Clamp01((speed - walkSpeed) / (sprintSpeed - walkSpeed));
        float targetAmplitudeScale = Mathf.Lerp(1f, sprintAmplitudeMultiplier, sprintBlend);
        bobAmplitudeScale = Mathf.Lerp(bobAmplitudeScale, targetAmplitudeScale, 1f - Mathf.Exp(-6f * dt));

        if (bobIntensity <= 0.0001f)
        {
            // Vuelve a la fase neutra para que el próximo arranque empiece suave.
            bobPhase = 0f;
            lastStepIndex = 0;
            return;
        }

        // La frecuencia sigue a la velocidad real (aceleraciones suaves = pasos suaves).
        float speedRatio = Mathf.Clamp(speed / walkSpeed, 0.6f, sprintSpeed / walkSpeed);
        float frequency = Mathf.Lerp(walkStepFrequency * speedRatio, sprintStepFrequency, sprintBlend);
        frequency = Mathf.Min(frequency, sprintStepFrequency);

        // Un paso = PI radianes (ciclo completo izquierda + derecha = 2PI).
        bobPhase += frequency * Mathf.PI * dt;
        if (bobPhase > Mathf.PI * 2000f)
        {
            bobPhase -= Mathf.PI * 2000f;
            lastStepIndex = Mathf.FloorToInt((bobPhase + Mathf.PI * 0.5f) / Mathf.PI);
        }

        // El punto más bajo del bob vertical (-cos(2φ)) ocurre en φ = k·PI.
        int stepIndex = Mathf.FloorToInt((bobPhase + Mathf.PI * 0.5f) / Mathf.PI);
        if (stepIndex != lastStepIndex)
        {
            lastStepIndex = stepIndex;
            if (grounded && bobIntensity > 0.5f)
            {
                Footstep?.Invoke();
            }
        }
    }

    private void UpdateSprings(float dt)
    {
        // Semi-implicit Euler: estable con los dt que permitimos (<= 0.05).
        springPosVel += (-springStiffness * springPos - springDamping * springPosVel) * dt;
        springPos += springPosVel * dt;

        springPitchVel += (-springStiffness * springPitch - springDamping * springPitchVel) * dt;
        springPitch += springPitchVel * dt;

        springPos = Mathf.Clamp(springPos, -0.25f, 0.25f);
        springPitch = Mathf.Clamp(springPitch, -8f, 8f);
    }

    private void UpdateStrafeTilt(float dt, Vector3 horizontalVelocity)
    {
        float target = 0f;
        if (enableStrafeTilt && cameraTransform.parent != null)
        {
            Vector3 camRight = cameraTransform.right;
            camRight.y = 0f;
            if (camRight.sqrMagnitude > 0.0001f)
            {
                float lateral = Vector3.Dot(horizontalVelocity, camRight.normalized);
                target = -Mathf.Clamp(lateral / Mathf.Max(0.1f, movement.SprintSpeed), -1f, 1f) * maxStrafeTilt;
            }
        }

        strafeTilt = Mathf.Lerp(strafeTilt, target, 1f - Mathf.Exp(-strafeTiltSpeed * dt));
    }

    private void UpdateFov(float dt, float speed, bool grounded)
    {
        if (targetCamera == null)
        {
            return;
        }

        float target = 0f;
        if (enableSprintFov && movement.IsSprinting && grounded)
        {
            float walkSpeed = Mathf.Max(0.1f, movement.WalkSpeed);
            float sprintSpeed = Mathf.Max(walkSpeed + 0.01f, movement.SprintSpeed);
            target = sprintFovIncrease * Mathf.Clamp01((speed - walkSpeed) / (sprintSpeed - walkSpeed));
        }

        currentFovOffset = Mathf.Lerp(currentFovOffset, target, 1f - Mathf.Exp(-fovBlendSpeed * dt));
        targetCamera.fieldOfView = baseFov + currentFovOffset;
    }

    private void Apply()
    {
        float amp = bobIntensity * bobAmplitudeScale;
        float bobY = -Mathf.Cos(bobPhase * 2f) * verticalAmplitude * amp;
        float bobX = Mathf.Sin(bobPhase) * horizontalAmplitude * amp;
        float bobRoll = Mathf.Sin(bobPhase) * rollAmplitude * amp;

        float shake = trauma * trauma;
        float t = Time.time * shakeFrequency;
        float shakePitch = 0f, shakeYaw = 0f, shakeRoll = 0f;
        Vector3 shakeOffset = Vector3.zero;
        if (shake > 0.0001f)
        {
            shakePitch = maxShakeAngle * shake * PerlinSigned(noiseSeed, t);
            shakeYaw = maxShakeAngle * shake * PerlinSigned(noiseSeed + 17.3f, t);
            shakeRoll = maxShakeAngle * 0.6f * shake * PerlinSigned(noiseSeed + 41.9f, t);
            shakeOffset = new Vector3(
                PerlinSigned(noiseSeed + 73.1f, t),
                PerlinSigned(noiseSeed + 91.7f, t),
                0f) * (maxShakeOffset * shake);
        }

        float yaw = movement.CameraBaseYaw;
        float pitch = Mathf.Clamp(movement.CameraPitch + springPitch + shakePitch, -89f, 89f);
        float roll = bobRoll + strafeTilt + shakeRoll;

        Quaternion yawRotation = Quaternion.Euler(0f, yaw, 0f);
        Vector3 localOffset = yawRotation * new Vector3(bobX + shakeOffset.x, bobY + springPos + shakeOffset.y, 0f);

        cameraTransform.SetLocalPositionAndRotation(
            baseLocalPosition + localOffset,
            Quaternion.Euler(pitch, yaw + shakeYaw, roll));
    }

    private static float PerlinSigned(float seed, float t)
    {
        return Mathf.PerlinNoise(seed, t) * 2f - 1f;
    }
}
