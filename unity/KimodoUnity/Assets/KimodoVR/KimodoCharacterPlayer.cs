// Drives a skinned Humanoid character with a Kimodo motion through the bridge
// plugin's retargeter (AminHP.KimodoBridge.KimodoPlayer), then re-solves the
// captured contacts on this rig with two-bone IK and keeps the body inside
// the room. Returns null from Create() if the prefab is not a valid Humanoid.

using AminHP.KimodoBridge;
using UnityEngine;

/// <summary>Direction API shared by the skinned-character and stick-figure
/// players: timeline cutting and manual placement.</summary>
public interface IKimodoDirectable
{
    float Duration { get; }
    float ClipTime { get; }
    float CutIn { get; }
    float CutOut { get; }
    void SetCutIn(float t);
    void SetCutOut(float t);
    void ResetCut();
    void Nudge(Vector3 d);
    void Rotate(float deg);
    void ResetAdjust();
    void SetAdjust(Vector3 offset, float yaw);
    Vector3 GetAdjustOffset();
    float GetAdjustYaw();
}

public class KimodoCharacterPlayer : MonoBehaviour, IKimodoDirectable
{
    KimodoPlayer player;
    GameObject character;
    float playT;
    bool playing;

    // contact schedule (Unity world space + seconds on the clip timeline)
    Vector3[] contactPts;
    float[] contactTimes;
    Transform upperArm, foreArm, hand, fingertip;   // fingertip null => wrist anchor
    Transform hips;
    const float RAMP = 0.3f;                        // blend in/out seconds

    // Cut window: the clip loops inside [cutIn, cutOut] (cutOut < 0 = full).
    float cutIn, cutOut = -1f;
    float curT;                                     // effective clip time this frame

    public float Duration => player != null ? player.Duration : 0f;
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

    // Room containment: the model has no walls, so playback enforces them —
    // whatever the clip does, the hips never leave this floor rect.
    Rect room;
    bool hasRoom;
    public void SetRoom(Rect r) { room = r; hasRoom = true; }

    // Furniture keep-out with tolerance and smoothing (hard pushes jittered).
    Rect[] keepOut;
    Vector3 koOffset;                    // smoothed correction
    const float KO_ALLOW = 0.12f;        // tolerated penetration depth
    public void SetKeepOut(Rect[] rects) { keepOut = rects; }

    // Manual adjustment (popup arrows / rotation) applied on top of the clip.
    Vector3 userOffset;
    float userYaw;
    public void Nudge(Vector3 d) { userOffset += d; }
    public void Rotate(float deg) { userYaw += deg; }
    public void ResetAdjust() { userOffset = Vector3.zero; userYaw = 0f; }
    public void SetAdjust(Vector3 offset, float yaw) { userOffset = offset; userYaw = yaw; }
    public Vector3 GetAdjustOffset() => userOffset;
    public float GetAdjustYaw() => userYaw;

    /// <summary>Where the character currently stands (hips), for click-selection.</summary>
    public Vector3 CurrentPos() => hips != null ? hips.position : transform.position;

