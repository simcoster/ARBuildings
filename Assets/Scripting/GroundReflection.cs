using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A mirror image of the model in the floor.
///
/// <para>
/// The floor the phone sits over is semi-gloss tile: the real reflections in it are visible in
/// any photograph of the room, and their absence under the model is one of the things that
/// keeps it looking pasted on rather than standing on the ground. This is a classic planar
/// reflection — a second camera mirrored through the ground plane, rendering ONLY the model
/// into a render texture, which a transparent quad on the floor reads back in screen space.
/// </para>
///
/// <para>
/// Three details are what make it work rather than nearly work:
/// </para>
///
/// <list type="bullet">
/// <item><b>Screen-space lookup, not a UV mapping.</b> The quad samples the reflection texture
/// at its own screen position, so the mirror lines up with the model above it without any
/// projection maths in the shader.</item>
/// <item><b>Inverted culling.</b> Mirroring a camera flips triangle winding, so without
/// <see cref="GL.invertCulling"/> the reflection shows the INSIDE of the model's far walls.
/// URP renders this camera itself, so the flag is set from
/// <c>RenderPipelineManager.beginCameraRendering</c> and cleared on end.</item>
/// <item><b>The quads hide from their own camera.</b> The reflection quad and the shadow
/// catcher sit on the same layer as the model, so they would otherwise render into the
/// reflection of themselves. They are switched off for the duration of that camera's pass —
/// cheaper than spending a project layer, and a layer cannot be added while the Editor holds
/// ProjectSettings anyway.</item>
/// </list>
///
/// <para>
/// Strength and fade are MATERIAL properties, not shader constants, so they can be dialled
/// over `RemoteControl` on site for free. Finding the right number for a given floor is a job
/// of twenty small pushes; each shader edit would be a quarter-hour rebuild.
/// </para>
/// </summary>
public class GroundReflection : MonoBehaviour
{
    [Tooltip("Whether the model is mirrored in the floor at all.")]
    [SerializeField] bool enableReflection = true;

    [Tooltip("How visible the mirror image is. A semi-gloss tile is around 0.2-0.3; polished " +
             "stone goes higher. Above ~0.5 it stops reading as a floor and starts reading " +
             "as water.")]
    [Range(0f, 1f)]
    [SerializeField] float strength = 0.25f;

    [Tooltip("Size of the reflection texture. Half screen height is plenty — a floor " +
             "reflection is blurry in reality, so the softness is free realism.")]
    [SerializeField] int textureSize = 512;

    [Tooltip("How far the reflection quad extends past the model's footprint, as a multiple " +
             "of it.")]
    [SerializeField] float spread = 2.2f;

    Camera _mirrorCam;
    RenderTexture _target;
    GameObject _quad;
    MeshRenderer _quadRenderer;
    Material _material;
    Renderer _shadowGround;

    Camera _main;
    Transform _model;
    float _groundY;
    string _status = "not built";

    static readonly int ReflectionTexId = Shader.PropertyToID("_ReflectionTex");
    static readonly int StrengthId = Shader.PropertyToID("_Strength");

    public bool Enabled
    {
        get => enableReflection;
        set
        {
            enableReflection = value;
            if (_quad != null) _quad.SetActive(value);
            if (_mirrorCam != null) _mirrorCam.enabled = value;
            _status = value ? _status : "off";
        }
    }

    public float Strength
    {
        get => strength;
        set
        {
            strength = Mathf.Clamp01(value);
            if (_material != null) _material.SetFloat(StrengthId, strength);
        }
    }

