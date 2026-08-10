using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Editor-only free-fly control for the AR camera, so you can walk around the model in the
/// Game view (correct FOV, fog and post-processing) without a phone.
///
/// In the Editor there is no XR provider, so TrackedPoseDriver leaves the camera alone and
/// this can drive it. It deletes itself outside the Editor so it can never ship.
/// </summary>
public class DebugFlyCamera : MonoBehaviour
{
    [Tooltip("Metres per second. Shift multiplies by fastMultiplier.")]
    [SerializeField] float moveSpeed = 8f;
    [SerializeField] float fastMultiplier = 4f;
    [SerializeField] float lookSensitivity = 0.12f;

    [Tooltip("Start at roughly eye height rather than wherever the rig sits.")]
    [SerializeField] bool startAtEyeHeight = true;
    [SerializeField] float eyeHeight = 1.6f;

    float _yaw, _pitch;

    void Awake()
    {
        if (!Application.isEditor)
        {
            Destroy(this);
            return;
        }

        var e = transform.eulerAngles;
        _yaw = e.y;
        _pitch = e.x;

        if (startAtEyeHeight)
        {
            var p = transform.position;
            transform.position = new Vector3(p.x, eyeHeight, p.z);
        }
    }

    void Update()
    {
        var mouse = Mouse.current;
        var keyboard = Keyboard.current;
        if (mouse == null || keyboard == null) return;

        // Look — only while the right button is held, so the mouse stays usable otherwise.
        if (mouse.rightButton.isPressed)
        {
            Vector2 delta = mouse.delta.ReadValue() * lookSensitivity;
            _yaw += delta.x;
            _pitch = Mathf.Clamp(_pitch - delta.y, -89f, 89f);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        // Scroll adjusts speed rather than zooming — FOV must stay honest to the phone.
        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
            moveSpeed = Mathf.Clamp(moveSpeed * (scroll > 0 ? 1.1f : 0.9f), 0.5f, 200f);

        Vector3 dir = Vector3.zero;
        if (keyboard.wKey.isPressed) dir += transform.forward;
        if (keyboard.sKey.isPressed) dir -= transform.forward;
        if (keyboard.dKey.isPressed) dir += transform.right;
        if (keyboard.aKey.isPressed) dir -= transform.right;
        if (keyboard.eKey.isPressed) dir += Vector3.up;
        if (keyboard.qKey.isPressed) dir -= Vector3.up;

        if (dir == Vector3.zero) return;

        float speed = moveSpeed * (keyboard.leftShiftKey.isPressed ? fastMultiplier : 1f);
        transform.position += dir.normalized * speed * Time.deltaTime;
    }
}
