using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using ProjectX.Migration;
using Roguelike.Core.Resources;
using Unity.Entities;

namespace Roguelike.Features.Combat.Ecs
{
    /// <summary>连接统一资源入口与战斗会话；动画时序只能从配置引用的实际 ANI 推导。</summary>
    public static class CombatSessionFactory
    {
        /// <summary>配置就绪后按 TbVisualSet 的片段资源 ID 加载 ANI，验证方向与命中帧，再创建会话。</summary>
        /// <param name="world">调用者持有的 Unity 主线程世界。</param>
        /// <param name="scenario">统一配置入口的 TbPerformanceScenario 行。</param>
        /// <param name="resources">已初始化的资源服务。</param>
        /// <param name="cancellationToken">退出入局流程时取消加载。</param>
        /// <returns>调用者负责释放的会话。</returns>
        /// <remarks>必须由 Unity 主线程调用并保留同步上下文；临时资源句柄无论成功或失败均释放，取消时不创建实体。</remarks>
        /// <exception cref="ArgumentException">场景缺少表现或怪物引用。</exception>
        /// <exception cref="ArgumentNullException">资源服务为空。</exception>
        /// <exception cref="OperationCanceledException">加载被取消。</exception>
        /// <exception cref="InvalidOperationException">配置不完整或资源不合法。</exception>
        public static async Task<CombatSession> CreateAsync(World world, PerformanceScenarioConfig scenario,
            IResourceService resources, CancellationToken cancellationToken)
        {
            if (resources == null) throw new ArgumentNullException(nameof(resources));
            if (scenario?.PresentationId_Ref == null || scenario.MonsterIds_Ref == null)
                throw new ArgumentException("Missing combat presentation or monsters.", nameof(scenario));
            cancellationToken.ThrowIfCancellationRequested();
            var options = new Dictionary<int, CombatAttackOption[]>();
            foreach (var monster in scenario.MonsterIds_Ref)
            {
                var visual = monster.VisualSetId_Ref;
                if (visual == null || visual.AttackClipId_Ref == null || visual.StandClipId_Ref == null || visual.MoveClipId_Ref == null)
                    throw new InvalidOperationException("Monster requires explicit idle, move and attack clips.");
                if (options.ContainsKey(visual.Id)) continue;
                using var attack = await resources.LoadRawFileAsync(visual.AttackClipId_Ref.AniResourceId, cancellationToken);
                using var idle = await resources.LoadRawFileAsync(visual.StandClipId_Ref.AniResourceId, cancellationToken);
                using var move = await resources.LoadRawFileAsync(visual.MoveClipId_Ref.AniResourceId, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var attackData = CocosAniData.Parse(attack.Data);
                var presentation = scenario.PresentationId_Ref;
                _ = new CombatAttackDirections(presentation, new CombatAnimation(attackData, presentation, false),
                    new CombatAnimation(CocosAniData.Parse(idle.Data), presentation, visual.StandClipId_Ref.Loop),
                    new CombatAnimation(CocosAniData.Parse(move.Data), presentation, visual.MoveClipId_Ref.Loop));
                options.Add(visual.Id, CombatAttackOption.Build(visual, presentation, attackData));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new CombatSession(world, scenario, options);
        }
    }
}
