using System;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

#pragma warning disable CS0649
/// <summary>
/// Gestiona la salud, muerte y respawn del jugador tanto en modo local como sincronizado en red (Netcode for GameObjects),
/// incluyendo feedback de impacto: empuje físico (knockback), destello rojo en el modelo 3D (MaterialPropertyBlock, 0 GC)
/// y destello/overlay rojo en pantalla para el jugador local.
/// </summary>
[DisallowMultipleComponent]
public class PlayerHealth : NetworkBehaviour
{
    [Header("Health Settings")]
    [Tooltip("Salud máxima del jugador.")]
    [Min(1f)]
    [SerializeField] private float maxHealth = 100f;

    [Tooltip("Si está activo, el jugador no puede recibir daño.")]
    [SerializeField] private bool isInvulnerable;

    [Header("Death & Respawn")]
    [Tooltip("Si está activo, el jugador reaparece automáticamente tras morir.")]
    [SerializeField] private bool autoRespawn = true;

    [Tooltip("Segundos que tarda el jugador en reaparecer tras morir.")]
    [Min(0.1f)]
    [SerializeField] private float respawnDelay = 3f;

    [Tooltip("Segundos de invulnerabilidad temporal tras reaparecer.")]
    [Min(0f)]
    [SerializeField] private float spawnInvulnerabilityDuration = 1.5f;

    [Tooltip("Opacidad del tinte rojo en pantalla mientras el jugador local está muerto esperando respawn.")]
    [Range(0f, 0.85f)]
    [SerializeField] private float deathScreenOverlayAlpha = 0.4f;

    [Tooltip("Texto TextMeshPro opcional para mostrar la cuenta regresiva del respawn en pantalla.")]
    [SerializeField] private TMP_Text respawnTimerText;

    [Tooltip("Si está activo, muestra el contador con un decimal (ej. 2.5); si no, muestra segundos enteros (3, 2, 1).")]
    [SerializeField] private bool showRespawnDecimals;

    [Header("Hit Feedback - Knockback")]
    [Tooltip("Fuerza horizontal de empuje por defecto al recibir un golpe.")]
    [SerializeField] private float defaultKnockbackForce = 6.5f;

    [Tooltip("Impulso vertical ligero al recibir el golpe.")]
    [SerializeField] private float knockbackUpwardForce = 2.8f;

    [Header("Hit Feedback - Red Flash")]
    [Tooltip("Color del destello en el cuerpo del personaje al recibir daño.")]
    [SerializeField] private Color hitFlashColor = new Color(1f, 0.18f, 0.18f, 1f);

    [Tooltip("Duración en segundos del destello rojo en el modelo.")]
    [SerializeField] private float hitFlashDuration = 0.22f;

    [Tooltip("Muestra también un destello rojo en los bordes/pantalla del jugador local al recibir daño.")]
    [SerializeField] private bool showScreenDamageFlash = true;

    [Tooltip("Opacidad máxima del destello rojo en la pantalla del jugador golpeado.")]
    [Range(0f, 0.8f)]
    [SerializeField] private float screenFlashMaxAlpha = 0.28f;

    private static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int ID_Color = Shader.PropertyToID("_Color");
    private static readonly int ID_EmissionColor = Shader.PropertyToID("_EmissionColor");

