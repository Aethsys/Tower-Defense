using System.Collections;
using TMPro;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class PauseMenu : NetworkBehaviour
{
    public static bool IsOpen { get; private set; }

    [SerializeField] private string mainMenuSceneName = "Main Menu";
    [SerializeField] private Color panelColor = new Color(0f, 0f, 0f, 0.65f);
    [SerializeField] private Color buttonColor = new Color(0.95f, 0.95f, 0.95f, 1f);
    [SerializeField] private Color buttonTextColor = new Color(0.1f, 0.1f, 0.1f, 1f);

    private GameObject canvasObject;
    private bool isLeaving;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsOwner)
        {
            BuildUi();
        }
    }

    public override void OnNetworkDespawn()
    {
        DestroyUi();
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        DestroyUi();
        base.OnDestroy();
    }

    private void Update()
    {
        if (!IsSpawned || !IsOwner) return;

        if (!IsGameplayScene())
        {
            if (IsOpen) Close();
            return;
        }

        if (canvasObject == null)
        {
            BuildUi();
        }

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (!keyboard.escapeKey.wasPressedThisFrame && !keyboard.pKey.wasPressedThisFrame) return;

        if (IsOpen) Close();
        else Open();
    }

    private void Open()
    {
        IsOpen = true;
        canvasObject.SetActive(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void Close()
    {
        IsOpen = false;
        if (canvasObject != null) canvasObject.SetActive(false);
        StartCoroutine(RelockCursor());
    }

    private IEnumerator RelockCursor()
    {
        yield return null;

        if (!IsOpen)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private async void ExitToMainMenu()
    {
        if (isLeaving) return;
        isLeaving = true;

        try
        {
            ISession session = MenuManagerNetwork.CurrentSession;
            if (session != null && session.State == SessionState.Connected)
            {
                await session.LeaveAsync();
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Warning while leaving session: " + e.Message);
        }

        MenuManagerNetwork.ClearCurrentSession();

        if (!Application.isPlaying) return;

        NetworkManager manager = NetworkManager.Singleton;
        if (manager != null && (manager.IsListening || manager.IsConnectedClient || manager.IsServer))
        {
            manager.Shutdown();
        }

        IsOpen = false;
        SceneManager.LoadScene(mainMenuSceneName, LoadSceneMode.Single);
    }

    private static bool IsGameplayScene()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        return sceneName != "Main Menu" && sceneName != "Lobby";
    }

    private void BuildUi()
    {
        if (canvasObject != null) return;

        if (EventSystem.current == null)
        {
            GameObject eventSystem = new GameObject("PauseEventSystem");
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<InputSystemUIInputModule>();
        }

        canvasObject = new GameObject("PauseMenuCanvas");
        Canvas canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasObject.AddComponent<GraphicRaycaster>();

        GameObject panel = CreateChild("Panel", canvasObject.transform);
        Image panelImage = panel.AddComponent<Image>();
        panelImage.color = panelColor;
        Stretch(panel.GetComponent<RectTransform>());

        CreateLabel("Title", panel.transform, "Pausa", 72f, new Vector2(0f, 190f), new Vector2(800f, 120f), Color.white);
        CreateButton("ResumeButton", panel.transform, "Continuar", new Vector2(0f, 40f), Close);
        CreateButton("ExitButton", panel.transform, "Salir al menu", new Vector2(0f, -70f), ExitToMainMenu);

        canvasObject.SetActive(false);
    }

    private void DestroyUi()
    {
        if (IsOpen && IsOwner) IsOpen = false;

        if (canvasObject != null)
        {
            Destroy(canvasObject);
            canvasObject = null;
        }
    }

    private static GameObject CreateChild(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static TextMeshProUGUI CreateLabel(string name, Transform parent, string text, float size, Vector2 position, Vector2 dimensions, Color color)
    {
        GameObject go = CreateChild(name, parent);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = dimensions;

        TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = size;
        label.alignment = TextAlignmentOptions.Center;
        label.color = color;
        label.raycastTarget = false;
        return label;
    }

    private void CreateButton(string name, Transform parent, string text, Vector2 position, UnityEngine.Events.UnityAction onClick)
    {
        GameObject go = CreateChild(name, parent);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(420f, 90f);

        Image image = go.AddComponent<Image>();
        image.color = buttonColor;

        Button button = go.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(onClick);

        CreateLabel("Label", go.transform, text, 44f, Vector2.zero, rect.sizeDelta, buttonTextColor);
    }
}
