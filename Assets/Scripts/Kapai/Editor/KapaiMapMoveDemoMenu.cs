using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using ProjectX.Migration;

namespace Rouge.Kapai.Editor
{
    /// <summary>
    /// 一键生成地图移动演示场景（复刻 kapai CocosMapMovementBuilder.BuildScene 的运行时结构）。
    /// 用法：菜单 Rouge/Kapai/创建地图移动演示场景 → 进入 Play → WASD/方向键控制角色。
    /// 角色模型运行时从 StreamingAssets/ProjectX/CocosAnimations 读取 hero/*.ani + .png。
    /// </summary>
    public static class KapaiMapMoveDemoMenu
    {
        private const string CanvasName = "MapWorldCanvas";
        private const string PlayerName = "PlayerMapNode";
        private const string ModelName = "CompositeModel";
        private const string CameraName = "Main Camera";

        [MenuItem("Rouge/Kapai/创建地图移动演示场景")]
        public static void CreateDemo()
        {
            RemoveExisting(CanvasName);
            RemoveExisting(PlayerName);
            RemoveExisting(ModelName);

            CocosMainCharacterData data = new CocosMainCharacterData
            {
                playerName = "KapaiHero",
                level = 1,
                professional = 1,
                weaponEffectId = 0,
                mountId = 0,
                wingId = 0,
                artifactId = 0
            };

            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject cameraObject = new GameObject(CameraName, typeof(Camera), typeof(AudioListener), typeof(CocosMapCameraFollow));
                cameraObject.tag = "MainCamera";
                camera = cameraObject.GetComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = 4.5f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.012f, 0.03f, 0.04f, 1f);
                camera.transform.position = new Vector3(0f, 0f, -10f);
            }

            GameObject canvasObject = new GameObject(CanvasName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(2400f, 1600f);
            canvasRect.localScale = Vector3.one * 0.01f;

            CreatePanel(canvasRect, "MapBackground", Vector2.zero, new Vector2(2400f, 1600f), new Color(0.055f, 0.12f, 0.105f, 1f));
            CreatePanel(canvasRect, "RoadHorizontal", Vector2.zero, new Vector2(2100f, 310f), new Color(0.16f, 0.22f, 0.18f, 1f));
            CreatePanel(canvasRect, "RoadVertical", new Vector2(120f, 0f), new Vector2(330f, 1350f), new Color(0.14f, 0.20f, 0.17f, 1f));
            for (int i = -3; i <= 3; i++)
                CreatePanel(canvasRect, "RoadMarker_" + i, new Vector2(i * 300f, -i * 21f), new Vector2(120f, 16f), new Color(0.62f, 0.64f, 0.45f, 0.75f));

            GameObject player = new GameObject(PlayerName, typeof(RectTransform), typeof(CocosMainCharacterController), typeof(CocosMapCharacterMotor));
            RectTransform playerRect = player.GetComponent<RectTransform>();
            playerRect.SetParent(canvasRect, false);
            playerRect.anchorMin = playerRect.anchorMax = playerRect.pivot = new Vector2(0.5f, 0.5f);
            playerRect.sizeDelta = Vector2.zero;

            GameObject modelObject = new GameObject(ModelName, typeof(RectTransform), typeof(CocosModelAniPlayer));
            RectTransform modelRect = modelObject.GetComponent<RectTransform>();
            modelRect.SetParent(playerRect, false);
            modelRect.anchorMin = modelRect.anchorMax = modelRect.pivot = new Vector2(0.5f, 0.5f);
            modelRect.sizeDelta = Vector2.zero;
            modelRect.localScale = Vector3.one * 1.35f;

            CocosMainCharacterController character = player.GetComponent<CocosMainCharacterController>();
            if (!character.Configure(data))
                throw new System.InvalidOperationException("角色模型加载失败: " + character.Model.LoadError);

            CocosMapCharacterMotor motor = player.GetComponent<CocosMapCharacterMotor>();
            motor.Configure(character, playerRect, 220f, new Vector2(900f, 550f), true);

            CocosMapCameraFollow follow = camera.GetComponent<CocosMapCameraFollow>();
            if (follow == null) follow = camera.gameObject.AddComponent<CocosMapCameraFollow>();
            follow.Configure(playerRect, 8f, new Vector3(0f, 0f, -10f));
            follow.SnapNow();

            Selection.activeGameObject = player;
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Debug.Log("Kapai 地图移动演示场景已创建：WASD/方向键移动，角色模型为 professional=1 (Z_0)。");
        }

        private static void CreatePanel(RectTransform parent, string name, Vector2 anchoredPosition, Vector2 size, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        private static void RemoveExisting(string objectName)
        {
            foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == objectName)
                {
                    Object.DestroyImmediate(root);
                    break;
                }
            }
        }
    }
}
