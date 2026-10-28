using System.Collections;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Roguelike.Features.Combat.Runtime;
using Roguelike.Features.Combat.Run;
using Roguelike.Infrastructure.Configuration;
using Roguelike.Infrastructure.Resources;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Roguelike.Tests
{
    public sealed partial class CombatRuntimeTests
    {
        /// <summary>隔离验证普通Stage、双职业试炼和Combat场景的真实红线像素、开关、重开、连续切换及资源清理。</summary>
        /// <returns>正式资源加载与摄像机渲染的协程。</returns>
        /// <remarks>使用无Profile的自有运行器，不修改启动覆盖或用户角色选择；目标位置只用于边界可见验证。</remarks>
        [UnityTest]
        public IEnumerator UniversalArenaBoundaryRendersTogglesRestartsAndCleansAcrossMaps()
        {
            using var initializer=new YooAssetPackageInitializer();yield return Wait(initializer.InitializeAsync(ResourcePlayMode.EditorSimulate,CancellationToken.None));
            var config=new LubanConfigService(initializer);yield return Wait(config.InitializeAsync(CancellationToken.None));
            var resources=new TribulationTrackingResources(new YooAssetResourceService(initializer,config));int baseline=World.All.Count;
            foreach(int stageId in new[]{1,101,102,0})
            {
                var root=new GameObject("Universal arena boundary isolated test");var runner=root.AddComponent<CombatRuntimeRunner>();
                Material ownedMaterial=null;GameObject boundary=null;
                try
                {
                    if(stageId==0)yield return Wait(runner.InitializeAsync(config.Tables.TbPerformanceScenario.Get(4),resources,new TestInput(),CancellationToken.None));
                    else yield return Wait(runner.InitializeAsync(CombatRunDefinition.Create(config.Tables,stageId),resources,new TestInput(),CancellationToken.None));
                    Assert.That(runner.Ready,Is.True,"Formal resources must finish initialization.");runner.TogglePause();
                    boundary=runner.ArenaBoundaryRoot;Assert.That(boundary,Is.Not.Null);Assert.That(runner.ArenaBoundaryVisible,Is.True,"Each newly entered combat map starts with the configured visible default.");
                    var line=boundary.GetComponent<LineRenderer>();ownedMaterial=line.sharedMaterial;
                    Assert.That(root.GetComponentsInChildren<LineRenderer>(true).Count(l=>l.gameObject.name.Contains("Boundary")),Is.EqualTo(1),"Trial must not retain a second legacy boundary.");
                    var bounds=runner.Session.MovementBoundsHalfSize;var player=runner.Session.ReadUnit(0);
                    player.Position=player.PreviousPosition=new float2(bounds.x-1,0);FindCombatWorld().EntityManager.SetComponentData(runner.Session.UnitEntity(0),player);
                    yield return null;yield return new WaitForEndOfFrame();
                    Assert.That(line.GetPosition(2),Is.EqualTo(new Vector3(bounds.x,bounds.y,0)));
                    int visibleRed=CountArenaBoundaryRedPixels(runner.ViewCamera);
                    runner.SetArenaBoundaryVisible(false);Assert.That(boundary.activeSelf,Is.False);
                    int hiddenRed=CountArenaBoundaryRedPixels(runner.ViewCamera);
                    Assert.That(visibleRed,Is.GreaterThan(hiddenRed),"The actual camera must render red boundary pixels, and hiding must remove them.");
                    Assert.That(runner.Session.MovementBoundsHalfSize,Is.EqualTo(bounds));
                    // 正式关卡仍要求胜负结算后重开；受控失败仅建立该测试前置，不改变产品规则或真实存档。
                    if(runner.RunModel!=null)Assert.That(runner.RunModel.ApplyPlayerDamage(runner.RunModel.CurrentHealth),Is.True);
                    Assert.That(runner.Restart(),Is.True,"Restart must succeed after the formal settlement precondition.");Assert.That(runner.ArenaBoundaryVisible,Is.False,"Restart keeps the display choice within this runner.");
                    runner.TogglePause();runner.SetArenaBoundaryVisible(true);
                    Assert.That(runner.ArenaBoundaryRoot,Is.SameAs(boundary));
                    player=runner.Session.ReadUnit(0);player.Position=player.PreviousPosition=new float2(bounds.x-1,0);FindCombatWorld().EntityManager.SetComponentData(runner.Session.UnitEntity(0),player);
                    yield return null;yield return new WaitForEndOfFrame();
                    Assert.That(CountArenaBoundaryRedPixels(runner.ViewCamera),Is.GreaterThan(hiddenRed));
                    if(stageId==1||stageId==101)yield return CaptureCamera(runner.ViewCamera,stageId==1?"boundary-stage1-visible.png":"boundary-trial-visible.png");
                }
                finally { runner.Close();Object.Destroy(root); }
                yield return null;
                Assert.That(boundary==null,Is.True,"The previous map boundary must be destroyed.");Assert.That(ownedMaterial==null,Is.True,"The previous boundary material must be destroyed.");Assert.That(resources.Outstanding,Is.Zero);Assert.That(World.All.Count,Is.EqualTo(baseline));
            }
        }

        /// <summary>同一冻结摄像机读回红色像素，用显示与隐藏差值证明真实渲染，不以对象存在代替可见性。</summary>
        /// <param name="camera">已就绪的正式战斗相机。</param><returns>当前画面的明显红色像素数。</returns>
        /// <remarks>RenderTexture与读回纹理只存在内存；恢复相机目标和全局活动RT后立即释放，不保存预览资产。</remarks>
        private static int CountArenaBoundaryRedPixels(Camera camera)
        {
            var oldTarget=camera.targetTexture;var oldActive=RenderTexture.active;
            var target=RenderTexture.GetTemporary(Screen.width,Screen.height,24);var pixels=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture=target;camera.Render();RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0);pixels.Apply();
                return pixels.GetPixels32().Count(c=>c.r>220&&c.g<60&&c.b<60);
            }
            finally { camera.targetTexture=oldTarget;RenderTexture.active=oldActive;RenderTexture.ReleaseTemporary(target);Object.Destroy(pixels); }
        }
    }
}
