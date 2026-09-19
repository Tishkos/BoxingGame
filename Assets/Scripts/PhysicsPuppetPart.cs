using UnityEngine;

/// <summary>
/// Tag on every rigidbody of a <see cref="BoxerPhysicsBody"/> puppet, so anything that collides with it (the bag)
/// can find the boxer it belongs to and know which body part it touched.
/// </summary>
public class PhysicsPuppetPart : MonoBehaviour
{
    public BoxerPunchController Owner;
    public BoxerPhysicsBody Body;
    public HumanBodyBones Bone;
    public bool IsHand;
    public bool IsHead;
    public PhysicalFist Fist;
}
