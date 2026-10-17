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

    private Transform selfTransform;
    private CharacterController characterController;
    private InputSystem_Actions inputActions;
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction jumpAction;
    private InputAction sprintAction;
    private InputAction crouchAction;

    private Vector3 verticalVelocity;
    private float xRotation;
    private float initialCameraLocalYaw;
    private float standingCameraY = 0.5f;
    private float crouchCameraY = 0.05f;
    private float jumpVelocity;
    private float crouchScaleRatio;
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
        useGUILayout = false;
        selfTransform = transform;
        characterController = GetComponent<CharacterController>();

        if (characterController != null && characterController.height > 0f)
        {
            standingHeight = characterController.height;
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

        jumpVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        crouchScaleRatio = standingHeight > 0.01f ? (crouchHeight / standingHeight) : 0.55f;

        inputActions = new InputSystem_Actions();
        var playerMap = inputActions.Player;
        moveAction = playerMap.Move;
        lookAction = playerMap.Look;
        jumpAction = playerMap.Jump;
        sprintAction = playerMap.Sprint;
        crouchAction = playerMap.Crouch;

        RefreshSceneState(SceneManager.GetActiveScene().name);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (!IsOwner)
        {
            inputActions?.Player.Disable();
        }
    }

    private void OnEnable()
    {
        if (!IsSpawned || IsOwner)
        {
            inputActions?.Player.Enable();
        }

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
            selfTransform.localScale = Vector3.one;
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
                netPosition.Value = selfTransform.position;
                netRotation.Value = selfTransform.rotation;
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
        Vector3 currentPos = selfTransform.position;
        if ((netPosition.Value - currentPos).sqrMagnitude > 0.0001f)
        {
            netPosition.Value = currentPos;
        }

        Quaternion currentRot = selfTransform.rotation;
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

        Vector2 lookInput = lookAction.ReadValue<Vector2>();

        float mouseX = lookInput.x * mouseSensitivity;
        float mouseY = lookInput.y * mouseSensitivity;

        xRotation = Mathf.Clamp(xRotation - mouseY, -maxLookAngle, maxLookAngle);

        if (cameraTransform != null)
        {
            cameraTransform.localRotation = Quaternion.Euler(xRotation, initialCameraLocalYaw, 0f);
        }

        selfTransform.Rotate(0f, mouseX, 0f);
    }

    private void HandleCrouch()
    {
        bool wantsToCrouch = crouchAction.IsPressed();

        if (wantsToCrouch)
        {
            isCrouching = true;
        }
        else if (isCrouching && CanStandUp())
        {
            isCrouching = false;
        }

        float dt = Time.deltaTime * crouchTransitionSpeed;
        float targetHeight = isCrouching ? crouchHeight : standingHeight;
        float currentHeight = characterController.height;

        if (Mathf.Abs(currentHeight - targetHeight) > 0.001f)
        {
            float newHeight = Mathf.Lerp(currentHeight, targetHeight, dt);
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
                camLocalPos.y = Mathf.Lerp(camLocalPos.y, targetCamY, dt);
                cameraTransform.localPosition = camLocalPos;
            }
        }
    }

    private bool CanStandUp()
    {
        float checkDistance = standingHeight - crouchHeight;
        Vector3 origin = selfTransform.position + new Vector3(0f, crouchHeight * 0.5f, 0f);
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

        Vector2 moveInput = moveAction.ReadValue<Vector2>();
        bool isSprinting = sprintAction.IsPressed() && !isCrouching;

        float currentSpeed = isCrouching ? crouchSpeed : (isSprinting ? sprintSpeed : walkSpeed);

        Transform referenceTransform = cameraTransform != null ? cameraTransform : selfTransform;
        Vector3 forward = referenceTransform.forward;
        forward.y = 0f;
        float fwdSqr = forward.sqrMagnitude;
        if (fwdSqr > 0.0001f)
        {
            forward *= 1f / Mathf.Sqrt(fwdSqr);
        }

        Vector3 right = referenceTransform.right;
        right.y = 0f;
        float rightSqr = right.sqrMagnitude;
        if (rightSqr > 0.0001f)
        {
            right *= 1f / Mathf.Sqrt(rightSqr);
        }

        Vector3 moveDirection = right * moveInput.x + forward * moveInput.y;
        float moveSqr = moveDirection.sqrMagnitude;
        if (moveSqr > 1f)
        {
            moveDirection *= 1f / Mathf.Sqrt(moveSqr);
        }

        if (jumpAction.WasPressedThisFrame() && isGrounded && !isCrouching)
        {
            verticalVelocity.y = jumpVelocity;
        }

        float dt = Time.deltaTime;
        verticalVelocity.y += gravity * dt;

        Vector3 totalDisplacement = (moveDirection * currentSpeed + verticalVelocity) * dt;
        characterController.Move(totalDisplacement);
    }

    private void ApplyRemoteState()
    {
        float dt = Time.deltaTime;

        if (netPosition.Value != Vector3.zero)
        {
            float lerpFactor = dt * 15f;
            Vector3 nextPos = Vector3.Lerp(selfTransform.position, netPosition.Value, lerpFactor);
            Quaternion nextRot = Quaternion.Slerp(selfTransform.rotation, netRotation.Value, lerpFactor);
            selfTransform.SetPositionAndRotation(nextPos, nextRot);
        }

        float targetScaleY = netIsCrouching.Value ? crouchScaleRatio : 1f;
        Vector3 currentScale = selfTransform.localScale;
        if (Mathf.Abs(currentScale.y - targetScaleY) > 0.001f)
        {
            currentScale.y = Mathf.Lerp(currentScale.y, targetScaleY, dt * crouchTransitionSpeed);
            selfTransform.localScale = currentScale;
        }
    }
}
