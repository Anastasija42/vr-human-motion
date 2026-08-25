// Plays a Kimodo motion (flat joint-position array) as a full mannequin-style
// figure: spheres at joints, capsule bones between joint and parent.

using UnityEngine;

public class KimodoSkeletonPlayer : MonoBehaviour, IKimodoDirectable
{
    float[] data;          // T * J * 3
    int T, J;
    int[] parents;
    float fps = 30f;
    float playT;
    bool playing;

    // ---- direction API (parity with KimodoCharacterPlayer) ----
    float cutIn, cutOut = -1f;   // loop window; cutOut < 0 = full clip
    float curT;
    Vector3 userOffset;          // manual placement on top of the clip
    float userYaw;

    public float Duration => T / Mathf.Max(1f, fps);
    public float ClipTime => curT;
    public float CutIn => cutIn;
    public float CutOut => cutOut < 0 ? Duration : cutOut;
    public void SetCutIn(float t)
    {
        cutIn = Mathf.Clamp(t, 0, Mathf.Max(0, Duration - 0.3f));
        if (cutOut >= 0 && cutOut < cutIn + 0.3f) cutOut = -1f;
    }
    public void SetCutOut(float t) { if (t > cutIn + 0.3f) cutOut = Mathf.Min(t, Duration); }
    public void ResetCut() { cutIn = 0; cutOut = -1f; }
    public void Nudge(Vector3 d) { userOffset += d; }
    public void Rotate(float deg) { userYaw += deg; }
    public void ResetAdjust() { userOffset = Vector3.zero; userYaw = 0f; }
    public void SetAdjust(Vector3 offset, float yaw) { userOffset = offset; userYaw = yaw; }
    public Vector3 GetAdjustOffset() => userOffset;
    public float GetAdjustYaw() => userYaw;

    Transform root;
    Transform[] joints;
    Transform[] bones;     // one per joint with a parent
    int[] boneJoint;

    [Tooltip("Figure color — set before Play(); each performer gets its own")]
    public Color accent = new Color(0.98f, 0.55f, 0.25f);

    /// <summary>Where the figure currently stands (root joint), for click-selection.</summary>
    public Vector3 CurrentPos() =>
        joints != null && joints.Length > 0 && joints[0] != null ? joints[0].position : transform.position;

    public void Play(float[] framesFlat, int frameCount, int jointCount,
                     int[] parentIdx, string[] names, float framesPerSecond)
    {
        Stop();
        data = framesFlat; T = frameCount; J = jointCount;
        parents = parentIdx; fps = framesPerSecond > 0 ? framesPerSecond : 30f;

        root = new GameObject("KimodoFigure").transform;
        joints = new Transform[J];
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var jointMat = new Material(shader) { color = accent };
        var boneMat = new Material(shader) { color = Color.Lerp(accent, Color.white, 0.4f) };

        for (int j = 0; j < J; j++)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(s.GetComponent<Collider>());
            bool head = names != null && j < names.Length && names[j] == "Head";
            s.transform.localScale = Vector3.one * (head ? 0.16f : 0.045f);
            s.GetComponent<Renderer>().material = jointMat;
            s.transform.SetParent(root);
            joints[j] = s.transform;
        }

        int nb = 0;
        for (int j = 0; j < J; j++) if (parents[j] >= 0) nb++;
        bones = new Transform[nb];
        boneJoint = new int[nb];
        int bi = 0;
        for (int j = 0; j < J; j++)
        {
            if (parents[j] < 0) continue;
            var c = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.Destroy(c.GetComponent<Collider>());
            c.GetComponent<Renderer>().material = boneMat;
            c.transform.SetParent(root);
            bones[bi] = c.transform;
            boneJoint[bi] = j;
            bi++;
        }

        playT = 0f;
        playing = true;
        Apply(0);
    }

    public void Stop()
    {
        playing = false;
        if (root != null) Object.Destroy(root.gameObject);
        root = null;
    }

    void Update()
    {
        if (!playing || T == 0) return;
        playT += Time.deltaTime;
        float dur = Mathf.Max(0.033f, Duration);
        float a = Mathf.Clamp(cutIn, 0, dur);
        float b = cutOut > 0 ? Mathf.Min(cutOut, dur) : dur;
        if (b - a < 0.2f) { a = 0; b = dur; }
        curT = a + Mathf.Repeat(playT, b - a);
        Apply(Mathf.Min(T - 1, Mathf.FloorToInt(curT * fps)));
    }

    Vector3 P(int frame, int j)
    {
        int o = (frame * J + j) * 3;
        return new Vector3(data[o], data[o + 1], data[o + 2]);
    }

    void Apply(int frame)
    {
        // user adjustment: yaw about the figure's own root, then nudge
        Vector3 r0 = P(frame, 0);
        Vector3 pivot = new Vector3(r0.x, 0, r0.z);
        Quaternion yawQ = Quaternion.Euler(0, userYaw, 0);
        Vector3 W(int jj) { Vector3 q = P(frame, jj); return yawQ * (q - pivot) + pivot + userOffset; }

        for (int j = 0; j < J; j++) joints[j].position = W(j);
        for (int b2 = 0; b2 < bones.Length; b2++)
        {
            int j = boneJoint[b2];
            Vector3 aP = W(j), pP = W(parents[j]);
            Vector3 mid = (aP + pP) * 0.5f, d = pP - aP;
            float len = d.magnitude;
            var t = bones[b2];
            t.position = mid;
            if (len > 1e-5f) t.rotation = Quaternion.FromToRotation(Vector3.up, d);
            // Capsule primitive is 2 units tall at scale 1 along Y.
            t.localScale = new Vector3(0.032f, Mathf.Max(0.01f, len * 0.5f), 0.032f);
        }
    }
}