    public static KimodoCharacterPlayer Create(
        GameObject characterPrefab, string[] names, int[] parents, float[] boneOffsets,
        float[] quatsFlat, float[] rootFlat, int frameCount, int jointCount,
        int rootIndex, float framesPerSecond)
    {
        if (characterPrefab == null || quatsFlat == null || quatsFlat.Length == 0) return null;

        // Lift the rest pose so the feet touch y=0 — a buried rest pose breaks
        // the avatar's humanScale and inflates every retargeted position.
        float minY = 0f;
        var restY = new float[jointCount];
        for (int j = 0; j < jointCount; j++)
        {
            restY[j] = (parents[j] >= 0 ? restY[parents[j]] : 0f) + boneOffsets[j * 3 + 1];
            if (restY[j] < minY) minY = restY[j];
        }

        var m = new KimodoMotion
        {
            fps = framesPerSecond > 0 ? framesPerSecond : 30f,
            frameCount = frameCount, jointCount = jointCount,
            rootIndex = rootIndex, quatOrder = "wxyz",
            coordSystem = "kimodo_rh_yup_zfwd_meters", skeletonName = "somaskel77",
        };
        for (int j = 0; j < jointCount; j++)
            m.bones.Add(new KimodoBone
            {
                name = names[j], parent = parents[j],
                ox = boneOffsets[j * 3],
                oy = boneOffsets[j * 3 + 1] + (j == rootIndex ? -minY : 0f),
                oz = boneOffsets[j * 3 + 2],
            });
        m.clips.Add(new KimodoClip { rootPositions = rootFlat, localQuats = quatsFlat });

        var go = new GameObject("KimodoCharacter");
        var p = go.AddComponent<KimodoCharacterPlayer>();
        p.character = Instantiate(characterPrefab, go.transform);
        p.character.transform.localPosition = Vector3.zero;

        var anim = p.character.GetComponentInChildren<Animator>();
        if (anim == null || anim.avatar == null || !anim.avatar.isHuman)
        {
            Debug.LogWarning("[KimodoVR] character prefab has no Humanoid Animator — falling back.");
            Destroy(go);
            return null;
        }
        anim.runtimeAnimatorController = null;   // nothing may fight SetHumanPose
        anim.applyRootMotion = false;

        // Baked bounds don't follow SetHumanPose — avoid frustum culling.
        foreach (var smr in p.character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            smr.updateWhenOffscreen = true;

        p.player = new KimodoPlayer();
        if (!p.player.Bind(anim, m))
        {
            Debug.LogWarning("[KimodoVR] KimodoPlayer.Bind failed — falling back.");
            Destroy(go);
            return null;
        }
        float scale = p.player.AutoCalibrateRootMotion();

        p.upperArm = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
        p.foreArm = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
        p.hand = anim.GetBoneTransform(HumanBodyBones.RightHand);

        p.player.SampleFrame(0, 0);
        p.playing = true;

        var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
        p.hips = hips;
        Debug.Log($"[KimodoVR] Character bound; humanScale src={p.player.SourceHumanScale:F3} " +
                  $"dst={p.player.TargetHumanScale:F3} rootMotionScale={scale:F3}; " +
                  "hips at " + (hips != null ? hips.position.ToString("F2") : "?"));
        return p;
    }

    /// <summary>Give the player the captured contact targets (Unity world space)
    /// and their clip times, so it can re-solve the touch on this rig.</summary>
    public void SetContact(Vector3[] pts, float[] times, bool wristAnchor)
    {
        if (pts == null || times == null || pts.Length == 0 || pts.Length != times.Length) return;
        if (upperArm == null || foreArm == null || hand == null)
        {
            Debug.LogWarning("[KimodoVR] Right-arm bones missing on character — contact IK off.");
            return;
        }
        contactPts = pts;
        contactTimes = times;
        fingertip = null;
        if (!wristAnchor)
        {
            var anim = character.GetComponentInChildren<Animator>();
            // deepest available index-finger bone stands in for the fingertip
            fingertip = anim.GetBoneTransform(HumanBodyBones.RightIndexDistal)
                     ?? anim.GetBoneTransform(HumanBodyBones.RightIndexIntermediate)
                     ?? anim.GetBoneTransform(HumanBodyBones.RightIndexProximal);
        }
        Debug.Log($"[KimodoVR] Contact IK on: {pts.Length} targets, " +
                  $"t={times[0]:0.00}–{times[times.Length - 1]:0.00}s, " +
                  $"anchor={(fingertip != null ? fingertip.name : "wrist")}");
    }

    public void Stop()
    {
        playing = false;
        Destroy(gameObject);
    }

    void Update()
    {
        if (!playing || player == null) return;
        playT += Time.deltaTime;
        float dur = Mathf.Max(0.01f, player.Duration);
        float a = Mathf.Clamp(cutIn, 0, dur);
        float b = cutOut > 0 ? Mathf.Min(cutOut, dur) : dur;
        if (b - a < 0.2f) { a = 0; b = dur; }
        curT = a + Mathf.Repeat(playT, b - a);
        player.SampleTime(0, curT, loop: false);
    }

    void LateUpdate()
    {
        if (!playing || player == null) return;

        if (hips != null)
        {
            // 1) user adjustment: yaw about the hips pivot + nudge
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Vector3 raw = hips.position;                       // clip-space pose
            Quaternion yawQ = Quaternion.Euler(0, userYaw, 0);
            Vector3 pivot = new Vector3(raw.x, 0, raw.z);
            transform.SetPositionAndRotation(pivot - yawQ * pivot + userOffset, yawQ);

            // 2) room containment: at a wall they walk in place, never leave
            if (hasRoom)
            {
                Vector3 h = hips.position;
                transform.position += new Vector3(
                    Mathf.Clamp(h.x, room.xMin, room.xMax) - h.x, 0,
                    Mathf.Clamp(h.z, room.yMin, room.yMax) - h.z);
            }

            // 3) furniture keep-out (tolerant, smoothed)
            if (keepOut != null)
            {
                Vector3 target = Vector3.zero;
                Vector3 h = hips.position;
                foreach (var r in keepOut)
                {
                    // shrink by the allowance; thin pieces vanish (walls etc.)
                    var rr = Rect.MinMaxRect(r.xMin + KO_ALLOW, r.yMin + KO_ALLOW,
                                             r.xMax - KO_ALLOW, r.yMax - KO_ALLOW);
                    if (rr.width <= 0 || rr.height <= 0) continue;
                    var p = new Vector2(h.x + target.x, h.z + target.z);
                    if (!rr.Contains(p)) continue;
                    float dxMin = p.x - rr.xMin, dxMax = rr.xMax - p.x;
                    float dzMin = p.y - rr.yMin, dzMax = rr.yMax - p.y;
                    float m = Mathf.Min(Mathf.Min(dxMin, dxMax), Mathf.Min(dzMin, dzMax));
                    target += m == dxMin ? new Vector3(-dxMin, 0, 0) :
                              m == dxMax ? new Vector3(dxMax, 0, 0) :
                              m == dzMin ? new Vector3(0, 0, -dzMin) :
                                           new Vector3(0, 0, dzMax);
                }
                koOffset = Vector3.Lerp(koOffset, target, 10f * Time.deltaTime);
                transform.position += koOffset;
            }
        }

        if (contactPts == null) return;
        float t = curT;

        float t0 = contactTimes[0], t1 = contactTimes[contactTimes.Length - 1];
        float w = Mathf.Clamp01((t - (t0 - RAMP)) / RAMP) * Mathf.Clamp01((t1 + RAMP - t) / RAMP);
        if (w <= 0f) return;
        w = w * w * (3f - 2f * w);   // smoothstep

        SolveArmIK(TargetAt(t), w);
    }

    Vector3 TargetAt(float t)
    {
        if (t <= contactTimes[0]) return contactPts[0];
        for (int i = 1; i < contactTimes.Length; i++)
            if (t <= contactTimes[i])
                return Vector3.Lerp(contactPts[i - 1], contactPts[i],
                    Mathf.InverseLerp(contactTimes[i - 1], contactTimes[i], t));
        return contactPts[contactPts.Length - 1];
    }

    // Two-bone IK: elbow by law of cosines, then shoulder swing;
    // two iterations because the fingertip offset rotates with the arm.
    void SolveArmIK(Vector3 target, float w)
    {
        for (int iter = 0; iter < 2; iter++)
        {
            Vector3 a = upperArm.position, b = foreArm.position, c = hand.position;
            Vector3 eff = fingertip != null ? fingertip.position : c;
            Vector3 goal = Vector3.Lerp(eff, target, w) - (eff - c);   // wrist goal

            float l1 = Vector3.Distance(a, b), l2 = Vector3.Distance(b, c);
            Vector3 toGoal = goal - a;
            float d = Mathf.Clamp(toGoal.magnitude, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);

            float cosE = Mathf.Clamp((l1 * l1 + l2 * l2 - d * d) / (2f * l1 * l2), -1f, 1f);
            float desired = Mathf.Acos(cosE) * Mathf.Rad2Deg;   // interior elbow angle
            float current = Vector3.Angle(a - b, c - b);
            Vector3 axis = Vector3.Cross(a - b, c - b);
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(a - b, Vector3.down);
            foreArm.rotation = Quaternion.AngleAxis(desired - current, axis.normalized) * foreArm.rotation;

            c = hand.position;
            if ((c - a).sqrMagnitude > 1e-8f && toGoal.sqrMagnitude > 1e-8f)
                upperArm.rotation = Quaternion.FromToRotation(c - a, toGoal) * upperArm.rotation;
        }
    }

    void OnDestroy()
    {
        player?.Dispose();
        player = null;
    }
}
