using System;
using UnityEngine;

namespace RehabPhotoGame
{
    /// <summary>
    /// 新计划第1段：把现有坐姿伸膝判定接入“远景对焦—保持—快门”反馈。
    /// 这是技术验证层，正式 Canvas/TMP 界面在下一段实现。
    /// </summary>
    [DefaultExecutionOrder(1100)]
    [DisallowMultipleComponent]
    public sealed class PhotoFocusFeedback : MonoBehaviour
    {
        private const string PhotoResource =
            "RehabPhotoGame/Photos/A_Distant/spring_01";
        private const string ShaderResource =
            "RehabPhotoGame/Shaders/FocusBlur";

        private readonly PhotoFocusSession focusSession = new PhotoFocusSession();
        private SeatedKneeExtensionRuntimeFix training;
        private MotionCaptureController motionCapture;
        private Texture2D sourcePhoto;
        private RenderTexture focusedPhoto;
        private Material blurMaterial;
        private Texture2D panelTexture;
        private Texture2D greenTexture;
        private GUIStyle titleStyle;
        private GUIStyle bodyStyle;
        private GUIStyle focusStyle;
        private GUIStyle successStyle;
        private float flashEndsAt;
        private float successEndsAt;
        private KneeExtensionTrainingSnapshot snapshot;

        private static readonly Color DarkGreen = new Color(0.07f, 0.20f, 0.10f, 1f);
        private static readonly Color Green = new Color(0.18f, 0.42f, 0.20f, 1f);

        private void Start()
        {
            training = GetComponent<SeatedKneeExtensionRuntimeFix>();
            if (training == null)
                training = FindObjectOfType<SeatedKneeExtensionRuntimeFix>();
            motionCapture = FindObjectOfType<MotionCaptureController>();

            sourcePhoto = Resources.Load<Texture2D>(PhotoResource);
            Shader shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null)
                shader = Shader.Find("Hidden/RehabPhotoGame/FocusBlur");
            if (shader != null && shader.isSupported)
                blurMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            panelTexture = Solid(new Color(0.97f, 0.99f, 0.96f, 0.92f));
            greenTexture = Solid(Green);
            EnsureRenderTarget();

            motionCapture?.LogGameDiagnostic(
                "photo_focus_session",
                $"photo={PhotoResource}, shader={(blurMaterial != null ? "ready" : "fallback")}");
        }

        private void LateUpdate()
        {
            if (training == null)
                return;

            snapshot = training.CurrentSnapshot;
            if (focusSession.Update(snapshot))
                CapturePhoto();

            if (sourcePhoto == null)
                return;
            EnsureRenderTarget();
            if (focusedPhoto == null)
                return;

            if (blurMaterial == null)
            {
                Graphics.Blit(sourcePhoto, focusedPhoto);
                return;
            }

            // 640x360 的取景纹理配合 0～18 像素采样半径，足以看出对焦变化，
            // 又不会把 2048x1152 原图设为 Read/Write 或每帧在 CPU 生成新纹理。
            blurMaterial.SetFloat("_BlurPixels", Mathf.Lerp(18f, 0f, focusSession.Focus01));
            Graphics.Blit(sourcePhoto, focusedPhoto, blurMaterial);
        }

        private void CapturePhoto()
        {
            float now = Time.realtimeSinceStartup;
            flashEndsAt = now + 0.20f;
            successEndsAt = now + 2.0f;
            motionCapture?.LogGameDiagnostic(
                "photo_capture",
                JsonUtility.ToJson(new CaptureDiagnostic
                {
                    leg = snapshot.Leg.ToString(),
                    sessionVersion = snapshot.SessionVersion,
                    capturedPhotos = focusSession.CapturedPhotos,
                    repetitionsBeforeReturn = snapshot.CompletedRepetitions,
                    kneeAngleDeg = snapshot.KneeAngleDeg,
                    readyReferenceKneeDeg = snapshot.ReadyReferenceKneeDeg,
                    targetKneeDeg = snapshot.TargetKneeDeg,
                    holdSeconds = snapshot.HoldSeconds,
                    focus01 = focusSession.Focus01
                }));
        }

        private void OnGUI()
        {
            EnsureStyles();
            float scale = Mathf.Max(0.1f,
                Mathf.Min(Screen.width / 1920f, Screen.height / 1080f));
            float virtualWidth = Screen.width / scale;
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);

            Rect panel = new Rect(1240f, 320f, 640f, 420f);
            if (virtualWidth < 1500f)
                panel.x = virtualWidth - panel.width - 40f;
            GUI.DrawTexture(panel, panelTexture);
            GUI.BeginGroup(panel);

            GUI.DrawTexture(new Rect(0f, 0f, panel.width, 48f), greenTexture);
            GUI.Label(new Rect(18f, 4f, 300f, 40f), "远景对焦练习", titleStyle);
            GUI.Label(new Rect(390f, 8f, 230f, 34f),
                $"清晰度 {focusSession.Focus01 * 100f:F0}%", focusStyle);

