using UnityEngine;

// Keeps a label (e.g. a TextMesh above an NPC) readable: turns it to face the main camera each frame.
public class FaceCamera : MonoBehaviour
{
    void LateUpdate()
    {
        var cam = Camera.main;
        if (cam != null) transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
    }
}