    // Sincronización en red (el servidor tiene la autoridad sobre la vida; si no está en red, usa localHealth)
    private readonly NetworkVariable<float> netCurrentHealth = new NetworkVariable<float>(
        100f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private float localHealth = 100f;
    private float respawnTimer;
    private float invulnerabilityTimer;
    private bool inDeadState;

    // Cache de componentes para feedback (0 GC Alloc en runtime)
    private PlayerMovement playerMovement;
    private FirstPersonArms firstPersonArms;
    private Transform cachedMeshRoot;
    private Renderer[] bodyRenderers;
    private Color[] originalBaseColors;
    private MaterialPropertyBlock propBlock;
    private float flashTimer;
    private bool isFlashing;
    private Image screenFlashImage;
    private int lastDisplayedRespawnStep = -1;

    /// <summary>Se dispara cuando cambia la vida: (vidaActual, vidaMaxima).</summary>
    public event Action<float, float> OnHealthChanged;

    /// <summary>Se dispara al recibir daño: (cantidadDeDaño, vidaRestante).</summary>
    public event Action<float, float> OnDamaged;

    /// <summary>Se dispara cuando la vida llega a 0.</summary>
    public event Action OnDied;

    /// <summary>Se dispara cuando el jugador reaparece con vida completa.</summary>
    public event Action OnRespawned;

    public float MaxHealth => maxHealth;
    public float CurrentHealth => IsSpawned ? netCurrentHealth.Value : localHealth;
    public float HealthNormalized => maxHealth > 0.0001f ? Mathf.Clamp01(CurrentHealth / maxHealth) : 0f;
    public bool IsDead => CurrentHealth <= 0f;
    public float RespawnTimeRemaining => inDeadState ? Mathf.Max(0f, respawnTimer) : 0f;
    public float DefaultKnockbackForce => defaultKnockbackForce;

    public bool IsInvulnerable
    {
        get => isInvulnerable || invulnerabilityTimer > 0f;
        set => isInvulnerable = value;
    }

    private void Awake()
    {
        useGUILayout = false;
        localHealth = maxHealth;
        playerMovement = GetComponent<PlayerMovement>();
        firstPersonArms = GetComponentInChildren<FirstPersonArms>(true);
        if (respawnTimerText != null)
        {
            respawnTimerText.raycastTarget = false;
        }
        CacheBodyRenderers();
        SetRespawnTimerVisible(false);
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        ClearFlashVisuals();
        SetRespawnTimerVisible(false);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        ClearFlashVisuals();
        invulnerabilityTimer = 0f;
        respawnTimer = 0f;

        if (inDeadState)
        {
            ExitDeadState();
        }

        // Al cambiar de escena (ej. entrar a partida desde Lobby o reiniciar ronda), restaurar la vida al máximo
        if (!IsSpawned)
        {
            localHealth = maxHealth;
            OnHealthChanged?.Invoke(localHealth, maxHealth);
        }
        else if (IsServer)
        {
            netCurrentHealth.Value = maxHealth;
        }
    }

    public void CacheBodyRenderers()
    {
        Transform meshRoot = (playerMovement != null && playerMovement.ThirdPersonMesh != null)
            ? playerMovement.ThirdPersonMesh
            : transform;

        Transform fpRoot = playerMovement != null ? playerMovement.FirstPersonMesh : null;
        cachedMeshRoot = meshRoot;

        Renderer[] allRenderers = meshRoot.GetComponentsInChildren<Renderer>(true);
        var filtered = new List<Renderer>(allRenderers.Length);
        for (int i = 0; i < allRenderers.Length; i++)
        {
            Renderer r = allRenderers[i];
            if (r == null) continue;
            if (fpRoot != null && (r.transform == fpRoot || r.transform.IsChildOf(fpRoot))) continue;
            filtered.Add(r);
        }

        bodyRenderers = filtered.ToArray();
        originalBaseColors = new Color[bodyRenderers.Length];

        for (int i = 0; i < bodyRenderers.Length; i++)
        {
            Renderer r = bodyRenderers[i];
            Color baseCol = Color.white;
            if (r != null && r.sharedMaterial != null)
            {
                Material mat = r.sharedMaterial;
                if (mat.HasProperty(ID_BaseColor)) baseCol = mat.GetColor(ID_BaseColor);
                else if (mat.HasProperty(ID_Color)) baseCol = mat.GetColor(ID_Color);
            }
            originalBaseColors[i] = baseCol;
        }

        if (propBlock == null)
        {
            propBlock = new MaterialPropertyBlock();
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsServer)
        {
            netCurrentHealth.Value = maxHealth;
        }

        localHealth = maxHealth;
        inDeadState = false;
        respawnTimer = 0f;
        invulnerabilityTimer = 0f;
        SetRespawnTimerVisible(false);

        netCurrentHealth.OnValueChanged += OnNetHealthChanged;
        OnHealthChanged?.Invoke(netCurrentHealth.Value, maxHealth);
    }

    public override void OnNetworkDespawn()
    {
        netCurrentHealth.OnValueChanged -= OnNetHealthChanged;
        ClearFlashVisuals();
        SetRespawnTimerVisible(false);
        base.OnNetworkDespawn();
    }

    private void Update()
    {
        if (!isFlashing && !inDeadState && invulnerabilityTimer <= 0f) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        if (invulnerabilityTimer > 0f)
        {
            invulnerabilityTimer -= dt;
        }

        // Temporizador de respawn (autoridad en servidor o en modo offline; visual en el dueño local)
        if (inDeadState && autoRespawn)
        {
            respawnTimer -= dt;
            UpdateRespawnTimerUI();

            if (respawnTimer <= 0f && (!IsSpawned || IsServer))
            {
                RespawnNow();
            }
        }

        if (!isFlashing) return;

        flashTimer -= dt;
        if (flashTimer <= 0f)
        {
            if (inDeadState)
            {
                isFlashing = false;
                ApplyDeathScreenOverlay(true);
            }
            else
            {
                ClearFlashVisuals();
            }
            return;
        }

        float t = Mathf.Clamp01(flashTimer / Mathf.Max(0.01f, hitFlashDuration));
        ApplyFlashVisuals(t);
    }

    private void EnterDeadState()
    {
        if (inDeadState) return;
        inDeadState = true;
        isFlashing = false;
        flashTimer = 0f;
        respawnTimer = respawnDelay;
        lastDisplayedRespawnStep = -1;

        // Limpiar materiales del modelo 3D antes de ocultarlo
        if (bodyRenderers != null)
        {
            for (int i = 0; i < bodyRenderers.Length; i++)
            {
                Renderer r = bodyRenderers[i];
                if (r != null) r.SetPropertyBlock(null);
            }
        }

        if (firstPersonArms != null)
        {
            firstPersonArms.ResetArmsToDefault();
        }

        if (playerMovement != null)
        {
            playerMovement.SetDeadState(true);
        }

        ApplyDeathScreenOverlay(true);

        if (autoRespawn)
        {
            SetRespawnTimerVisible(true);
            UpdateRespawnTimerUI();
        }

        OnDied?.Invoke();
    }

    private void ExitDeadState()
    {
        bool wasDead = inDeadState;
        inDeadState = false;
        respawnTimer = 0f;
        invulnerabilityTimer = spawnInvulnerabilityDuration;
        lastDisplayedRespawnStep = -1;

        ClearFlashVisuals();
        SetRespawnTimerVisible(false);

        if (playerMovement != null)
        {
            playerMovement.SetDeadState(false);
        }

        if (firstPersonArms != null)
        {
            firstPersonArms.ResetArmsToDefault();
        }

        if (wasDead)
        {
            OnRespawned?.Invoke();
        }
    }

    private void SetRespawnTimerVisible(bool visible)
    {
        if (respawnTimerText == null) return;

        // Solo el jugador dueño local debe ver su propio contador en pantalla
        bool shouldShow = visible && (!IsSpawned || IsOwner);

        if (shouldShow && playerMovement != null && playerMovement.CrosshairUI != null)
        {
            // Si el usuario colocó el texto dentro del Crosshair (que se desactiva al morir),
            // subirlo al Canvas padre para que pueda mostrarse mientras está muerto
            Transform crosshairTransform = playerMovement.CrosshairUI.transform;
            if (respawnTimerText.transform != crosshairTransform &&
                respawnTimerText.transform.IsChildOf(crosshairTransform) &&
                crosshairTransform.parent != null)
            {
                respawnTimerText.transform.SetParent(crosshairTransform.parent, true);
            }
        }

        GameObject textObj = respawnTimerText.gameObject;
        if (textObj.activeSelf != shouldShow)
        {
            textObj.SetActive(shouldShow);
        }
    }

    private void UpdateRespawnTimerUI()
    {
        if (respawnTimerText == null || (IsSpawned && !IsOwner)) return;

        float remaining = Mathf.Max(0f, respawnTimer);
        if (showRespawnDecimals)
        {
            // Actualizar solo cada décima de segundo (0 GC Alloc con SetText)
            int tenthStep = Mathf.CeilToInt(remaining * 10f);
            if (tenthStep != lastDisplayedRespawnStep)
            {
                lastDisplayedRespawnStep = tenthStep;
                respawnTimerText.SetText("{0:1}", tenthStep * 0.1f);
            }
        }
        else
        {
            // Actualizar solo cuando cambia el segundo entero (ej. 3 -> 2 -> 1), evitando reconstruir el mesh de TMP cada frame
            int secStep = Mathf.Max(1, Mathf.CeilToInt(remaining));
            if (secStep != lastDisplayedRespawnStep)
            {
                lastDisplayedRespawnStep = secStep;
                respawnTimerText.SetText("{0:0}", secStep);
            }
        }
    }

    private void ApplyDeathScreenOverlay(bool dead)
    {
        if (!showScreenDamageFlash || (IsSpawned && !IsOwner)) return;

        if (dead && deathScreenOverlayAlpha > 0.001f)
        {
            if (screenFlashImage == null)
            {
                screenFlashImage = EnsureScreenFlashImage();
            }

            if (screenFlashImage != null)
            {
                if (!screenFlashImage.enabled) screenFlashImage.enabled = true;
                Color c = hitFlashColor;
                c.a = deathScreenOverlayAlpha;
                screenFlashImage.color = c;
            }
        }
        else if (screenFlashImage != null && screenFlashImage.enabled)
        {
            screenFlashImage.enabled = false;
        }
    }

    /// <summary>
    /// Fuerza el respawn inmediato del jugador en su punto de aparición con vida máxima.
    /// </summary>
    public void RespawnNow()
    {
        GetRespawnTransform(out Vector3 spawnPos, out Quaternion spawnRot);

        if (!IsSpawned)
        {
            localHealth = maxHealth;
            if (playerMovement != null)
            {
                playerMovement.TeleportTo(spawnPos, spawnRot, enableController: playerMovement.IsGameplaySceneActive);
            }
            else
            {
                transform.SetPositionAndRotation(spawnPos, spawnRot);
            }

            ExitDeadState();
            OnHealthChanged?.Invoke(localHealth, maxHealth);
            return;
        }

        if (IsServer)
        {
            ExecuteServerRespawn(spawnPos, spawnRot);
        }
        else
        {
            RequestRespawnServerRpc();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestRespawnServerRpc()
    {
        GetRespawnTransform(out Vector3 spawnPos, out Quaternion spawnRot);
        ExecuteServerRespawn(spawnPos, spawnRot);
    }

    private void ExecuteServerRespawn(Vector3 spawnPos, Quaternion spawnRot)
    {
        invulnerabilityTimer = spawnInvulnerabilityDuration;
        RespawnClientRpc(spawnPos, spawnRot);
        netCurrentHealth.Value = maxHealth;
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void RespawnClientRpc(Vector3 spawnPos, Quaternion spawnRot)
    {
        if (playerMovement != null)
        {
            playerMovement.TeleportTo(spawnPos, spawnRot, enableController: playerMovement.IsGameplaySceneActive);
        }
        else
        {
            transform.SetPositionAndRotation(spawnPos, spawnRot);
        }

        ExitDeadState();
    }

    private void GetRespawnTransform(out Vector3 pos, out Quaternion rot)
    {
        Transform spawn = IsSpawned
            ? NetworkPlayerUtility.GetActiveSceneSpawnForClient(OwnerClientId)
            : NetworkPlayerUtility.GetActiveSceneSpawnForClient(0);

        if (spawn != null)
        {
            pos = spawn.position;
            rot = spawn.rotation;
        }
        else
        {
            pos = transform.position;
            rot = transform.rotation;
        }
    }

    private void TriggerHitFeedback(Vector3 hitDirection, float knockbackForce)
    {
        // 1. Iniciar destello rojo en modelo 3D (y pantalla si es el jugador local)
        flashTimer = hitFlashDuration;
        isFlashing = true;
        ApplyFlashVisuals(1f);

        // 2. Aplicar empuje físico si este cliente es el dueño del PlayerMovement (o en modo local) y sigue vivo
        bool isLocalAuthority = !IsSpawned || IsOwner;
        if (isLocalAuthority && playerMovement != null && !inDeadState)
        {
            float force = knockbackForce > 0f ? knockbackForce : defaultKnockbackForce;
            if (force > 0f && hitDirection.sqrMagnitude > 0.0001f)
            {
                playerMovement.ApplyKnockback(hitDirection, force, knockbackUpwardForce);
            }
            else if (force > 0f)
            {
                playerMovement.ApplyKnockback(-transform.forward, force * 0.6f, knockbackUpwardForce * 0.7f);
            }
        }
    }

    private void ApplyFlashVisuals(float intensity)
    {
        if (playerMovement != null && playerMovement.ThirdPersonMesh != null && cachedMeshRoot != playerMovement.ThirdPersonMesh)
        {
            CacheBodyRenderers();
        }

        // Tinte rojo sobre los renderers del cuerpo usando MaterialPropertyBlock (0 instancias de Material creadas)
        if (!inDeadState && bodyRenderers != null && propBlock != null)
        {
            Color emission = hitFlashColor * (intensity * 0.65f);
            for (int i = 0; i < bodyRenderers.Length; i++)
            {
                Renderer r = bodyRenderers[i];
                if (r == null) continue;

                Color tinted = Color.Lerp(originalBaseColors[i], hitFlashColor, intensity * 0.85f);
                r.GetPropertyBlock(propBlock);
                propBlock.SetColor(ID_BaseColor, tinted);
                propBlock.SetColor(ID_Color, tinted);
                propBlock.SetColor(ID_EmissionColor, emission);
                r.SetPropertyBlock(propBlock);
            }
        }

        // Destello rojo de pantalla únicamente para el jugador dueño que recibió el golpe
        if (showScreenDamageFlash && (!IsSpawned || IsOwner))
        {
            if (screenFlashImage == null)
            {
                screenFlashImage = EnsureScreenFlashImage();
            }

            if (screenFlashImage != null)
            {
                if (!screenFlashImage.enabled) screenFlashImage.enabled = true;
                Color c = hitFlashColor;
                float minAlpha = inDeadState ? deathScreenOverlayAlpha : 0f;
                c.a = Mathf.Max(minAlpha, intensity * screenFlashMaxAlpha);
                screenFlashImage.color = c;
            }
        }
    }

    private void ClearFlashVisuals()
    {
        isFlashing = false;
        flashTimer = 0f;

        if (bodyRenderers != null)
        {
            for (int i = 0; i < bodyRenderers.Length; i++)
            {
                Renderer r = bodyRenderers[i];
                if (r != null) r.SetPropertyBlock(null);
            }
        }

        if (screenFlashImage != null && screenFlashImage.enabled)
        {
            screenFlashImage.enabled = false;
        }
    }

    private Image EnsureScreenFlashImage()
    {
        GameObject crosshair = playerMovement != null ? playerMovement.CrosshairUI : null;
        Canvas targetCanvas = crosshair != null ? crosshair.GetComponentInParent<Canvas>(true) : GetComponentInChildren<Canvas>(true);
        if (targetCanvas == null) return null;

        Transform existing = targetCanvas.transform.Find("DamageFlashOverlay");
        if (existing != null && existing.TryGetComponent(out Image existingImg))
        {
            return existingImg;
        }

        var obj = new GameObject("DamageFlashOverlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var rect = (RectTransform)obj.transform;
        rect.SetParent(targetCanvas.transform, false);
        rect.SetAsFirstSibling();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var img = obj.GetComponent<Image>();
        img.color = new Color(hitFlashColor.r, hitFlashColor.g, hitFlashColor.b, 0f);
        img.raycastTarget = false;
        img.enabled = false;
        return img;
    }

    private void OnNetHealthChanged(float previousValue, float newValue)
    {
        OnHealthChanged?.Invoke(newValue, maxHealth);

        if (newValue < previousValue)
        {
            OnDamaged?.Invoke(previousValue - newValue, newValue);
        }

        if (newValue <= 0f && !inDeadState)
        {
            EnterDeadState();
        }
        else if (newValue > 0f && inDeadState)
        {
            ExitDeadState();
        }
    }

    /// <summary>
    /// Aplica daño sin dirección específica (usa el empuje por defecto hacia atrás).
    /// </summary>
    public void TakeDamage(float amount)
    {
        TakeDamage(amount, Vector3.zero, -1f);
    }

    /// <summary>
    /// Aplica daño junto con una dirección y fuerza de empuje (knockback) y destello rojo en red y local.
    /// </summary>
    public void TakeDamage(float amount, Vector3 hitDirection, float knockbackForce = -1f)
    {
        if (amount <= 0f || IsInvulnerable || IsDead) return;

        float force = knockbackForce >= 0f ? knockbackForce : defaultKnockbackForce;

        if (!IsSpawned)
        {
            ApplyLocalDamage(amount, hitDirection, force);
            return;
        }

        if (IsServer)
        {
            ApplyServerDamage(amount, hitDirection, force);
        }
        else
        {
            TakeDamageServerRpc(amount, hitDirection, force);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void TakeDamageServerRpc(float amount, Vector3 hitDirection, float knockbackForce)
    {
        ApplyServerDamage(amount, hitDirection, knockbackForce);
    }

    private void ApplyServerDamage(float amount, Vector3 hitDirection, float knockbackForce)
    {
        if (amount <= 0f || IsInvulnerable || netCurrentHealth.Value <= 0f) return;

        float next = Mathf.Max(0f, netCurrentHealth.Value - amount);
        netCurrentHealth.Value = next;

        if (next <= 0f && !inDeadState)
        {
            EnterDeadState();
        }

        // Notificar a todos los clientes el destello visual y al dueño el empuje físico
        HitFeedbackClientRpc(hitDirection, knockbackForce);
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void HitFeedbackClientRpc(Vector3 hitDirection, float knockbackForce)
    {
        TriggerHitFeedback(hitDirection, knockbackForce);
    }

    private void ApplyLocalDamage(float amount, Vector3 hitDirection, float knockbackForce)
    {
        float prev = localHealth;
        localHealth = Mathf.Max(0f, localHealth - amount);

        if (Mathf.Abs(localHealth - prev) > 0.0001f)
        {
            OnHealthChanged?.Invoke(localHealth, maxHealth);
            OnDamaged?.Invoke(prev - localHealth, localHealth);
            TriggerHitFeedback(hitDirection, knockbackForce);
        }

        if (localHealth <= 0f && !inDeadState)
        {
            EnterDeadState();
        }
    }

    /// <summary>
    /// Cura al jugador hasta el máximo de vida.
    /// </summary>
    public void Heal(float amount)
    {
        if (amount <= 0f || IsDead) return;

        if (!IsSpawned)
        {
            float prev = localHealth;
            localHealth = Mathf.Min(maxHealth, localHealth + amount);
            if (Mathf.Abs(localHealth - prev) > 0.0001f)
            {
                OnHealthChanged?.Invoke(localHealth, maxHealth);
            }
            return;
        }

        if (IsServer)
        {
            netCurrentHealth.Value = Mathf.Min(maxHealth, netCurrentHealth.Value + amount);
        }
        else
        {
            HealServerRpc(amount);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void HealServerRpc(float amount)
    {
        if (amount <= 0f || netCurrentHealth.Value <= 0f) return;
        netCurrentHealth.Value = Mathf.Min(maxHealth, netCurrentHealth.Value + amount);
    }

    /// <summary>
    /// Restaura la vida al máximo y revive al jugador en el lugar si estaba muerto.
    /// </summary>
    public void ResetHealth()
    {
        ClearFlashVisuals();
        localHealth = maxHealth;

        if (!IsSpawned)
        {
            if (inDeadState) ExitDeadState();
            OnHealthChanged?.Invoke(localHealth, maxHealth);
            return;
        }

        if (IsServer)
        {
            netCurrentHealth.Value = maxHealth;
            if (inDeadState) ExitDeadState();
        }
        else
        {
            ResetHealthServerRpc();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ResetHealthServerRpc()
    {
        netCurrentHealth.Value = maxHealth;
        if (inDeadState) ExitDeadState();
    }
}
