using System;
using UnityEngine;

namespace RehabPhotoGame
{
    /// <summary>将现有游戏素材组成双联 JPG；不是人物截图，也不是人体测量结果。</summary>
    public static class S2CombinationPhotoWriter
    {
        public static Rect ViewRect(string type, bool second) =>
            new Rect(.1f, second ? (type == "A" ? .2f : 0f) : (type == "B" ? .2f : .1f), .8f, .8f);

        public static byte[] Encode(S2CombinationPhoto photo)
        {
            Texture2D first = Resources.Load<Texture2D>(photo.first_resource);
            Texture2D second = Resources.Load<Texture2D>(photo.second_resource);
            if (first == null || second == null) throw new InvalidOperationException("组合摄影素材未加载。");
            const int width = 640, height = 360, gap = 16;
            var pair = new Texture2D(width * 2 + gap, height, TextureFormat.RGB24, false);
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture old = RenderTexture.active;
            try
            {
                Color32[] pixels = new Color32[(width * 2 + gap) * height];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(16, 43, 66, 255);
                pair.SetPixels32(pixels);
                Copy(first, ViewRect(photo.combination_type, false), pair, target, 0, width, height);
                Copy(second, ViewRect(photo.combination_type, true), pair, target, width + gap, width, height);
                pair.Apply();
                return pair.EncodeToJPG(90);
            }
            finally
            {
                RenderTexture.active = old;
                RenderTexture.ReleaseTemporary(target);
                if (Application.isPlaying) UnityEngine.Object.Destroy(pair);
                else UnityEngine.Object.DestroyImmediate(pair);
            }
        }

        private static void Copy(Texture source, Rect uv, Texture2D destination, RenderTexture target, int x, int w, int h)
        {
            Graphics.Blit(source, target, new Vector2(uv.width, uv.height), new Vector2(uv.x, uv.y));
            RenderTexture.active = target;
            destination.ReadPixels(new Rect(0, 0, w, h), x, 0, false);
        }
    }
}
