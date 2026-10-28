using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using Roguelike.Features.Combat.Runtime;
using Roguelike.Infrastructure.Configuration;
using Roguelike.Infrastructure.Resources;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Unity.Transforms;

namespace Roguelike.Tests
{
    public sealed partial class CombatRuntimeTests
    {
        /// <summary>在真实帧末逐像素检测顶部和左侧覆盖，覆盖相机八向移动与三种实际 Game 分辨率。</summary>
        /// <returns>等待正式加载、窗口重排及真实屏幕读回的协程。</returns>
        /// <remarks>纯色仅作用于隔离测试实例以识别背景漏出，不改正式 Prefab 颜色；finally 恢复颜色、窗口尺寸、焦点并释放本次运行器。</remarks>
        [UnityTest]
        public IEnumerator FormalHudEdgesRemainCoveredInRenderedMovingFrames()
        {
#if UNITY_EDITOR
            using var gameView = new HudCoverageGameView();
#endif
            using var initializer = new YooAssetPackageInitializer();
            yield return Wait(initializer.InitializeAsync(ResourcePlayMode.EditorSimulate, CancellationToken.None));
            var config = new LubanConfigService(initializer);
            yield return Wait(config.InitializeAsync(CancellationToken.None));
            var resources = new YooAssetResourceService(initializer, config);
            var definition = CombatRunDefinition.Create(config.Tables, 1);
            var root = new GameObject("Rendered HUD edge acceptance");
            var runner = root.AddComponent<CombatRuntimeRunner>();
            var input = new TestInput();
            Image bar = null; Color savedColor = default;
            const string directory = ".local/hud-gap-pixel-fix-20261027";
            var report = new List<string>();
            try
            {
                yield return Wait(runner.InitializeAsync(definition, resources, input, CancellationToken.None));
                runner.TogglePause();
                Assert.That(runner.Coordinator.Paused, Is.True);
                var world = FindCombatWorld();
                Canvas canvas = runner.FormalUiRoot.GetComponent<Canvas>();
                Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                Assert.That(canvas.pixelPerfect, Is.True);
                Assert.That(canvas.worldCamera, Is.Null);
                bar = runner.FormalUiRoot.transform.Find("TopBar").GetComponent<Image>();
                savedColor = bar.color; bar.color = new Color(1, 0, 1, 1);
                var rect = (RectTransform)bar.transform;
                Vector2 originalAnchorMin = rect.anchorMin, originalAnchorMax = rect.anchorMax,
                    originalPosition = rect.anchoredPosition, originalSize = rect.sizeDelta;
                Directory.CreateDirectory(directory);
                foreach (Vector2Int size in new[] { new Vector2Int(1334, 750), new Vector2Int(1280, 720), new Vector2Int(1001, 563) })
                {
#if UNITY_EDITOR
                    gameView.Select(size.x, size.y);
#endif
                    for (int warmup = 0; warmup < 3; warmup++) { yield return null; yield return new WaitForEndOfFrame(); }
                    Assert.That(Screen.width, Is.EqualTo(size.x)); Assert.That(Screen.height, Is.EqualTo(size.y));
                    int frameCount = 0;
                    bool cameraMoved = false;
                    foreach (float2 direction in new[] { new float2(-1, 0), new float2(0, 1), new float2(-1, 1), new float2(1, 1),
                        new float2(1, 0), new float2(0, -1), new float2(1, -1), new float2(-1, -1) })
                    {
                        for (int frame = 0; frame < 3; frame++)
                        {
                            // 冻结升级/伤害，只在隔离 World 移动权威玩家位置，让生产 LateUpdate 自行跟随相机。
                            Vector3 previousCamera = runner.ViewCamera.transform.position;
                            CombatUnit player = runner.Session.ReadUnit(0);
                            player.Position += direction * 0.08f; player.PreviousPosition = player.Position;
                            var playerEntity = runner.Session.UnitEntity(0);
                            world.EntityManager.SetComponentData(playerEntity, player);
                            LocalTransform transform = world.EntityManager.GetComponentData<LocalTransform>(playerEntity);
                            transform.Position = new float3(player.Position, 0);
                            world.EntityManager.SetComponentData(playerEntity, transform);
                            runner.Visuals.Synchronize();
                            yield return null;
                            yield return new WaitForEndOfFrame();
                            cameraMoved |= Vector3.Distance(previousCamera, runner.ViewCamera.transform.position) > 0.001f;
                            Texture2D capture = ScreenCapture.CaptureScreenshotAsTexture();
                            try
                            {
                                Assert.That(capture.width, Is.EqualTo(size.x)); Assert.That(capture.height, Is.EqualTo(size.y));
                                Color32[] colors = capture.GetPixels32();
                                int missingTop = 0, missingLeft = 0;
                                for (int x = 0; x < size.x; x++) if (!IsHudCoveragePixel(colors[(size.y - 1) * size.x + x])) missingTop++;
                                int barHeight = Mathf.FloorToInt(rect.rect.height * canvas.scaleFactor);
                                for (int y = size.y - barHeight + 4; y < size.y - 4; y++) if (!IsHudCoveragePixel(colors[y * size.x])) missingLeft++;
                                report.Add(size.x + "x" + size.y + "|frame=" + frameCount + "|top=" + missingTop + "|left=" + missingLeft);
                                if (missingTop > 0 || missingLeft > 0)
                                    File.WriteAllBytes(Path.Combine(directory, "edge-failure-" + size.x + "x" + size.y + ".png"), capture.EncodeToPNG());
                                Assert.That(missingTop, Is.Zero, "Top screen row must stay covered in an actual moving rendered frame.");
                                Assert.That(missingLeft, Is.Zero, "Left HUD edge must stay covered in an actual moving rendered frame.");
                                if (frameCount == 0) File.WriteAllBytes(Path.Combine(directory, "edge-covered-" + size.x + "x" + size.y + ".png"), capture.EncodeToPNG());
                            }
                            finally { UnityEngine.Object.Destroy(capture); }
                            frameCount++;
                        }
                    }
                    Assert.That(frameCount, Is.EqualTo(24));
                    Assert.That(cameraMoved, Is.True, "The test must observe real LateUpdate camera movement.");
                    input.Frame = default;
                    Assert.That(rect.anchorMin, Is.EqualTo(originalAnchorMin)); Assert.That(rect.anchorMax, Is.EqualTo(originalAnchorMax));
                    Assert.That(rect.anchoredPosition, Is.EqualTo(originalPosition)); Assert.That(rect.sizeDelta, Is.EqualTo(originalSize));
                }
            }
            finally
            {
                if (bar != null) bar.color = savedColor;
                File.WriteAllLines(Path.Combine(directory, "rendered-coverage.txt"), report);
                if (root != null) { runner.Close(); UnityEngine.Object.Destroy(root); }
            }
        }

