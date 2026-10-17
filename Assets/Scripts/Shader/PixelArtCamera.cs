using UnityEngine;

[RequireComponent(typeof(Camera))]
[ExecuteAlways]
public class PixelArtCamera : MonoBehaviour
{
    [Header("Render Resolution")]
    public int renderWidth = 320;
    public int renderHeight = 180;

    [Header("Color Palette")]
    [Range(2, 256)]
    public int colorsPerChannel = 8;

    [Header("Shader")]
    [SerializeField] private Shader pixelShader;

    private static readonly int ID_RenderRes   = Shader.PropertyToID("_RenderRes");
    private static readonly int ID_QuantParams = Shader.PropertyToID("_QuantParams");

    private Material pixelMat;
    private bool hasMaterial;

    private int _lastW = -1, _lastH = -1, _lastColors = -1;

    private void Awake()
    {
        useGUILayout = false;
    }

    private void Reset()
    {
        if (!pixelShader) pixelShader = Shader.Find("Hidden/PixelArtPostProcess");
    }

    private void OnEnable() => EnsureMaterial();

    private void OnDisable() => ReleaseMaterial();

    private void OnDestroy() => ReleaseMaterial();

    private void OnValidate()
    {
        renderWidth      = Mathf.Max(1, renderWidth);
        renderHeight     = Mathf.Max(1, renderHeight);
        colorsPerChannel = Mathf.Clamp(colorsPerChannel, 2, 256);

        if (!pixelShader) pixelShader = Shader.Find("Hidden/PixelArtPostProcess");

        EnsureMaterial();
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

        if (!hasMaterial)
        {
            EnsureMaterial();
            if (!hasMaterial)
            {
                Graphics.Blit(src, dest);
                return;
            }
        }

        SyncMaterialIfDirty();
        Graphics.Blit(src, dest, pixelMat);
    }

    private void EnsureMaterial()
    {
        if (hasMaterial && pixelMat != null) return;

        if (!pixelShader) pixelShader = Shader.Find("Hidden/PixelArtPostProcess");
        if (!pixelShader)
        {
            Debug.LogError(
                "[PixelArtCamera] Shader 'Hidden/PixelArtPostProcess' not found. " +
                "Assign it in the 'Pixel Shader' field in the Inspector.", this);
            return;
        }

        if (!pixelShader.isSupported)
        {
            Debug.LogError(
                "[PixelArtCamera] Shader 'Hidden/PixelArtPostProcess' is not supported on this device.", this);
            return;
        }

        pixelMat = new Material(pixelShader) { hideFlags = HideFlags.HideAndDontSave };
        hasMaterial = true;
        _lastW = -1;
    }

    private void ReleaseMaterial()
    {
        hasMaterial = false;
        if (!pixelMat) return;
        DestroyImmediate(pixelMat);
        pixelMat = null;
    }

    private void SyncMaterialIfDirty()
    {
        if (renderWidth      == _lastW &&
            renderHeight     == _lastH &&
            colorsPerChannel == _lastColors) return;

        float rcpW = 1f / renderWidth;
        float rcpH = 1f / renderHeight;
        pixelMat.SetVector(ID_RenderRes, new Vector4(renderWidth, renderHeight, rcpW, rcpH));

        float stepsM1  = colorsPerChannel - 1f;
        float stepsRcp = 1f / stepsM1;
        pixelMat.SetVector(ID_QuantParams, new Vector4(stepsM1, stepsRcp, 0f, 0f));

        _lastW      = renderWidth;
        _lastH      = renderHeight;
        _lastColors = colorsPerChannel;
    }
}