    void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;
    }

    void OnDestroy() => Teardown();

    /// <summary>
    /// Builds the mirror for a model that has just been placed. Called by whoever knows where
    /// the model and its base are; nothing here can work that out on its own, because the
    /// anchor moves and the model is rescaled after loading.
    /// </summary>
    public void Build(Transform modelRoot, Transform groundParent, float footprintMetres,
                      float groundLocalY)
    {
        Teardown();

        _model = modelRoot;
        if (_model == null) { _status = "no model"; return; }

        // Resources FIRST, and the shader lives in Assets/Resources for exactly that reason:
        // Shader.Find returns null on device for any shader no scene references, because the
        // build strips it. Anything under Resources is always included. This cost an hour
        // once already with the shadow catcher.
        var shader = Resources.Load<Shader>("GroundReflection");
        if (shader == null) shader = Shader.Find("AR/GroundReflection");

        if (shader == null)
        {
            _status = "SHADER MISSING — stripped from the build?";
            Debug.LogWarning("[Reflect] AR/GroundReflection not found in Resources or by name");
            return;
        }

        _material = new Material(shader);
        _material.SetFloat(StrengthId, strength);

        _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _quad.name = "GroundReflection";
        Destroy(_quad.GetComponent<Collider>());       // must not catch the placement raycast
        _quad.transform.SetParent(groundParent, false);

        // -90 about X, not +90: a Quad's normal is +Z and +90 aims it at the floor.
        _quad.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

        // A centimetre above the shadow catcher so the two never fight for the same depth.
        // Transparent queue means it cannot z-fight the model, only its co-planar neighbour.
        _quad.transform.localPosition = new Vector3(0f, groundLocalY + 0.01f, 0f);

        float span = Mathf.Max(1f, footprintMetres * spread);
        _quad.transform.localScale = new Vector3(span, span, 1f);

        _quadRenderer = _quad.GetComponent<MeshRenderer>();
        _quadRenderer.sharedMaterial = _material;
        _quadRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _quadRenderer.receiveShadows = false;

        _groundY = _quad.transform.position.y;

        BuildCamera();

        _quad.SetActive(enableReflection);
        _status = $"built, {span:F1} m quad at y {_groundY:F2}";
        Debug.Log($"[Reflect] {_status}");
    }

    void BuildCamera()
    {
        _main = Camera.main;
        if (_main == null) { _status = "no main camera"; return; }

        _target = new RenderTexture(textureSize, textureSize, 16, RenderTextureFormat.ARGB32)
        {
            name = "GroundReflectionRT",
            antiAliasing = 1
        };
        _target.Create();

        var holder = new GameObject("GroundReflectionCamera");
        holder.transform.SetParent(transform, false);
        _mirrorCam = holder.AddComponent<Camera>();

        // Transparent clear is what makes the shader's masking free: alpha is already
        // "is there any model at this pixel".
        _mirrorCam.clearFlags = CameraClearFlags.SolidColor;
        _mirrorCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        _mirrorCam.targetTexture = _target;

        // Never let it be treated as a main camera or draw the AR background.
        _mirrorCam.tag = "Untagged";
        _mirrorCam.depth = _main.depth - 10;
        _mirrorCam.allowHDR = false;
        _mirrorCam.allowMSAA = false;
        _mirrorCam.useOcclusionCulling = false;
        _mirrorCam.cullingMask = _main.cullingMask;

        _material.SetTexture(ReflectionTexId, _target);
        _mirrorCam.enabled = enableReflection;
    }

    void LateUpdate()
    {
        if (!enableReflection || _mirrorCam == null || _main == null || _quad == null) return;

        // Track the ground in world space every frame: the anchor is refined continuously by
        // ARCore, and a reflection plane a few centimetres out from the model's feet reads
        // as the building hovering.
        _groundY = _quad.transform.position.y;

        var t = _main.transform;
        Vector3 p = t.position;

        // Mirror the camera through the horizontal plane at the model's base.
        _mirrorCam.transform.position = new Vector3(p.x, 2f * _groundY - p.y, p.z);

        Vector3 forward = t.forward; forward.y = -forward.y;
        Vector3 up = t.up;           up.y = -up.y;
        if (forward.sqrMagnitude > 1e-6f)
            _mirrorCam.transform.rotation = Quaternion.LookRotation(forward, up);

        // Copy the AR camera's projection rather than its field of view: ARCore supplies a
        // projection matrix that matches the physical camera, and rebuilding it from fov and
        // aspect would put the reflection subtly out of register with the model.
        _mirrorCam.projectionMatrix = _main.projectionMatrix;
        _mirrorCam.nearClipPlane = _main.nearClipPlane;
        _mirrorCam.farClipPlane = _main.farClipPlane;
    }

    void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam != _mirrorCam) return;

        GL.invertCulling = true;

        // Keep the floor surfaces out of their own reflection.
        if (_quadRenderer != null) _quadRenderer.enabled = false;
        if (_shadowGround == null && _quad != null)
        {
            var ground = _quad.transform.parent != null
                ? _quad.transform.parent.Find("ShadowGround")
                : null;
            if (ground != null) _shadowGround = ground.GetComponent<Renderer>();
        }
        if (_shadowGround != null) _shadowGround.enabled = false;
    }

    void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam != _mirrorCam) return;

        GL.invertCulling = false;
        if (_quadRenderer != null) _quadRenderer.enabled = true;
        if (_shadowGround != null) _shadowGround.enabled = true;
    }

    void Teardown()
    {
        if (_mirrorCam != null) Destroy(_mirrorCam.gameObject);
        if (_quad != null) Destroy(_quad);
        if (_material != null) Destroy(_material);

        if (_target != null)
        {
            _target.Release();
            Destroy(_target);
        }

        _mirrorCam = null; _quad = null; _quadRenderer = null;
        _material = null; _target = null; _shadowGround = null;
    }

    public string StateReport
    {
        get
        {
            var report = new StringBuilder();
            report.AppendLine($"ground reflection  : {(enableReflection ? "ON" : "OFF")} " +
                              $"strength {strength:F2}");
            report.AppendLine($"reflection plane   : {_status}");
            report.AppendLine($"mirror camera      : " +
                              (_mirrorCam == null ? "none"
                                                  : $"enabled={_mirrorCam.enabled}, " +
                                                    $"{textureSize}px, y {_groundY:F2}"));
            return report.ToString();
        }
    }

    public string HudReadout => $"reflect floor: {(enableReflection ? $"{strength:F2}" : "off")}";
}
