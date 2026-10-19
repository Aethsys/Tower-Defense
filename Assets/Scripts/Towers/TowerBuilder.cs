using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class TowerBuilder : NetworkBehaviour
{
    [Serializable]
    public struct TowerOption
    {
        public string displayName;
        public NetworkObject prefab;
    }

    private static readonly Dictionary<NetworkObject, List<NetworkObject>> SpawnedByPrefab =
        new Dictionary<NetworkObject, List<NetworkObject>>(4);
    private static readonly Collider[] OverlapBuffer = new Collider[32];

    [Header("References")]
    [SerializeField] private TowerOption[] options;
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private PlayerCombat playerCombat;
    [SerializeField] private PlayerMovement playerMovement;

    [Header("Placement")]
    [SerializeField] private LayerMask groundLayers = 1;
    [SerializeField, Min(1f)] private float maxPlaceDistance = 8f;
    [SerializeField, Range(0f, 1f)] private float minGroundNormalY = 0.7f;
    [SerializeField, Min(0.1f)] private float clearRadius = 1.2f;

    [Header("Construction")]
    [SerializeField, Min(0.5f)] private float buildDuration = 4f;
    [SerializeField, Min(0.2f)] private float hammerInterval = 0.7f;

    [Header("Limits")]
    [SerializeField, Min(0f)] private float placeCooldown = 40f;
    [SerializeField, Min(1)] private int maxTowersPerType = 5;

    [Header("Audio")]
    [SerializeField] private SoundData buildSound;

    [Header("Preview")]
    [SerializeField] private Color validColor = new Color(0.2f, 1f, 0.3f, 0.45f);
    [SerializeField] private Color invalidColor = new Color(1f, 0.25f, 0.2f, 0.45f);

    private GameObject[] ghosts;
    private Material validMaterial;
    private Material invalidMaterial;
    private int selectedIndex = -1;
    private float nextPlaceTime;
    private float serverNextPlaceTime;
    private bool canPlaceNow;
    private bool isBuilding;
    private float buildEndsAt;
    private bool serverBuilding;
    private GUIStyle labelStyle;

    private bool IsLocalControl => !IsSpawned || IsOwner;

    private void Awake()
    {
        if (cameraTransform == null)
        {
            Camera cam = GetComponentInChildren<Camera>(true);
            if (cam != null) cameraTransform = cam.transform;
        }

        if (playerCombat == null)
        {
            playerCombat = GetComponent<PlayerCombat>();
        }

        if (playerMovement == null)
        {
            playerMovement = GetComponent<PlayerMovement>();
        }
    }

    private void OnDisable()
    {
        CancelBuild();
    }

    public override void OnDestroy()
    {
        if (ghosts != null)
        {
            for (int i = 0; i < ghosts.Length; i++)
            {
                if (ghosts[i] != null) Destroy(ghosts[i]);
            }
        }

        if (validMaterial != null) Destroy(validMaterial);
        if (invalidMaterial != null) Destroy(invalidMaterial);

        base.OnDestroy();
    }

    private void Update()
    {
        if (!IsLocalControl || !IsSpawned) return;

        if (!IsGameplayScene() || Cursor.lockState != CursorLockMode.Locked)
        {
            CancelBuild();
            return;
        }

        Keyboard keyboard = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (keyboard == null || mouse == null) return;

        if (isBuilding)
        {
            if (Time.time >= buildEndsAt + 2f) EndBuild();
            return;
        }

        HandleSelectionInput(keyboard);

        if (selectedIndex < 0) return;

        if (keyboard.qKey.wasPressedThisFrame)
        {
            CancelBuild();
            return;
        }

        UpdatePreview();

        if (mouse.leftButton.wasPressedThisFrame && canPlaceNow && Time.time >= nextPlaceTime)
        {
            nextPlaceTime = Time.time + buildDuration + placeCooldown;
            StartBuild();
            PlaceTowerServerRpc(selectedIndex, ghosts[selectedIndex].transform.position, ghosts[selectedIndex].transform.eulerAngles.y);
        }
    }

    private void HandleSelectionInput(Keyboard keyboard)
    {
        if (options == null) return;

        int count = Mathf.Min(options.Length, 9);
        for (int i = 0; i < count; i++)
        {
            Key key = Key.Digit1 + i;
            if (!keyboard[key].wasPressedThisFrame) continue;

            if (selectedIndex == i) CancelBuild();
            else SelectTower(i);
            return;
        }
    }

    private void SelectTower(int index)
    {
        if (options[index].prefab == null) return;

        HideGhosts();
        selectedIndex = index;
        EnsureGhost(index);

        if (playerCombat != null) playerCombat.enabled = false;
    }

    private void StartBuild()
    {
        isBuilding = true;
        buildEndsAt = Time.time + buildDuration;
        SetMovementLocked(true);
        ApplyGhostMaterial(ghosts[selectedIndex], validMaterial);
    }

    private void EndBuild()
    {
        isBuilding = false;
        SetMovementLocked(false);
        CancelBuild();
    }

    private void SetMovementLocked(bool locked)
    {
        if (playerMovement != null) playerMovement.MovementLocked = locked;
    }

    private void CancelBuild()
    {
        if (isBuilding)
        {
            isBuilding = false;
            SetMovementLocked(false);
        }

        if (selectedIndex < 0) return;

        selectedIndex = -1;
        HideGhosts();

        if (playerCombat != null) playerCombat.enabled = true;
    }

    private void UpdatePreview()
    {
        GameObject ghost = ghosts[selectedIndex];
        bool hasSpot = TryGetPlacement(out Vector3 position, out Quaternion rotation);

        ghost.SetActive(hasSpot);
        canPlaceNow = hasSpot && Time.time >= nextPlaceTime && IsSpotClear(position);

        if (!hasSpot) return;

        ghost.transform.SetPositionAndRotation(position, rotation);
        ApplyGhostMaterial(ghost, canPlaceNow ? validMaterial : invalidMaterial);
    }

    private bool TryGetPlacement(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;

        if (cameraTransform == null) return false;

        if (!Physics.Raycast(cameraTransform.position, cameraTransform.forward, out RaycastHit hit,
                maxPlaceDistance, groundLayers, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        if (hit.normal.y < minGroundNormalY) return false;

        Vector3 forward = cameraTransform.forward;
        forward.y = 0f;
        float yaw = forward.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(forward).eulerAngles.y : 0f;

        position = hit.point;
        rotation = Quaternion.Euler(0f, yaw, 0f);
        return true;
    }

    private bool IsSpotClear(Vector3 position)
    {
        Vector3 center = position + Vector3.up * (clearRadius + 0.05f);
        int count = Physics.OverlapSphereNonAlloc(center, clearRadius, OverlapBuffer, ~0, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            Collider col = OverlapBuffer[i];
            if (col == null) continue;

            if (col.GetComponentInParent<TowerHealth>() != null ||
                col.GetComponentInParent<EnemyHealth>() != null ||
                col.GetComponentInParent<PlayerMovement>() != null)
            {
                return false;
            }
        }

        return true;
    }

    [Rpc(SendTo.Server)]
    private void PlaceTowerServerRpc(int optionIndex, Vector3 position, float yaw)
    {
        if (options == null || optionIndex < 0 || optionIndex >= options.Length) return;

        NetworkObject prefab = options[optionIndex].prefab;
        if (prefab == null) return;

        bool valid = !serverBuilding &&
                     Time.time >= serverNextPlaceTime &&
                     (position - transform.position).magnitude <= maxPlaceDistance + 3f &&
                     IsSpotClear(position) &&
                     CountAlive(prefab) < maxTowersPerType;

        if (!valid)
        {
            BuildFinishedRpc();
            return;
        }

        serverBuilding = true;
        serverNextPlaceTime = Time.time + buildDuration + placeCooldown;
        StartCoroutine(BuildRoutine(prefab, position, yaw));
    }

    private IEnumerator BuildRoutine(NetworkObject prefab, Vector3 position, float yaw)
    {
        float elapsed = 0f;
        while (elapsed < buildDuration)
        {
            PlayBuildSoundRpc(position);
            float wait = Mathf.Min(hammerInterval, buildDuration - elapsed);
            yield return new WaitForSeconds(wait);
            elapsed += wait;
        }

        if (IsSpotClear(position) && CountAlive(prefab) < maxTowersPerType)
        {
            NetworkObject tower = Instantiate(prefab, position, Quaternion.Euler(0f, yaw, 0f));
            tower.Spawn();

            if (!SpawnedByPrefab.TryGetValue(prefab, out List<NetworkObject> list))
            {
                list = new List<NetworkObject>(maxTowersPerType);
                SpawnedByPrefab[prefab] = list;
            }
            list.Add(tower);
        }

        serverBuilding = false;
        BuildFinishedRpc();
    }

    [Rpc(SendTo.Owner)]
    private void BuildFinishedRpc()
    {
        EndBuild();
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void PlayBuildSoundRpc(Vector3 position)
    {
        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlaySFXAt(buildSound, position);
        }
    }

    private static int CountAlive(NetworkObject prefab)
    {
        if (!SpawnedByPrefab.TryGetValue(prefab, out List<NetworkObject> list)) return 0;

        int alive = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            NetworkObject tower = list[i];
            if (tower == null || !tower.IsSpawned)
            {
                list.RemoveAt(i);
                continue;
            }

            TowerHealth health = tower.GetComponent<TowerHealth>();
            if (health == null || !health.IsDestroyed) alive++;
        }

        return alive;
    }

    private static bool IsGameplayScene()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        return sceneName != "Main Menu" && sceneName != "Lobby";
    }

    private void EnsureGhost(int index)
    {
        if (ghosts == null || ghosts.Length != options.Length)
        {
            ghosts = new GameObject[options.Length];
        }

        EnsureMaterials();

        if (ghosts[index] != null) return;

        GameObject root = new GameObject("TowerGhost_" + options[index].displayName);
        root.SetActive(false);

        Transform source = options[index].prefab.transform;
        MeshFilter[] filters = options[index].prefab.GetComponentsInChildren<MeshFilter>(true);

        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter filter = filters[i];
            if (filter.sharedMesh == null) continue;

            Matrix4x4 local = source.worldToLocalMatrix * filter.transform.localToWorldMatrix;

            GameObject part = new GameObject("Part");
            part.transform.SetParent(root.transform, false);
            part.transform.localPosition = local.GetColumn(3);
            part.transform.localRotation = local.rotation;
            part.transform.localScale = local.lossyScale;

            part.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            MeshRenderer renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = validMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        ghosts[index] = root;
    }

    private void EnsureMaterials()
    {
        if (validMaterial != null) return;

        Shader shader = Shader.Find("Sprites/Default");
        validMaterial = new Material(shader) { color = validColor };
        invalidMaterial = new Material(shader) { color = invalidColor };
    }

    private static void ApplyGhostMaterial(GameObject ghost, Material material)
    {
        MeshRenderer[] renderers = ghost.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            renderers[i].sharedMaterial = material;
        }
    }

    private void HideGhosts()
    {
        if (ghosts == null) return;

        for (int i = 0; i < ghosts.Length; i++)
        {
            if (ghosts[i] != null) ghosts[i].SetActive(false);
        }
    }

    private void OnGUI()
    {
        if (!IsLocalControl || !IsSpawned || options == null || !IsGameplayScene()) return;
        if (Cursor.lockState != CursorLockMode.Locked) return;

        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            labelStyle.normal.textColor = Color.white;
        }

        string text;
        if (isBuilding)
        {
            float left = Mathf.Max(0f, buildEndsAt - Time.time);
            text = "Construyendo " + options[selectedIndex].displayName + "...   " + left.ToString("0.0") + " s";
        }
        else if (selectedIndex >= 0)
        {
            text = "Construyendo: " + options[selectedIndex].displayName + "   |   Clic izquierdo: colocar   |   Q: cancelar";
        }
        else
        {
            text = string.Empty;
            for (int i = 0; i < options.Length && i < 9; i++)
            {
                text += "[" + (i + 1) + "] " + options[i].displayName + "   ";
            }
        }

        float remaining = isBuilding ? 0f : nextPlaceTime - Time.time;
        if (remaining > 0f)
        {
            text += "   |   Enfriamiento: " + remaining.ToString("0.0") + " s";
        }

        GUI.Label(new Rect(16f, Screen.height - 36f, Screen.width - 32f, 28f), text, labelStyle);

        if (remaining > 0f && placeCooldown > 0f)
        {
            float width = 220f;
            float fill = 1f - Mathf.Clamp01(remaining / placeCooldown);
            Rect bar = new Rect(16f, Screen.height - 54f, width, 10f);
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = new Color(0.3f, 0.9f, 0.4f, 0.95f);
            GUI.DrawTexture(new Rect(bar.x, bar.y, width * fill, bar.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