        /// <summary>通过实际 EventSystem 射线定位并点击按钮，验证 Overlay 下交互命中而非直接调用 onClick。</summary>
        /// <param name="button">正式 Prefab 实例上的可点击按钮。</param>
        /// <returns>等待新激活面板完成实际绘制后执行射线点击的协程。</returns>
        /// <remarks>仅供隔离 UI 流程测试；执行生产 Button 的指针点击处理，可能改变当前测试的单局或面板状态。</remarks>
        private static IEnumerator ClickHudButtonThroughRaycast(Button button)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            Canvas.ForceUpdateCanvases();
            Assert.That(EventSystem.current, Is.Not.Null);
            var rect = (RectTransform)button.transform;
            var pointer = new PointerEventData(EventSystem.current)
            {
                position = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center)),
                button = PointerEventData.InputButton.Left
            };
            var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(pointer, hits);
            Assert.That(hits.Count, Is.GreaterThan(0), "The actual HUD button must receive a UI raycast: " + button.name +
                " position=" + pointer.position + " depth=" + button.targetGraphic.depth);
            Assert.That(hits[0].gameObject.transform == button.transform || hits[0].gameObject.transform.IsChildOf(button.transform), Is.True,
                "The topmost UI hit must be the requested button or its label.");
            Assert.That(ExecuteEvents.ExecuteHierarchy(hits[0].gameObject, pointer, ExecuteEvents.pointerClickHandler), Is.Not.Null);
        }

        /// <summary>识别隔离测试实例的纯色覆盖，不把字体、背景或离屏黑色误认为顶栏。</summary>
        /// <param name="pixel">实际屏幕读回像素。</param><returns>红蓝高、绿色低的覆盖标识。</returns>
        private static bool IsHudCoveragePixel(Color32 pixel) => pixel.r > 200 && pixel.b > 200 && pixel.g < 80;

        /// <summary>在真实帧末截取完整游戏屏幕，包含 Overlay HUD，不能用 Camera.Render 替代。</summary>
        /// <param name="fileName">runtime-review 内的截图文件名。</param><returns>等待实际帧末并读回屏幕的协程。</returns>
        /// <remarks>临时读回纹理在 finally 销毁，不保存 Unity 资产；编辑器只聚焦本实例的 Game 窗口。</remarks>
        private static IEnumerator CaptureGameScreen(string fileName)
        {
#if UNITY_EDITOR
            Type viewType = typeof(UnityEditor.EditorWindow).Assembly.GetType("UnityEditor.GameView");
            UnityEditor.EditorWindow.GetWindow(viewType).Focus();
#endif
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D pixels = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                Assert.That(pixels.width, Is.EqualTo(Screen.width)); Assert.That(pixels.height, Is.EqualTo(Screen.height));
                Assert.That(pixels.width, Is.GreaterThan(100));
                Directory.CreateDirectory("outputs/combat-config/runtime-review");
                File.WriteAllBytes(Path.Combine("outputs/combat-config/runtime-review", fileName), pixels.EncodeToPNG());
            }
            finally { UnityEngine.Object.Destroy(pixels); }
        }

