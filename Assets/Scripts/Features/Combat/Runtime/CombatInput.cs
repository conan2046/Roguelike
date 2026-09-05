using System;
using cfg;
using Unity.Mathematics;
using UnityEngine;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>单渲染帧设备输入快照，与战斗状态和键位配置解耦。</summary>
    public struct CombatInputFrame
    {
        public float2 Movement;
        public bool PausePressed, RestartPressed;
    }
    /// <summary>运行时设备输入契约，允许测试替身走相同控制流程。</summary>
    public interface ICombatInput
    {
        /// <summary>渲染帧开始时采样一次输入，按钮只代表本帧按下事件。</summary>
        /// <returns>移动和控制事件。</returns>
        CombatInputFrame Read();
    }
    /// <summary>当前项目旧 InputManager 适配器；键位及轴名称只来自表现表。</summary>
    public sealed class UnityCombatInput : ICombatInput
    {
        private readonly string horizontal, vertical;
        private readonly KeyCode pause, restart;

        /// <summary>配置就绪后解析 TbCombatPresentation 的轴名和控制键，非法值拒绝启动。</summary>
        /// <param name="config">已加载表现方案。</param>
        /// <exception cref="InvalidOperationException">轴名空、键名非法或控制键重复。</exception>
        public UnityCombatInput(CombatPresentationConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.HorizontalAxis) || string.IsNullOrWhiteSpace(config.VerticalAxis) ||
                !Enum.TryParse(config.PauseKey, out pause) || !Enum.IsDefined(typeof(KeyCode), pause) || pause == KeyCode.None ||
                !Enum.TryParse(config.RestartKey, out restart) || !Enum.IsDefined(typeof(KeyCode), restart) || restart == KeyCode.None || pause == restart)
                throw new InvalidOperationException("TbCombatPresentation: invalid axes or control keys.");
            horizontal = config.HorizontalAxis; vertical = config.VerticalAxis;
        }

        /// <summary>Unity Update 中采样配置输入；不在这里缩放速度、推进冷却或修改 timeScale。</summary>
        /// <returns>交给会话的原始移动与控制事件。</returns>
        public CombatInputFrame Read() => new CombatInputFrame {
            Movement = new float2(Input.GetAxisRaw(horizontal), Input.GetAxisRaw(vertical)),
            PausePressed = Input.GetKeyDown(pause), RestartPressed = Input.GetKeyDown(restart) };
    }
}