            Rect photoRect = new Rect(18f, 64f, 604f, 340f);
            if (focusedPhoto != null)
                GUI.DrawTexture(photoRect, focusedPhoto, ScaleMode.ScaleAndCrop, false);
            else if (sourcePhoto != null)
                GUI.DrawTexture(photoRect, sourcePhoto, ScaleMode.ScaleAndCrop, false);
            else
                GUI.Label(photoRect, "未找到远景照片，请检查 Resources 摄影素材。", bodyStyle);

            DrawViewfinder(photoRect);
            GUI.Label(new Rect(photoRect.x + 16f, photoRect.y + photoRect.height - 42f,
                    photoRect.width - 32f, 30f),
                Prompt(), bodyStyle);

            if (Time.realtimeSinceStartup < successEndsAt)
                GUI.Label(new Rect(photoRect.x, photoRect.y + 126f,
                        photoRect.width, 70f),
                    $"咔嚓！拍摄成功  ·  第 {focusSession.CapturedPhotos} 张",
                    successStyle);

            GUI.EndGroup();

            if (Time.realtimeSinceStartup < flashEndsAt)
            {
                float remaining = (flashEndsAt - Time.realtimeSinceStartup) / 0.20f;
                Color oldColor = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(remaining) * 0.82f);
                GUI.DrawTexture(new Rect(0f, 0f,
                    Screen.width / scale, Screen.height / scale), Texture2D.whiteTexture);
                GUI.color = oldColor;
            }

            GUI.matrix = oldMatrix;
        }

        private string Prompt()
        {
            if (training == null) return "等待坐姿伸膝训练模块";
            if (snapshot.IsSwitchingLeg) return "请自然放下小腿，准备建立对焦起点";
            if (!snapshot.IsDataValid) return "传感器信号暂停，画面清晰度已冻结";
            switch (snapshot.Stage)
            {
                case KneeExtensionStage.Preparing: return "坐稳后慢慢抬腿，照片会逐渐变清晰";
                case KneeExtensionStage.Extending: return "继续缓慢伸膝，让画面完全清晰";
                case KneeExtensionStage.Holding: return "对焦完成，请保持姿势等待快门";
                default: return "照片已拍好，请慢慢放下小腿";
            }
        }

        private void DrawViewfinder(Rect rect)
        {
            Color oldColor = GUI.color;
            GUI.color = Color.white;
            const float length = 38f;
            const float width = 3f;
            DrawLine(rect.x + 10f, rect.y + 10f, length, width);
            DrawLine(rect.x + 10f, rect.y + 10f, width, length);
            DrawLine(rect.xMax - 10f - length, rect.y + 10f, length, width);
            DrawLine(rect.xMax - 10f - width, rect.y + 10f, width, length);
            DrawLine(rect.x + 10f, rect.yMax - 10f - width, length, width);
            DrawLine(rect.x + 10f, rect.yMax - 10f - length, width, length);
            DrawLine(rect.xMax - 10f - length, rect.yMax - 10f - width, length, width);
            DrawLine(rect.xMax - 10f - width, rect.yMax - 10f - length, width, length);
            GUI.color = oldColor;
        }

        private static void DrawLine(float x, float y, float width, float height) =>
            GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);

        private void EnsureStyles()
        {
            if (titleStyle != null) return;
            titleStyle = Label(25, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
            focusStyle = Label(20, FontStyle.Bold, Color.white, TextAnchor.MiddleRight);
            bodyStyle = Label(18, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
            bodyStyle.normal.background = Solid(new Color(0f, 0f, 0f, 0.42f));
            successStyle = Label(30, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
            successStyle.normal.background = Solid(new Color(Green.r, Green.g, Green.b, 0.86f));
        }

        private void EnsureRenderTarget()
        {
            if (sourcePhoto == null || focusedPhoto != null) return;
            focusedPhoto = new RenderTexture(640, 360, 0, RenderTextureFormat.ARGB32)
            {
                name = "Rehab Photo Focus Preview",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            focusedPhoto.Create();
        }

        private static GUIStyle Label(int size, FontStyle fontStyle,
            Color color, TextAnchor alignment)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = fontStyle,
                alignment = alignment,
                wordWrap = true
            };
            style.normal.textColor = color;
            return style;
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private void OnDestroy()
        {
            if (focusedPhoto != null)
            {
                focusedPhoto.Release();
                Destroy(focusedPhoto);
            }
            if (blurMaterial != null) Destroy(blurMaterial);
            if (panelTexture != null) Destroy(panelTexture);
            if (greenTexture != null) Destroy(greenTexture);
            if (bodyStyle?.normal.background != null) Destroy(bodyStyle.normal.background);
            if (successStyle?.normal.background != null) Destroy(successStyle.normal.background);
        }

        [Serializable]
        private sealed class CaptureDiagnostic
        {
            public string leg;
            public int sessionVersion, capturedPhotos, repetitionsBeforeReturn;
            public float kneeAngleDeg, readyReferenceKneeDeg, targetKneeDeg;
            public float holdSeconds, focus01;
        }
    }
}
