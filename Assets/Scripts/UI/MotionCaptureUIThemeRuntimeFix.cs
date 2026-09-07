using System.Reflection;
using UnityEngine;

[DefaultExecutionOrder(850)]
public sealed class MotionCaptureUIThemeRuntimeFix : MonoBehaviour
{
    private MotionCaptureUI target;
    private bool applied;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        MotionCaptureUI ui = Object.FindObjectOfType<MotionCaptureUI>();
        if (ui == null) return;
        if (ui.gameObject.GetComponent<MotionCaptureUIThemeRuntimeFix>() == null)
            ui.gameObject.AddComponent<MotionCaptureUIThemeRuntimeFix>();
    }

    private void Start()
    {
        target = GetComponent<MotionCaptureUI>();
        ApplyTheme();
    }

    private void LateUpdate()
    {
        if (!applied) ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (target == null) target = GetComponent<MotionCaptureUI>();
        if (target == null) return;

        Texture2D panel = Solid(new Color(0.97f, 0.99f, 0.96f, 0.48f));
        Texture2D pale = Solid(new Color(0.78f, 0.90f, 0.76f, 0.70f));
        Texture2D white = Solid(new Color(1f, 1f, 1f, 0.55f));
        Texture2D green = Solid(new Color(0.18f, 0.42f, 0.20f, 0.84f));

        SetField("panelTexture", panel);
        SetField("paleTexture", pale);
        SetField("whiteTexture", white);
        SetField("greenTexture", green);

        // 清掉已经缓存的 GUIStyle，让 MotionCaptureUI 用新纹理重新建立样式。
        SetField("windowStyle", null);
        SetField("titleStyle", null);
        SetField("labelStyle", null);
        SetField("smallStyle", null);
        SetField("buttonStyle", null);
        SetField("tableHeaderStyle", null);
        SetField("tableCellStyle", null);
        SetField("valueStyle", null);
        applied = true;
    }

    private void SetField(string name, object value)
    {
        FieldInfo field = typeof(MotionCaptureUI).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field != null) field.SetValue(target, value);
    }

    private static Texture2D Solid(Color color)
    {
        Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }
}

