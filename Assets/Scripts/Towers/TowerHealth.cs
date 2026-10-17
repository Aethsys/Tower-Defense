using System;
using TMPro;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class TowerHealth : NetworkBehaviour
{
    [Header("Configuración de Vida")]
    [SerializeField] private int maxHealth = 100;

    [Header("UI de la Torre")]
    [SerializeField] private TMP_Text healthText;
    [SerializeField] private bool lookAtLocalCamera = true;
    [SerializeField] private bool lockVerticalRotation = true;

    private readonly NetworkVariable<int> currentHealth = new NetworkVariable<int>(
        100,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private Camera localCamera;

    public int MaxHealth => maxHealth;
    public int CurrentHealth => IsSpawned ? currentHealth.Value : maxHealth;
    public bool IsDestroyed => CurrentHealth <= 0;

    public event Action<int, int> OnHealthChanged;
    public event Action<TowerHealth> OnTowerDestroyed;

    private void Awake()
    {
        if (healthText == null)
        {
            healthText = GetComponentInChildren<TMP_Text>(true);
        }
    }

    private void Start()
    {
        if (!IsSpawned)
        {
            UpdateHealthUI(maxHealth);
        }
    }

    private void LateUpdate()
    {
        if (!lookAtLocalCamera || healthText == null)
        {
            return;
        }

        Camera cam = GetLocalActiveCamera();
        if (cam == null)
        {
            return;
        }

        Vector3 directionFromCamera = healthText.transform.position - cam.transform.position;
        if (lockVerticalRotation)
        {
            directionFromCamera.y = 0f;
        }

        if (directionFromCamera.sqrMagnitude > 0.0001f)
        {
            healthText.transform.rotation = Quaternion.LookRotation(directionFromCamera.normalized, Vector3.up);
        }
    }

    private Camera GetLocalActiveCamera()
    {
        if (localCamera != null && localCamera.isActiveAndEnabled)
        {
            return localCamera;
        }

        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.SpawnManager != null &&
            NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject() != null)
        {
            NetworkObject localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
            localCamera = localPlayer.GetComponentInChildren<Camera>(false);
            if (localCamera != null && localCamera.isActiveAndEnabled)
            {
                return localCamera;
            }
        }

        if (Camera.main != null && Camera.main.isActiveAndEnabled)
        {
            localCamera = Camera.main;
            return localCamera;
        }

        return null;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        currentHealth.OnValueChanged += HandleHealthChanged;

        if (IsServer)
        {
            currentHealth.Value = maxHealth;
        }

        UpdateHealthUI(currentHealth.Value);
    }

    public override void OnNetworkDespawn()
    {
        currentHealth.OnValueChanged -= HandleHealthChanged;
        base.OnNetworkDespawn();
    }

    /// <summary>
    /// Aplica daño a la torre. Si se llama desde el servidor lo aplica directamente;
    /// si se llama desde un cliente, envía la petición al servidor.
    /// </summary>
    public void TakeDamage(int damage)
    {
        if (damage <= 0) return;

        if (!IsSpawned)
        {
            int fallbackHealth = Mathf.Max(0, maxHealth - damage);
            UpdateHealthUI(fallbackHealth);
            return;
        }

        if (IsServer)
        {
            ApplyDamageInternal(damage);
        }
        else
        {
            TakeDamageServerRpc(damage);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TakeDamageServerRpc(int damage)
    {
        ApplyDamageInternal(damage);
    }

    private void ApplyDamageInternal(int damage)
    {
        if (!IsServer || damage <= 0 || currentHealth.Value <= 0)
        {
            return;
        }

        int nextHealth = Mathf.Max(0, currentHealth.Value - damage);
        currentHealth.Value = nextHealth;
    }

    private void HandleHealthChanged(int previousValue, int newValue)
    {
        UpdateHealthUI(newValue);
        OnHealthChanged?.Invoke(newValue, maxHealth);

        if (previousValue > 0 && newValue <= 0)
        {
            OnTowerDestroyed?.Invoke(this);
        }
    }

    private void UpdateHealthUI(int healthValue)
    {
        if (healthText != null)
        {
            healthText.text = healthValue.ToString();
        }
    }
}
