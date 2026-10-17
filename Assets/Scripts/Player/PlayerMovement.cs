using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : NetworkBehaviour
{
    [Header("Movement Speeds")]
    [SerializeField] private float walkSpeed = 5f;
    [SerializeField] private float sprintSpeed = 8.5f;
    [SerializeField] private float crouchSpeed = 2.5f;

    [Header("Jump & Gravity")]
    [SerializeField] private float jumpHeight = 1.5f;
    [SerializeField] private float gravity = -19.62f;

    [Header("Crouch Settings")]
    [SerializeField] private float standingHeight = 2f;
    [SerializeField] private float crouchHeight = 1.1f;
    [SerializeField] private float crouchTransitionSpeed = 10f;

    [Header("Camera & Mouse Look")]
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private float mouseSensitivity = 0.15f;
    [SerializeField] private float maxLookAngle = 85f;

    private CharacterController characterController;
    private InputSystem_Actions inputActions;

    private Vector3 verticalVelocity;
    private float xRotation;
    private float initialCameraLocalYaw;
    private float standingCameraY = 0.5f;
    private float crouchCameraY = 0.05f;
    private bool isCrouching;
    private bool isGameplayScene;
    private bool hasInitializedGameplayPosition;

    private readonly NetworkVariable<Vector3> netPosition = new NetworkVariable<Vector3>(
        writePerm: NetworkVariableWritePermission.Owner);

    private readonly NetworkVariable<Quaternion> netRotation = new NetworkVariable<Quaternion>(
        writePerm: NetworkVariableWritePermission.Owner);

    private readonly NetworkVariable<bool> netIsCrouching = new NetworkVariable<bool>(
        writePerm: NetworkVariableWritePermission.Owner);

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();
        if (characterController != null)
        {
            standingHeight = characterController.height > 0f ? characterController.height : 2f;
        }

        if (cameraTransform == null)
        {
            Camera cam = GetComponentInChildren<Camera>(true);
            if (cam != null)
            {
                cameraTransform = cam.transform;
            }
        }

        if (cameraTransform != null)
        {
            initialCameraLocalYaw = cameraTransform.localEulerAngles.y;
            standingCameraY = cameraTransform.localPosition.y;
            crouchCameraY = standingCameraY - ((standingHeight - crouchHeight) * 0.5f);
        }

        inputActions = new InputSystem_Actions();
        RefreshSceneState(SceneManager.GetActiveScene().name);
    }

    private void OnEnable()
    {
        inputActions?.Player.Enable();
        SceneManager.sceneLoaded += OnSceneLoaded;
        RefreshSceneState(SceneManager.GetActiveScene().name);
    }

    private void OnDisable()
    {
        inputActions?.Player.Disable();
        SceneManager.sceneLoaded -= OnSceneLoaded;

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        inputActions?.Dispose();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        hasInitializedGameplayPosition = false;
        RefreshSceneState(scene.name);
    }

    private void RefreshSceneState(string sceneName)
    {
        isGameplayScene = sceneName != "Main Menu" && sceneName != "Lobby";

        if (!isGameplayScene)
        {
            isCrouching = false;
            transform.localScale = Vector3.one;
            if (characterController != null)
            {
                characterController.height = standingHeight;
                characterController.center = Vector3.zero;
            }
        }
    }

    private void Update()
    {
        if (!isGameplayScene)
        {
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            return;
        }

        if (IsSpawned && !IsOwner)
        {
            ApplyRemoteState();
            return;
        }

        if (!hasInitializedGameplayPosition)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            if (IsSpawned)
            {
                netPosition.Value = transform.position;
                netRotation.Value = transform.rotation;
                netIsCrouching.Value = false;
            }
            hasInitializedGameplayPosition = true;
        }

        HandleCursorLock();
        HandleLook();
        HandleCrouch();
        HandleMovementAndJump();

        if (IsSpawned)
        {
            SyncOwnerNetworkState();
        }
    }

    private void SyncOwnerNetworkState()
    {
        Vector3 currentPos = transform.position;
        if ((netPosition.Value - currentPos).sqrMagnitude > 0.0001f)
        {
            netPosition.Value = currentPos;
        }

        Quaternion currentRot = transform.rotation;
        if (Quaternion.Angle(netRotation.Value, currentRot) > 0.05f)
        {
            netRotation.Value = currentRot;
        }

        if (netIsCrouching.Value != isCrouching)
        {
            netIsCrouching.Value = isCrouching;
        }
    }

    private void HandleCursorLock()
    {
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void HandleLook()
    {
        if (Cursor.lockState != CursorLockMode.Locked)
        {
            return;
        }

        Vector2 lookInput = inputActions.Player.Look.ReadValue<Vector2>();

        float mouseX = lookInput.x * mouseSensitivity;
        float mouseY = lookInput.y * mouseSensitivity;

        xRotation = Mathf.Clamp(xRotation - mouseY, -maxLookAngle, maxLookAngle);

        if (cameraTransform != null)
        {
            cameraTransform.localRotation = Quaternion.Euler(xRotation, initialCameraLocalYaw, 0f);
        }

        transform.Rotate(0f, mouseX, 0f);
    }

    private void HandleCrouch()
    {
        bool wantsToCrouch = inputActions.Player.Crouch.IsPressed();

        if (wantsToCrouch)
        {
            isCrouching = true;
        }
        else if (isCrouching && CanStandUp())
        {
            isCrouching = false;
        }

        float targetHeight = isCrouching ? crouchHeight : standingHeight;
        float currentHeight = characterController.height;

        if (Mathf.Abs(currentHeight - targetHeight) > 0.001f)
        {
            float newHeight = Mathf.Lerp(currentHeight, targetHeight, Time.deltaTime * crouchTransitionSpeed);
            characterController.height = newHeight;

            float heightDifference = standingHeight - newHeight;
            characterController.center = new Vector3(0f, -heightDifference * 0.5f, 0f);
        }

        if (cameraTransform != null)
        {
            float targetCamY = isCrouching ? crouchCameraY : standingCameraY;
            Vector3 camLocalPos = cameraTransform.localPosition;
            if (Mathf.Abs(camLocalPos.y - targetCamY) > 0.001f)
            {
                camLocalPos.y = Mathf.Lerp(camLocalPos.y, targetCamY, Time.deltaTime * crouchTransitionSpeed);
                cameraTransform.localPosition = camLocalPos;
            }
        }
    }

    private bool CanStandUp()
    {
        float checkDistance = standingHeight - crouchHeight;
        Vector3 origin = transform.position + Vector3.up * (crouchHeight * 0.5f);
        float radius = characterController.radius * 0.9f;

        return !Physics.SphereCast(origin, radius, Vector3.up, out _, checkDistance, ~0, QueryTriggerInteraction.Ignore);
    }

    private void HandleMovementAndJump()
    {
        bool isGrounded = characterController.isGrounded;

        if (isGrounded && verticalVelocity.y < 0f)
        {
            verticalVelocity.y = -2f;
        }

        Vector2 moveInput = inputActions.Player.Move.ReadValue<Vector2>();
        bool isSprinting = inputActions.Player.Sprint.IsPressed() && !isCrouching;

        float currentSpeed = walkSpeed;
        if (isCrouching)
        {
            currentSpeed = crouchSpeed;
        }
        else if (isSprinting)
        {
            currentSpeed = sprintSpeed;
        }

        Transform referenceTransform = cameraTransform != null ? cameraTransform : transform;
        Vector3 forward = Vector3.ProjectOnPlane(referenceTransform.forward, Vector3.up).normalized;
        Vector3 right = Vector3.ProjectOnPlane(referenceTransform.right, Vector3.up).normalized;

        Vector3 moveDirection = right * moveInput.x + forward * moveInput.y;
        if (moveDirection.sqrMagnitude > 1f)
        {
            moveDirection.Normalize();
        }

        if (inputActions.Player.Jump.WasPressedThisFrame() && isGrounded && !isCrouching)
        {
            verticalVelocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        verticalVelocity.y += gravity * Time.deltaTime;

        Vector3 totalVelocity = moveDirection * currentSpeed + verticalVelocity;
        characterController.Move(totalVelocity * Time.deltaTime);
    }

    private void ApplyRemoteState()
    {
        if (netPosition.Value != Vector3.zero)
        {
            transform.position = Vector3.Lerp(transform.position, netPosition.Value, Time.deltaTime * 15f);
            transform.rotation = Quaternion.Slerp(transform.rotation, netRotation.Value, Time.deltaTime * 15f);
        }

        float targetScaleY = netIsCrouching.Value ? (crouchHeight / standingHeight) : 1f;
        Vector3 currentScale = transform.localScale;
        if (Mathf.Abs(currentScale.y - targetScaleY) > 0.001f)
        {
            currentScale.y = Mathf.Lerp(currentScale.y, targetScaleY, Time.deltaTime * crouchTransitionSpeed);
            transform.localScale = currentScale;
        }
    }
}
