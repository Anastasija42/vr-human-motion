// Pose a URDF-imported robot in EDIT mode, no physics involved: one slider
// per revolute joint, rotation applied about the joint's true anchor point
// and axis (read from the imported ArticulationBody), so links can never
// disconnect. Works with disabled/static ArticulationBodies; the pose is just
// the transforms, so it saves with the scene and survives Play mode.
//
// Usage: add to the robot root -> right-click the component header ->
// "Scan Robot" (do this while the robot is in its clean imported pose) ->
// drag the degree sliders. "Zero Pose" returns to the scanned rest pose.

using System.Collections.Generic;
using UnityEngine;

public class RobotPoser : MonoBehaviour
{
    [System.Serializable]
    public class Joint
    {
        public string name;
        [Range(-180f, 180f)] public float degrees;
        public float lower = -180f, upper = 180f;   // from the URDF drive limits
        [HideInInspector] public Transform link;
        [HideInInspector] public Quaternion rest;    // local rotation at scan time
        [HideInInspector] public Vector3 restPos;    // local position at scan time
        [HideInInspector] public Vector3 axisLocal;  // joint axis in link space
        [HideInInspector] public Vector3 anchorLocal;
    }

    public List<Joint> joints = new List<Joint>();

    [ContextMenu("Scan Robot")]
    public void Scan()
    {
        joints.Clear();
        foreach (var ab in GetComponentsInChildren<ArticulationBody>(true))
        {
            if (ab.isRoot || ab.jointType != ArticulationJointType.RevoluteJoint) continue;
            var j = new Joint
            {
                name = ab.name,
                link = ab.transform,
                rest = ab.transform.localRotation,
                restPos = ab.transform.localPosition,
                // ArticulationBody revolute joints turn about the X axis of
                // their anchor frame.
                axisLocal = ab.anchorRotation * Vector3.right,
                anchorLocal = ab.anchorPosition,
                degrees = 0f,
            };
            if (ab.xDrive.upperLimit > ab.xDrive.lowerLimit)
            {
                j.lower = ab.xDrive.lowerLimit;
                j.upper = ab.xDrive.upperLimit;
            }
            joints.Add(j);
        }
        Debug.Log($"[RobotPoser] {name}: {joints.Count} revolute joints ready.");
    }

    [ContextMenu("Zero Pose")]
    public void ZeroPose()
    {
        foreach (var j in joints) j.degrees = 0f;
        Apply();
    }

    void OnValidate() => Apply();   // sliders pose the robot live in Edit mode

    public void Apply()
    {
        foreach (var j in joints)
        {
            if (j.link == null) continue;
            float a = Mathf.Clamp(j.degrees, j.lower, j.upper);
            var spin = Quaternion.AngleAxis(a, j.axisLocal);
            // Rotate about the anchor POINT, not the transform origin — this is
            // what keeps the mesh seated in its socket for any joint layout.
            j.link.localRotation = j.rest * spin;
            j.link.localPosition = j.restPos + j.rest * (j.anchorLocal - spin * j.anchorLocal);
        }
    }
}