#if UNITY_EDITOR
        /// <summary>隔离测试的 Game 窗口尺寸所有者；只登记本轮三个临时尺寸，结束恢复原选择与焦点。</summary>
        private sealed class HudCoverageGameView : IDisposable
        {
            private readonly UnityEditor.EditorWindow view, previousFocus;
            private readonly object group;
            private readonly Type groupType;
            private readonly PropertyInfo selected;
            private readonly int originalSelection;
            private readonly List<int> addedTotalIndices = new List<int>();

            /// <summary>在已核验的当前 Editor 创建尺寸编辑会话，不修改项目资产。</summary>
            /// <remarks>使用当前 Unity 反射核验的 GameViewSizes API，只有本测试拥有的新尺寸会在 Dispose 移除。</remarks>
            public HudCoverageGameView()
            {
                Assembly editor = typeof(UnityEditor.EditorWindow).Assembly;
                Type sizes = editor.GetType("UnityEditor.GameViewSizes");
                Type groupEnum = editor.GetType("UnityEditor.GameViewSizeGroupType");
                object instance = typeof(UnityEditor.ScriptableSingleton<>).MakeGenericType(sizes)
                    .GetProperty("instance", BindingFlags.Static | BindingFlags.Public).GetValue(null);
                group = sizes.GetMethod("GetGroup").Invoke(instance, new[] { Enum.Parse(groupEnum, "Standalone") });
                groupType = group.GetType();
                previousFocus = UnityEditor.EditorWindow.focusedWindow;
                view = UnityEditor.EditorWindow.GetWindow(editor.GetType("UnityEditor.GameView"));
                selected = view.GetType().GetProperty("selectedSizeIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                originalSelection = (int)selected.GetValue(view);
            }

            /// <summary>增加并选择本测试独占的固定像素分辨率，保持 Game 窗口聚焦以完成帧末绘制。</summary>
            /// <param name="width">测试宽度。</param><param name="height">测试高度。</param>
            public void Select(int width, int height)
            {
                Assembly editor = typeof(UnityEditor.EditorWindow).Assembly;
                Type size = editor.GetType("UnityEditor.GameViewSize"), kind = editor.GetType("UnityEditor.GameViewSizeType");
                int total = (int)groupType.GetMethod("GetBuiltinCount").Invoke(group, null) + (int)groupType.GetMethod("GetCustomCount").Invoke(group, null);
                object item = Activator.CreateInstance(size, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { Enum.Parse(kind, "FixedResolution"), (object)width, height, "HUD pixel coverage temporary" }, null);
                groupType.GetMethod("AddCustomSize").Invoke(group, new[] { item });
                addedTotalIndices.Add(total); selected.SetValue(view, total); view.Focus(); view.Repaint();
            }

            /// <summary>还原原分辨率选择并倒序移除本次登记的尺寸，不清理用户已有尺寸。</summary>
            /// <remarks>尺寸回收仅作用于本会话登记的精确索引；最后恢复此前焦点窗口。</remarks>
            public void Dispose()
            {
                selected.SetValue(view, originalSelection);
                for (int i = addedTotalIndices.Count - 1; i >= 0; i--)
                {
                    // 当前 Unity 的 RemoveCustomSize 接受总索引，由其内部扣除内置尺寸数。
                    groupType.GetMethod("RemoveCustomSize").Invoke(group, new object[] { addedTotalIndices[i] });
                }
                if (previousFocus != null) previousFocus.Focus();
            }
        }
#endif
    }
}
