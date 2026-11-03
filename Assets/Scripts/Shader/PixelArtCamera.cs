using UnityEngine;

#pragma warning disable CS0649
[RequireComponent(typeof(Camera))]
[ExecuteAlways]
[DisallowMultipleComponent]
public class PixelArtCamera : MonoBehaviour
{
    private const string DefaultShaderName = "Hidden/PixelArtPostProcess";

    [Header("Render Resolution")]
    [Tooltip("Si está activo, el ancho se calcula a partir de la altura y la relación de aspecto real de la pantalla (píxeles siempre cuadrados).")]
    public bool matchScreenAspect = true;
    [Min(1)]
    public int renderWidth = 320;
    [Min(1)]
    public int renderHeight = 180;

    [Header("Color Palette")]
    [Range(2, 256)]
    public int colorsPerChannel = 8;

    [Header("Quality")]
    [Tooltip("Intensidad del dithering ordenado (Bayer 4x4). Suaviza los degradados con pocos colores.")]
    [Range(0f, 1f)]
    public float ditherStrength = 0.5f;
    [Tooltip("Promedia varias muestras dentro de cada píxel para reducir parpadeo y bordes serrados.")]
    [Range(0f, 1f)]
    public float cellFilter = 1f;

    [Header("Shader")]
    [SerializeField] private Shader pixelShader;

    private static readonly int ID_RenderRes   = Shader.PropertyToID("_RenderRes");
    private static readonly int ID_QuantParams = Shader.PropertyToID("_QuantParams");
    private static readonly int ID_EffectParams = Shader.PropertyToID("_EffectParams");

    private Material pixelMat;
    private bool hasMaterial;
    private bool hasLoggedShaderError;

    private int _lastW = -1;
    private int _lastH = -1;
    private int _lastColors = -1;
    private float _lastDither = -1f;
    private float _lastFilter = -1f;

    private void Awake()
    {
        useGUILayout = false;
    }

    private void Reset()
    {
        if (!pixelShader)
        {
            pixelShader = Shader.Find(DefaultShaderName);
        }
    }

    private void OnEnable()
    {
        EnsureMaterial();
    }

    private void OnDisable()
    {
        ReleaseMaterial();
    }

    private void OnDestroy()
    {
        ReleaseMaterial();
    }

    private void OnValidate()
    {
        renderWidth      = Mathf.Max(1, renderWidth);
        renderHeight     = Mathf.Max(1, renderHeight);
        colorsPerChannel = Mathf.Clamp(colorsPerChannel, 2, 256);
        ditherStrength   = Mathf.Clamp01(ditherStrength);
        cellFilter       = Mathf.Clamp01(cellFilter);

        if (!pixelShader)
        {
            pixelShader = Shader.Find(DefaultShaderName);
        }

        // Solo crear el material si el componente está activo: si está desactivado no habrá
        // OnDisable que lo libere y quedaría huérfano (HideAndDontSave) en el editor.
        if (isActiveAndEnabled)
        {
            EnsureMaterial();
        }
        _lastW = -1;
    }

    private void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
#if UNITY_EDITOR
        if (Camera.current != null && Camera.current.cameraType == CameraType.SceneView)
        {
            Graphics.Blit(src, dest);
            return;
        }
#endif

        if (!hasMaterial || pixelMat == null)
        {
            hasMaterial = false;
            EnsureMaterial();
            if (!hasMaterial)
            {
                Graphics.Blit(src, dest);
                return;
            }
        }

        SyncMaterialIfDirty(src);
        Graphics.Blit(src, dest, pixelMat);
    }

    private void EnsureMaterial()
    {
        if (hasMaterial && pixelMat != null) return;

        if (!pixelShader)
        {
            pixelShader = Shader.Find(DefaultShaderName);
        }

        if (!pixelShader)
        {
            LogShaderErrorOnce($"[PixelArtCamera] Shader '{DefaultShaderName}' not found. Assign it in the 'Pixel Shader' field in the Inspector.");
            return;
        }

        if (!pixelShader.isSupported)
        {
            LogShaderErrorOnce($"[PixelArtCamera] Shader '{DefaultShaderName}' is not supported on this device.");
            return;
        }

        pixelMat = new Material(pixelShader) { hideFlags = HideFlags.HideAndDontSave };
        hasMaterial = true;
        hasLoggedShaderError = false;
        _lastW = -1;
    }

    // OnRenderImage reintenta crear el material cada frame: evitar inundar la consola con el mismo error
    private void LogShaderErrorOnce(string message)
    {
        if (hasLoggedShaderError) return;
        hasLoggedShaderError = true;
        Debug.LogError(message, this);
    }

    private void ReleaseMaterial()
    {
        hasMaterial = false;
        if (pixelMat == null) return;

        if (Application.isPlaying)
        {
            Destroy(pixelMat);
        }
        else
        {
            DestroyImmediate(pixelMat);
        }

        pixelMat = null;
    }

    private void SyncMaterialIfDirty(RenderTexture src)
    {
        int height = Mathf.Max(1, renderHeight);
        int width = matchScreenAspect && src != null && src.height > 0
            ? Mathf.Max(1, Mathf.RoundToInt(height * (src.width / (float)src.height)))
            : Mathf.Max(1, renderWidth);

        if (width == _lastW &&
            height == _lastH &&
            colorsPerChannel == _lastColors &&
            Mathf.Approximately(ditherStrength, _lastDither) &&
            Mathf.Approximately(cellFilter, _lastFilter))
        {
            return;
        }

        pixelMat.SetVector(ID_RenderRes, new Vector4(width, height, 1f / width, 1f / height));

        float stepsM1 = Mathf.Max(1f, colorsPerChannel - 1f);
        pixelMat.SetVector(ID_QuantParams, new Vector4(stepsM1, 1f / stepsM1, 0f, 0f));
        pixelMat.SetVector(ID_EffectParams, new Vector4(ditherStrength, cellFilter, 0f, 0f));

        _lastW      = width;
        _lastH      = height;
        _lastColors = colorsPerChannel;
        _lastDither = ditherStrength;
        _lastFilter = cellFilter;
    }
}