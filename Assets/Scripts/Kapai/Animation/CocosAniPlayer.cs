using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectX.Migration
{
    [ExecuteAlways]
    public sealed class CocosAniPlayer : MonoBehaviour
    {
        public const string StreamingRoot = "ProjectX/CocosAnimations";
        private const byte FlipY = 0x1;
        private const byte FlipX = 0x2;

        [SerializeField] private string resourcePath;
        [SerializeField] private string textureResourcePath;
        [SerializeField] private int actionIndex;
        [SerializeField] private bool loop = true;
        [SerializeField] private bool playAutomatically = true;
        [SerializeField] private float speed = 1f;
        [SerializeField] private int currentActionFrame;
        [SerializeField] private bool loaded;
        [SerializeField] private string loadError;
        [SerializeField] private bool externallyClocked;

        private CocosAniData data;
        private Texture2D texture;
        [SerializeField] private List<RawImage> moduleImages = new List<RawImage>();
        private float elapsed;
        private Action<CocosAniPlayer> completionCallback;
        private Action<CocosAniPlayer> frameCallback;
        private int callbackActionFrame = -1;
        private bool completionPending;
        private bool frameCallbackPending;

        public string ResourcePath => resourcePath;
        public string TextureResourcePath => string.IsNullOrEmpty(textureResourcePath) ? resourcePath : textureResourcePath;
        public bool IsLoaded => loaded;
        public string LoadError => loadError;
        public int ModuleCount => data != null ? data.modules.Length : 0;
        public int FrameCount => data != null ? data.frames.Length : 0;
        public int ActionCount => data != null ? data.actions.Length : 0;
        public int ActionIndex => actionIndex;
        public int CurrentActionFrame => currentActionFrame;
        public int CurrentActionFrameCount => loaded && data != null && actionIndex >= 0 && actionIndex < data.actions.Length
            ? data.actions[actionIndex].frames.Length
            : 0;
        public bool IsLooping => loop;
        public bool IsPlaying => loaded && playAutomatically;
        public bool IsExternallyClocked => externallyClocked;
        public bool IsFlippedX => transform.localScale.x < 0f;
        // ImodAnim schedules every frame using the final duration byte read from the ANI,
        // rather than each action frame's individual duration value.
        public float CurrentFrameDuration => loaded && data != null
            ? data.SourceTickDuration
            : CocosAniData.FrameRate * 5f;

        public void Configure(string value, int valueActionIndex = 0, bool valueLoop = true)
        {
            resourcePath = NormalizeResourcePath(value);
            textureResourcePath = resourcePath;
            actionIndex = valueActionIndex;
            loop = valueLoop;
            LoadNow();
        }

        public bool ConfigureAndLoad(string value, int valueActionIndex = 0, bool valueLoop = true)
        {
            resourcePath = NormalizeResourcePath(value);
            textureResourcePath = resourcePath;
            actionIndex = valueActionIndex;
            loop = valueLoop;
            return LoadNow();
        }

        public bool ConfigureAndLoad(
            string aniResourcePath,
            string texturePath,
            int valueActionIndex = 0,
            bool valueLoop = true)
        {
            resourcePath = NormalizeResourcePath(aniResourcePath);
            textureResourcePath = NormalizeResourcePath(texturePath);
            actionIndex = valueActionIndex;
            loop = valueLoop;
            return LoadNow();
        }

        public void SetPlaybackEnabled(bool enabled) => playAutomatically = enabled;

        public void SetExternalClock(bool enabled) => externallyClocked = enabled;

        public void SetCompletionCallback(Action<CocosAniPlayer> callback) => completionCallback = callback;

        public void SetFrameCallback(int actionFrame, Action<CocosAniPlayer> callback)
        {
            callbackActionFrame = actionFrame;
            frameCallback = callback;
            frameCallbackPending = callback != null;
        }

        public bool PlayOnce(int valueActionIndex = 0, Action<CocosAniPlayer> onCompleted = null)
        {
            if (!loaded || data == null || data.actions.Length == 0)
                return false;
            if (onCompleted != null)
                completionCallback = onCompleted;
            loop = false;
            playAutomatically = true;
            completionPending = true;
            RestartAction(valueActionIndex);
            return true;
        }

        public bool PlayLoop(int valueActionIndex = 0)
        {
            if (!loaded || data == null || data.actions.Length == 0)
                return false;
            loop = true;
            playAutomatically = true;
            completionPending = false;
            RestartAction(valueActionIndex);
            return true;
        }

        public void StopPlayback() => playAutomatically = false;

        public bool LoadNow()
        {
            RebuildModuleImageCache();
            ClearRuntimeData();
            loadError = string.Empty;
            try
            {
                string normalized = NormalizeResourcePath(resourcePath);
                if (string.IsNullOrEmpty(normalized))
                    throw new InvalidOperationException("ANI resource path is empty.");

                string root = Path.Combine(Application.streamingAssetsPath, StreamingRoot);
                string aniPath = Path.Combine(root, normalized + ".ani");
                string normalizedTexture = NormalizeResourcePath(TextureResourcePath);
                string pngPath = Path.Combine(root, normalizedTexture + ".png");
                if (!File.Exists(aniPath))
                    throw new FileNotFoundException("ANI file was not found.", aniPath);
                if (!File.Exists(pngPath))
                    throw new FileNotFoundException("ANI texture was not found.", pngPath);

                data = CocosAniData.Parse(File.ReadAllBytes(aniPath));
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = Path.GetFileNameWithoutExtension(pngPath),
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                if (!texture.LoadImage(File.ReadAllBytes(pngPath), false))
                    throw new InvalidOperationException("Unity failed to decode ANI texture: " + pngPath);

                actionIndex = Mathf.Clamp(actionIndex, 0, data.actions.Length - 1);
                currentActionFrame = 0;
                elapsed = 0f;
                completionPending = false;
                frameCallbackPending = frameCallback != null;
                loaded = true;
                RenderCurrentFrame();
                DispatchFrameCallback();
                return true;
            }
            catch (Exception exception)
            {
                loaded = false;
                loadError = exception.Message;
                Debug.LogError("Cocos ANI load failed: " + resourcePath + "\n" + exception, this);
                return false;
            }
        }

        public void SetAction(int value, bool restart = true)
        {
            if (data == null || data.actions.Length == 0)
                return;
            actionIndex = Mathf.Clamp(value, 0, data.actions.Length - 1);
            if (restart)
            {
                currentActionFrame = 0;
                elapsed = 0f;
                completionPending = !loop;
                frameCallbackPending = frameCallback != null;
            }
            RenderCurrentFrame();
            DispatchFrameCallback();
        }

        public void AdvanceOneFrame()
        {
            if (!loaded || data == null || data.actions == null || data.actions.Length == 0 || actionIndex < 0 || actionIndex >= data.actions.Length)
                return;
            CocosAniData.Action action = data.actions[actionIndex];
            if (currentActionFrame + 1 < action.frames.Length)
                currentActionFrame++;
            else if (loop)
                currentActionFrame = 0;
            RenderCurrentFrame();
            DispatchFrameCallback();
        }

        public bool AdvancePlaybackTick()
        {
            if (!loaded || data == null || data.actions == null || data.actions.Length == 0 ||
                actionIndex < 0 || actionIndex >= data.actions.Length || !playAutomatically)
                return false;

            CocosAniData.Action action = data.actions[actionIndex];
            if (!loop && currentActionFrame + 1 >= action.frames.Length)
            {
                playAutomatically = false;
                elapsed = 0f;
                CompleteOnce();
                return false;
            }

            AdvanceOneFrame();
            return true;
        }

        private void OnEnable()
        {
            if ((!loaded || data == null || data.actions == null || data.actions.Length == 0 || actionIndex < 0 || actionIndex >= data.actions.Length) &&
                !string.IsNullOrWhiteSpace(resourcePath))
                LoadNow();
        }

        private void Update()
        {
            if (!Application.isPlaying || externallyClocked || !playAutomatically || !loaded || data == null || speed <= 0f)
                return;

            elapsed += Time.unscaledDeltaTime * speed;
            int safety = 0;
            while (elapsed >= data.SourceTickDuration && safety++ < 100)
            {
                elapsed -= data.SourceTickDuration;
                if (!AdvancePlaybackTick())
                    break;
            }
        }

        private void RestartAction(int valueActionIndex)
        {
            actionIndex = Mathf.Clamp(valueActionIndex, 0, data.actions.Length - 1);
            currentActionFrame = 0;
            elapsed = 0f;
            frameCallbackPending = frameCallback != null;
            RenderCurrentFrame();
            DispatchFrameCallback();
        }

        private void DispatchFrameCallback()
        {
            if (!frameCallbackPending || frameCallback == null || currentActionFrame != callbackActionFrame)
                return;
            frameCallbackPending = false;
            Action<CocosAniPlayer> callback = frameCallback;
            callback(this);
        }

        private void CompleteOnce()
        {
            if (!completionPending)
                return;
            completionPending = false;
            Action<CocosAniPlayer> callback = completionCallback;
            if (callback != null)
                callback(this);
        }

        private void RenderCurrentFrame()
        {
            if (!loaded || data == null || texture == null)
                return;

            CocosAniData.Action action = data.actions[actionIndex];
            currentActionFrame = Mathf.Clamp(currentActionFrame, 0, action.frames.Length - 1);
            CocosAniData.Frame frame = data.frames[action.frames[currentActionFrame]];
            EnsureImageCount(frame.modules.Length);

            for (int i = 0; i < moduleImages.Count; i++)
            {
                RawImage image = moduleImages[i];
                bool visible = i < frame.modules.Length;
                image.gameObject.SetActive(visible);
                if (!visible)
                    continue;

                CocosAniData.FrameModule frameModule = frame.modules[i];
                CocosAniData.Module module = data.modules[frameModule.moduleId];
                image.texture = texture;
                float u = module.x / (float)texture.width;
                float v = 1f - (module.y + module.height) / (float)texture.height;
                float uw = module.width / (float)texture.width;
                float vh = module.height / (float)texture.height;
                image.uvRect = new Rect(u, v, uw, vh);

                RectTransform rect = image.rectTransform;
                rect.sizeDelta = new Vector2(module.width, module.height);
                rect.anchoredPosition = new Vector2(
                    frameModule.x + module.width * 0.5f,
                    -frameModule.y - module.height * 0.5f);
                rect.localScale = new Vector3(
                    (frameModule.flags & FlipX) != 0 ? -1f : 1f,
                    (frameModule.flags & FlipY) != 0 ? -1f : 1f,
                    1f);
            }
        }

        private void EnsureImageCount(int count)
        {
            while (moduleImages.Count < count)
            {
                GameObject child = new GameObject("AniModule" + moduleImages.Count, typeof(RectTransform), typeof(RawImage));
                child.hideFlags = HideFlags.HideInHierarchy;
                RectTransform rect = child.GetComponent<RectTransform>();
                rect.SetParent(transform, false);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                RawImage image = child.GetComponent<RawImage>();
                image.raycastTarget = false;
                moduleImages.Add(image);
            }
        }

        private void RebuildModuleImageCache()
        {
            if (moduleImages == null)
                moduleImages = new List<RawImage>();
            moduleImages.RemoveAll(image => image == null);
            foreach (RawImage image in GetComponentsInChildren<RawImage>(true))
            {
                if (image.gameObject.name.StartsWith("AniModule", StringComparison.Ordinal) &&
                    !moduleImages.Contains(image))
                {
                    image.gameObject.hideFlags |= HideFlags.HideInHierarchy;
                    moduleImages.Add(image);
                }
            }
        }

        private void ClearRuntimeData()
        {
            loaded = false;
            data = null;
            elapsed = 0f;
            completionPending = false;
            frameCallbackPending = false;
            foreach (RawImage image in moduleImages)
            {
                if (image != null)
                    image.gameObject.SetActive(false);
            }
            if (texture != null)
            {
                if (Application.isPlaying)
                    Destroy(texture);
                else
                    DestroyImmediate(texture);
                texture = null;
            }
        }

        private void OnDestroy() => ClearRuntimeData();

        private static string NormalizeResourcePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            string normalized = value.Replace('\\', '/').Trim().TrimStart('/');
            if (normalized.EndsWith(".ani", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - 4);
            return normalized;
        }
    }
}
