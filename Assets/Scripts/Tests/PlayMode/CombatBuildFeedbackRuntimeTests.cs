using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Roguelike.Features.Combat;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using Roguelike.Features.Combat.Runtime;
using Roguelike.Infrastructure.Configuration;
using Roguelike.Infrastructure.Resources;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Roguelike.Tests
{
    public sealed partial class CombatRuntimeTests
    {
        /// <summary>真实YooAsset与UI射线验证普通重掷／跳过、正式职业伤害目标、结算保存和返回后启用。</summary>
        /// <returns>正式资源加载、交互、受控Boss边界与资源清理的协程。</returns>
        /// <remarks>仅在唯一临时隔离目录保存Profile；受控蓄能、目标位置和Boss时间用于边界测试，不冒充自然完整通关。</remarks>
        [UnityTest]
        public IEnumerator BuildFeedbackDraftControlsAndIsolatedUnlockPersistAcrossEntry()
        {
            string isolated=Path.Combine(Path.GetTempPath(),"roguelike-feedback-profile-tests",Guid.NewGuid().ToString("N"));
            tribulationTestInitializer=new YooAssetPackageInitializer();var initializer=tribulationTestInitializer;
            yield return Wait(initializer.InitializeAsync(ResourcePlayMode.EditorSimulate,CancellationToken.None));
            var config=new LubanConfigService(initializer);yield return Wait(config.InitializeAsync(CancellationToken.None));
            var resources=new TribulationTrackingResources(new YooAssetResourceService(initializer,config));
            var definition=CombatRunDefinition.Create(config.Tables,config.Tables.TbDraftPolicy.DataList.First().StageId);
            var goal=definition.DraftPolicy.GoalId_Ref;var profile=new BuildFeedbackProfileStore(isolated,definition.DraftPolicy.UiId_Ref);
            int baseline=World.All.Count;var root=new GameObject("P0 isolated feedback entry");tribulationTestRoot=root;var entry=root.AddComponent<TribulationEntryController>();
            try
            {
                yield return Wait(entry.InitializeAsync(config.Tables,resources,CancellationToken.None,profile));
                var lobby=entry.LobbyRoot.GetComponent<TribulationLobbyView>();Assert.That(lobby.GoalDescriptions[0].text,Does.Contain(goal.Name));
                Assert.That(lobby.ContentToggles[0].interactable,Is.False);
                yield return ClickFeedbackButtonThroughRaycast(lobby.RoleButtons[0]);
                double deadline=Time.realtimeSinceStartupAsDouble+30;
                while((entry.ActiveRunner==null||!entry.ActiveRunner.Ready)&&Time.realtimeSinceStartupAsDouble<deadline)yield return null;
                var runner=entry.ActiveRunner;Assert.That(runner!=null&&runner.Ready,Is.True);runner.enabled=false;
                var model=runner.RunModel;var view=runner.BuildFeedbackUiRoot.GetComponent<BuildFeedbackPanelView>();
                model.GrantExperience(definition.ExperienceLevels[0].RequiredExperience);runner.AdvanceFrame(0,default);
                int rerolls=model.RerollsRemaining;ulong old=model.UpgradePanelGeneration;
                yield return ClickFeedbackButtonThroughRaycast(view.RerollButton);runner.AdvanceFrame(0,default);
                Assert.That(model.RerollsRemaining,Is.EqualTo(rerolls-1));Assert.That(runner.Coordinator.RerollUpgrade(old),Is.False);
                Assert.That(view.RerollText.text,Does.Contain(model.RerollsRemaining.ToString()));
                yield return CaptureGameScreen("p0-draft-controls.png");
                yield return ClickFeedbackButtonThroughRaycast(view.SkipButton);runner.AdvanceFrame(0,default);
                Assert.That(model.State,Is.EqualTo(CombatRunState.Playing));Assert.That(model.UpgradeRanks,Is.Empty);
                var world=FindCombatWorld();float scale=definition.CombatRules.WorldUnitsPerPixel;
                for(int i=0;i<goal.MinimumTargets;i++)
                {
                    Assert.That(runner.Session.TrySpawnMonster(definition.Monsters.First(),definition.Map.SpawnRadiusPixels,(ulong)i+1,false,out int slot),Is.True);
                    var unit=runner.Session.ReadUnit(slot);double angle=Math.PI*2*i/goal.MinimumTargets;
                    unit.Position=unit.PreviousPosition=new float2((float)Math.Cos(angle),(float)Math.Sin(angle))*definition.Tribulation.SignatureId_Ref.CounterRadiusPixels*scale*.5f;
                    unit.Target.Health=unit.Target.MaxHealth=10000;unit.Target.Evasion=0;unit.MoveSpeed=0;unit.Cooldown=1000;
                    world.EntityManager.SetComponentData(runner.Session.UnitEntity(slot),unit);
                }
                var player=runner.Session.ReadUnit(0);
                for(int i=0;i<player.Target.Guard.ChargeHits;i++)player.Target.Guard.RecordEffectiveHit(runner.Session.Statistics.Tick*runner.Session.StepSeconds,true);
                world.EntityManager.SetComponentData(runner.Session.UnitEntity(0),player);
                runner.AdvanceFrame(runner.Session.StepSeconds,default);
                Assert.That(runner.Coordinator.BuildFeedback.GoalEvents,Is.EqualTo(goal.RequiredEvents));
                int oneTick=(int)Math.Ceiling(1000d/definition.CombatRules.SimulationHz);
                model.Advance((int)(definition.Rule.BossTimeMilli-model.ElapsedMilli-oneTick));
                runner.AdvanceFrame(runner.Session.StepSeconds*2,default);
                int bossSlot=Enumerable.Range(1,runner.Session.UnitCount-1).Single(s=>runner.Session.ReadUnit(s).IsBoss);
                var boss=runner.Session.ReadUnit(bossSlot);boss.Position=boss.PreviousPosition=runner.Session.ReadUnit(0).Position+new float2(.1f,0);
                boss.MoveSpeed=0;boss.Target.Health=1;boss.Target.Evasion=0;boss.Target.Defense=0;boss.Target.InvulnerableUntil=0;
                world.EntityManager.SetComponentData(runner.Session.UnitEntity(bossSlot),boss);
                for(int i=0;i<definition.CombatRules.SimulationHz*5&&!model.Result.HasValue;i++)runner.AdvanceFrame(runner.Session.StepSeconds,default);
                Assert.That(model.Result,Is.EqualTo(cfg.ERunResultType.Victory));
                Assert.That(profile.UnlockedIds,Has.Member(goal.Id));Assert.That(profile.EnabledIds,Is.Empty);
                Assert.That(view.SaveText.text,Is.EqualTo(definition.DraftPolicy.UiId_Ref.SavedText));
                yield return CaptureGameScreen("p0-build-summary.png");
                entry.ReturnToLobby();yield return null;
                Assert.That(lobby.ContentToggles[0].interactable,Is.True);Assert.That(lobby.ContentToggles[0].isOn,Is.False);
                lobby.ContentToggles[0].isOn=true;
                Assert.That(new BuildFeedbackProfileStore(isolated,definition.DraftPolicy.UiId_Ref).EnabledIds,Has.Member(goal.Id));
                Assert.That(model.ExplainUpgradeExclusion(config.Tables.TbMechanismCard.Get(goal.ModifierId).OptionId_Ref),Is.EqualTo(DraftExclusion.Locked),"An existing run keeps its frozen content snapshot.");
                yield return Wait(entry.ShutdownAsync());yield return null;
                Assert.That(resources.Outstanding,Is.Zero);Assert.That(World.All.Count,Is.EqualTo(baseline));
            }
            finally
            {
                entry.Close();
                string permitted=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"roguelike-feedback-profile-tests"))+Path.DirectorySeparatorChar;
                if(Path.GetFullPath(isolated).StartsWith(permitted,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(isolated))Directory.Delete(isolated,true);
            }
        }

        /// <summary>真实EventSystem射线按目标Canvas模式使用对应相机，同时支持选角Overlay和本局Camera画布。</summary>
        /// <param name="button">期望成为最高命中的正式按钮。</param><returns>布局完成后派发一次正式点击。</returns>
        private static IEnumerator ClickFeedbackButtonThroughRaycast(Button button)
        {
            yield return null;yield return new WaitForEndOfFrame();Canvas.ForceUpdateCanvases();
            var canvas=button.GetComponentInParent<Canvas>();var rect=(RectTransform)button.transform;
            var camera=canvas.renderMode==RenderMode.ScreenSpaceOverlay?null:canvas.worldCamera;
            var pointer=new PointerEventData(EventSystem.current) { position=RectTransformUtility.WorldToScreenPoint(camera,rect.TransformPoint(rect.rect.center)),button=PointerEventData.InputButton.Left };
            var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(pointer,hits);
            Assert.That(hits.Count,Is.GreaterThan(0),"The requested real button must receive a raycast: "+button.name);
            Assert.That(hits[0].gameObject.transform==button.transform||hits[0].gameObject.transform.IsChildOf(button.transform),Is.True,
                "Requested "+button.name+" but the topmost raycast hit was "+hits[0].gameObject.name);
            Assert.That(ExecuteEvents.ExecuteHierarchy(hits[0].gameObject,pointer,ExecuteEvents.pointerClickHandler),Is.Not.Null);
        }
    }
}
