using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Administrador persistente de transiciones y fundidos a negro entre escenas (DontDestroyOnLoad).
/// Garantiza transiciones completamente fluidas sin tirones, parpadeos de cielo/geometría ni artefactos visuales.
/// </summary>
#pragma warning disable CS0649
[DisallowMultipleComponent]
public class SceneTransitionOverlay : MonoBehaviour
{
    private const float ClearThreshold = 0.001f;
    private const string OverlayImageName = "FadeOverlayImage";

    private static readonly Color OpaqueBlack = new Color(0f, 0f, 0f, 1f);
    private static readonly Color TransparentBlack = new Color(0f, 0f, 0f, 0f);

    private static SceneTransitionOverlay instance;
    private static bool isApplicationQuitting;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        isApplicationQuitting = false;
    }

    [Header("Overlay Canvas Settings")]
    [SerializeField] private int overlaySortingOrder = 32767;
    [SerializeField] private Vector2 referenceResolution = new Vector2(1920, 1080);

    [Header("Fade Settings")]
    [Tooltip("Límite máximo de delta time por frame en fade para evitar saltos bruscos.")]
    [SerializeField] private float maxDeltaTimeStep = 0.05f;

    public static SceneTransitionOverlay Instance
    {
        get
        {
            if (isApplicationQuitting || !Application.isPlaying) return null;

            if (instance == null)
            {
                instance = FindAnyObjectByType<SceneTransitionOverlay>();
                if (instance == null)
                {
                    GameObject go = new GameObject("[SceneTransitionOverlay]");
                    instance = go.AddComponent<SceneTransitionOverlay>();
                }
            }
            return instance;
        }
    }

    private Canvas overlayCanvas;
    private Image overlayImage;
    private GameObject overlayObject;
    private Coroutine activeFadeCoroutine;
    private TaskCompletionSource<bool> activeFadeTcs;
    private bool isTransitioning;
    private float currentTargetAlpha;

    public bool IsFading => isTransitioning;
    public bool IsBlack => overlayImage != null && overlayObject.activeInHierarchy && overlayImage.color.a >= 0.85f;

    private void Awake()
    {
        useGUILayout = false;
        isApplicationQuitting = false;

        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);

        EnsureCanvasSetup();
    }

    private void OnApplicationQuit()
    {
        isApplicationQuitting = true;
    }

    private void OnDestroy()
    {
        // Liberar cualquier await pendiente para no dejar tareas colgadas
        CompleteActiveTask();

        if (instance == this)
        {
            instance = null;
        }
    }

    private void EnsureCanvasSetup()
    {
        if (overlayCanvas == null)
        {
            if (!TryGetComponent(out overlayCanvas))
            {
                overlayCanvas = gameObject.AddComponent<Canvas>();
            }
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            overlayCanvas.sortingOrder = overlaySortingOrder;

            if (!TryGetComponent(out CanvasScaler scaler))
            {
                scaler = gameObject.AddComponent<CanvasScaler>();
            }
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution;

            // Necesario para que raycastTarget del overlay bloquee clics durante el fundido
            if (!TryGetComponent(out GraphicRaycaster _))
            {
                gameObject.AddComponent<GraphicRaycaster>();
            }
        }

        if (overlayImage == null)
        {
            Transform child = transform.Find(OverlayImageName);
            GameObject imageObj = child != null ? child.gameObject : null;
            if (imageObj == null)
            {
                imageObj = new GameObject(OverlayImageName, typeof(RectTransform));
                imageObj.transform.SetParent(transform, false);

                RectTransform rect = (RectTransform)imageObj.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }

            if (!imageObj.TryGetComponent(out overlayImage))
            {
                overlayImage = imageObj.AddComponent<Image>();
            }
            overlayObject = imageObj;
            overlayImage.color = TransparentBlack;
            overlayImage.raycastTarget = false;
        }
    }

    private void CompleteActiveTask()
    {
        if (activeFadeTcs != null)
        {
            activeFadeTcs.TrySetResult(true);
            activeFadeTcs = null;
        }
    }

    private void StopCurrentFade()
    {
        if (activeFadeCoroutine != null)
        {
            StopCoroutine(activeFadeCoroutine);
            activeFadeCoroutine = null;
        }

        isTransitioning = false;
        CompleteActiveTask();
    }

    /// <summary>
    /// Aplica el estado final del overlay (opaco bloqueando input, o transparente y desactivado).
    /// </summary>
    private void ApplyFinalState(bool black)
    {
        if (black)
        {
            overlayObject.SetActive(true);
            overlayImage.color = OpaqueBlack;
            overlayImage.raycastTarget = true;
            Application.backgroundLoadingPriority = ThreadPriority.High;
        }
        else
        {
            overlayImage.color = TransparentBlack;
            overlayImage.raycastTarget = false;
            overlayObject.SetActive(false);
            Application.backgroundLoadingPriority = ThreadPriority.BelowNormal;
        }
    }

    /// <summary>
    /// Establece inmediatamente la pantalla en negro bloqueando cualquier renderizado previo.
    /// </summary>
    public void SetBlack()
    {
        if (isApplicationQuitting) return;

        // Si ya hay un fundido a negro activo en progreso hacia targetAlpha = 1,
        // no lo abortamos: dejamos que termine su duración completa suavemente
        if (isTransitioning && activeFadeCoroutine != null && currentTargetAlpha >= 0.95f)
        {
            return;
        }

        StopCurrentFade();
        EnsureCanvasSetup();
        ApplyFinalState(black: true);
    }

    /// <summary>
    /// Restablece la pantalla transparente de forma inmediata.
    /// </summary>
    public void SetClear()
    {
        if (isApplicationQuitting) return;

        StopCurrentFade();
        EnsureCanvasSetup();
        ApplyFinalState(black: false);
    }

    /// <summary>
    /// Realiza un fundido a negro (Fade Out) suave y progresivo con tiempo no escalado.
    /// </summary>
    public Task FadeOutAsync(float duration) => StartFade(1f, duration);

    /// <summary>
    /// Realiza un fundido desde negro (Fade In) suave y progresivo hacia la escena cargada.
    /// </summary>
    public Task FadeInAsync(float duration) => StartFade(0f, duration);

    /// <summary>
    /// Espera (con timeout) a que termine cualquier fundido en curso y, si la pantalla está en negro,
    /// revela la escena con un fade in. Pensado para el Start() de cada escena tras una transición.
    /// </summary>
    public async Task RevealSceneAsync(float duration, Func<bool> isCancelled = null, float waitTimeout = 2f)
    {
        if (!IsBlack && !isTransitioning) return;

        float timeout = waitTimeout;
        while (isTransitioning && timeout > 0f)
        {
            await Awaitable.NextFrameAsync();
            if (isApplicationQuitting || this == null || (isCancelled != null && isCancelled())) return;
            timeout -= Time.unscaledDeltaTime;
        }

        if (isApplicationQuitting || this == null || (isCancelled != null && isCancelled())) return;
        await FadeInAsync(duration);
    }

    private Task StartFade(float targetAlpha, float duration)
    {
        if (isApplicationQuitting || !Application.isPlaying) return Task.CompletedTask;

        EnsureCanvasSetup();
        if (overlayImage == null) return Task.CompletedTask;

        StopCurrentFade();

        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
        activeFadeTcs = tcs;
        activeFadeCoroutine = StartCoroutine(FadeRoutine(targetAlpha, duration, tcs));
        return tcs.Task;
    }

    private void FinishFade(TaskCompletionSource<bool> tcs)
    {
        isTransitioning = false;
        activeFadeCoroutine = null;
        if (activeFadeTcs == tcs) activeFadeTcs = null;
        tcs.TrySetResult(true);
    }

    private IEnumerator FadeRoutine(float targetAlpha, float duration, TaskCompletionSource<bool> tcs)
    {
        isTransitioning = true;
        currentTargetAlpha = targetAlpha;
        duration = Mathf.Max(0.01f, duration);

        float startAlpha = overlayImage.color.a;
        bool toClear = targetAlpha <= ClearThreshold;

        // Si ya está transparente y el objetivo es transparente, omitir para evitar tirones
        if (toClear && startAlpha <= ClearThreshold)
        {
            ApplyFinalState(black: false);
            FinishFade(tcs);
            yield break;
        }

        overlayObject.SetActive(true);

        if (!toClear && targetAlpha > 0.5f)
        {
            // Fade out hacia negro: priorizar hilos de carga y bloquear raycasts
            Application.backgroundLoadingPriority = ThreadPriority.High;
            overlayImage.raycastTarget = true;
        }
        else
        {
            overlayImage.raycastTarget = false;
        }

        float elapsed = 0f;
        float invDuration = 1f / duration;
        Color c = overlayImage.color;

        while (elapsed < duration)
        {
            yield return null;

            if (isApplicationQuitting || !Application.isPlaying)
            {
                // Completar la tarea para que ningún await quede colgado al salir
                FinishFade(tcs);
                yield break;
            }

            // Clamping del delta time para evitar que los tirones de carga de escena se coman la animación
            elapsed += Mathf.Min(Time.unscaledDeltaTime, maxDeltaTimeStep);

            float smoothT = Mathf.SmoothStep(0f, 1f, elapsed * invDuration);
            c.a = Mathf.Lerp(startAlpha, targetAlpha, smoothT);
            overlayImage.color = c;
        }

        if (toClear)
        {
            ApplyFinalState(black: false);
        }
        else
        {
            c.a = targetAlpha;
            overlayImage.color = c;
            overlayImage.raycastTarget = true;
        }

        FinishFade(tcs);
    }
}
