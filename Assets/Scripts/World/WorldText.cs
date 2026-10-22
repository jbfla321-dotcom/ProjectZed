using System.Collections.Generic;
using UnityEngine;

// Puts a TextMesh on the ProjectZed/WorldText shader (alpha-tested, writes depth), so clouds, sky and volumetric
// fog don't paint over the half of a label that sits against the sky. One shared material per font.
[ExecuteAlways]
[RequireComponent(typeof(TextMesh))]
public class WorldText : MonoBehaviour
{
    [Tooltip("ProjectZed/WorldText (assigned by Tools > Environment > Setup Weather so builds include it).")]
    public Shader shader;

    static readonly Dictionary<Font, Material> materials = new Dictionary<Font, Material>();

    static WorldText() => Font.textureRebuilt += font =>
    {
        if (materials.TryGetValue(font, out var m) && m != null) m.mainTexture = font.material.mainTexture;
    };

    void OnEnable()
    {
        var text = GetComponent<TextMesh>();
        var font = text.font != null ? text.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var s = shader != null ? shader : Shader.Find("ProjectZed/WorldText");
        if (font == null || font.material == null || s == null) return;
        if (!materials.TryGetValue(font, out var m) || m == null)
        {
            m = new Material(s) { name = font.name + " (WorldText)", hideFlags = HideFlags.DontSave, mainTexture = font.material.mainTexture };
            materials[font] = m;
        }
        GetComponent<MeshRenderer>().sharedMaterial = m;
    }
}
