using UnityEngine;

/// <summary>
/// Post-proceso pixel art de un único pase para la cámara (Built-in Render Pipeline).
///
/// El shader hace en un solo Blit:
///   ① Snap de UV → limita la resolución visualmente sin RenderTexture intermedia.
///   ② Cuantización de color → paleta reducida.
///
/// Colores totales = colorsPerChannel³:
///   2 →    8 (CGA mono) | 4 →    64 (Game Boy) | 8 →  512 (NES)
///   16 → 4096 (EGA)     | 32 → ~32K (15-bit)   | 256 → sin límite
///
/// OPTIMIZACIONES respecto a la versión anterior:
///   • Sin RenderTexture de baja resolución (elimina una RT, un Blit y sus allocaciones).
///   • PropertyToID cacheados como static: sin búsqueda de string por frame.
///   • Dirty-flag: SetVector solo cuando cambian parámetros (no cada frame).
///   • Todos los valores costosos (divisiones) se precalculan en C# y se envían
///     como dos Vector4 → el shader solo ejecuta multiplicaciones.
///   • Sin OnGUI en editor (string allocation por frame).
///   • Sin Debug.Log en runtime.
///
/// USO: Adjunta a la cámara principal. Requiere Built-in RP (no URP/HDRP).
/// </summary>
[RequireComponent(typeof(Camera))]
[ExecuteAlways]
public class PixelArtCamera : MonoBehaviour
{
    [Header("Resolución de render")]
    [Tooltip("Ancho del área de píxeles virtuales. 320 = retro 16:9.")]
    public int renderWidth = 320;
    [Tooltip("Alto del área de píxeles virtuales.")]
    public int renderHeight = 180;

    [Header("Paleta de colores")]
    [Tooltip("Tonos por canal RGB. colorsPerChannel³ = colores totales.")]
    [Range(2, 256)]
    public int colorsPerChannel = 8;

    [Header("Shader")]
    [Tooltip("Shader Hidden/PixelArtPostProcess. Se autoasigna en el editor; mantenerlo referenciado garantiza que se incluya en el build.")]
    [SerializeField] private Shader pixelShader;

    // ── IDs de propiedades cacheados ────────────────────────────────────────
    // static readonly: se resuelven una sola vez, compartidos por todas las instancias.
    // Evitan la búsqueda de string en la tabla hash de shaders (~60 veces/seg por prop.).
    private static readonly int ID_RenderRes   = Shader.PropertyToID("_RenderRes");
    private static readonly int ID_QuantParams = Shader.PropertyToID("_QuantParams");

    // ── Estado interno ───────────────────────────────────────────────────────
    private Material pixelMat;
    private bool hasMaterial;

    // Dirty-flag: valores enviados al shader la última vez.
    // SetVector solo se llama cuando algo cambia (no cada frame).
    private int _lastW = -1, _lastH = -1, _lastColors = -1;

    // ═════════════════════ Ciclo de vida ═════════════════════════════════════

    private void Awake()
    {
        useGUILayout = false;
    }

    private void Reset()
    {
        if (!pixelShader) pixelShader = Shader.Find("Hidden/PixelArtPostProcess");
    }

    void OnEnable()  => EnsureMaterial();

    void OnDisable() => ReleaseMaterial();

    void OnDestroy() => ReleaseMaterial();

    void OnValidate()
    {
        // Clamp de valores (evita divisiones por cero en el shader)
        renderWidth      = Mathf.Max(1, renderWidth);
        renderHeight     = Mathf.Max(1, renderHeight);
        colorsPerChannel = Mathf.Clamp(colorsPerChannel, 2, 256);

        if (!pixelShader) pixelShader = Shader.Find("Hidden/PixelArtPostProcess");

        EnsureMaterial();
        _lastW = -1; // Fuerza actualización del material al cambiar en Inspector
    }

    // ═════════════════════ Render ════════════════════════════════════════════

    // OnRenderImage se invoca automáticamente por Unity al finalizar el render de la cámara.
    // src  = imagen a resolución completa | dest = framebuffer de pantalla.
    void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
#if UNITY_EDITOR
        // No aplicar el efecto de pixel art en la vista de escena (Scene View) del editor
        if (Camera.current != null && Camera.current.cameraType == CameraType.SceneView)
        {
            Graphics.Blit(src, dest);
            return;
        }
#endif

        if (!hasMaterial)
        {
            EnsureMaterial();
            if (!hasMaterial) { Graphics.Blit(src, dest); return; }
        }

        // Actualiza uniforms solo si algo cambió (dirty-flag).
        SyncMaterialIfDirty();

        // Pase único: snap de UV + cuantización → sin RenderTexture intermedia.
        Graphics.Blit(src, dest, pixelMat);
    }

    // ═════════════════════ Helpers ═══════════════════════════════════════════

    private void EnsureMaterial()
    {
        if (hasMaterial && pixelMat != null) return;

        // Fallback a Shader.Find solo si no hay referencia asignada; se guarda en el campo
        // serializado para que el shader quede referenciado y se incluya en el build.
        if (!pixelShader) pixelShader = Shader.Find("Hidden/PixelArtPostProcess");
        if (!pixelShader)
        {
            Debug.LogError(
                "[PixelArtCamera] Shader 'Hidden/PixelArtPostProcess' no encontrado. " +
                "Asígnalo en el campo 'Pixel Shader' del Inspector.", this);
            return;
        }

        if (!pixelShader.isSupported)
        {
            Debug.LogError(
                "[PixelArtCamera] El shader 'Hidden/PixelArtPostProcess' no es compatible con el dispositivo actual.", this);
            return;
        }

        pixelMat = new Material(pixelShader) { hideFlags = HideFlags.HideAndDontSave };
        hasMaterial = true;
        _lastW   = -1; // Fuerza sincronización inicial
    }

    private void ReleaseMaterial()
    {
        hasMaterial = false;
        if (!pixelMat) return;
        DestroyImmediate(pixelMat);
        pixelMat = null;
    }

    // Solo llama a SetVector cuando al menos un parámetro cambió.
    private void SyncMaterialIfDirty()
    {
        if (renderWidth      == _lastW &&
            renderHeight     == _lastH &&
            colorsPerChannel == _lastColors) return;

        // ── Vector 1: resolución + recíprocos ─────────────────────────────────
        // Precalculamos 1/w y 1/h aquí (C# float) para que el shader solo multiplique.
        float rcpW = 1f / renderWidth;
        float rcpH = 1f / renderHeight;
        pixelMat.SetVector(ID_RenderRes, new Vector4(renderWidth, renderHeight, rcpW, rcpH));

        // ── Vector 2: parámetros de cuantización de color ─────────────────────
        //   stepsM1  = colorsPerChannel - 1 (multiplicador de cuantización)
        //   stepsRcp = 1 / stepsM1          (factor de normalización)
        float stepsM1  = colorsPerChannel - 1f;
        float stepsRcp = 1f / stepsM1;
        pixelMat.SetVector(ID_QuantParams, new Vector4(stepsM1, stepsRcp, 0f, 0f));

        // Guarda estado actual
        _lastW      = renderWidth;
        _lastH      = renderHeight;
        _lastColors = colorsPerChannel;
    }
}
