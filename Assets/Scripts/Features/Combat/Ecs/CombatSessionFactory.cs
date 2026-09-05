using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using ProjectX.Migration;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat.Run;
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
            var options = await LoadAttackOptionsAsync(scenario.MonsterIds_Ref, scenario.PresentationId_Ref,
                resources, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new CombatSession(world, scenario, options);
        }

        /// <summary>为正式关卡预加载全部普通怪和 Boss 的攻击 ANI 时序，再创建动态生成会话。</summary>
        /// <param name="world">调用者持有的 Unity 主线程世界。</param>
        /// <param name="definition">由 TbStage 聚合并验证的正式单局定义。</param>
        /// <param name="resources">已初始化的资源服务。</param>
        /// <param name="cancellationToken">退出入局流程时取消加载。</param>
        /// <returns>初始只包含玩家、由运行器消费生成请求的会话。</returns>
        /// <remarks>所有怪物 ANI 只加载一次；临时句柄在创建会话前释放。</remarks>
        /// <exception cref="ArgumentNullException">定义或资源服务为空。</exception>
        /// <exception cref="OperationCanceledException">加载被取消。</exception>
        /// <exception cref="InvalidOperationException">怪物表现或攻击时序不完整。</exception>
        public static async Task<CombatSession> CreateAsync(World world, CombatRunDefinition definition,
            IResourceService resources, CancellationToken cancellationToken)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (resources == null) throw new ArgumentNullException(nameof(resources));
            var monsters = new List<MonsterConfig>(definition.Monsters);
            if (monsters.Find(item => item.Id == definition.Boss.MonsterId_Ref.Id) == null)
                monsters.Add(definition.Boss.MonsterId_Ref);
            var options = await LoadAttackOptionsAsync(monsters, definition.Presentation, resources, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new CombatSession(world, definition, options);
        }

        /// <summary>按表现集合去重加载怪物待机、移动和攻击 ANI，并验证方向映射。</summary>
        /// <param name="monsters">测试场景或正式关卡允许的怪物目录。</param>
        /// <param name="presentation">TbCombatPresentation 动画时钟和方向配置。</param>
        /// <param name="resources">统一资源服务。</param>
        /// <param name="cancellationToken">退出加载令牌。</param>
        /// <returns>以 TbVisualSet.id 为键的非托管攻击选项数组。</returns>
        /// <remarks>逐个释放临时原始资源句柄；返回数据不持有 YooAsset 句柄。</remarks>
        private static async Task<Dictionary<int, CombatAttackOption[]>> LoadAttackOptionsAsync(
            IEnumerable<MonsterConfig> monsters, CombatPresentationConfig presentation,
            IResourceService resources, CancellationToken cancellationToken)
        {
            var options = new Dictionary<int, CombatAttackOption[]>();
            foreach (var monster in monsters)
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
                _ = new CombatAttackDirections(presentation, new CombatAnimation(attackData, presentation, false),
                    new CombatAnimation(CocosAniData.Parse(idle.Data), presentation, visual.StandClipId_Ref.Loop),
                    new CombatAnimation(CocosAniData.Parse(move.Data), presentation, visual.MoveClipId_Ref.Loop));
                options.Add(visual.Id, CombatAttackOption.Build(visual, presentation, attackData));
            }
            return options;
        }
    }
}
