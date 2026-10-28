namespace Roguelike.Features.Combat.Runtime
{
    public sealed partial class CombatRuntimeRunner
    {
        /// <summary>结算重开或返回选角前确保显式Profile已确认保存；失败保留当前结算供用户重试。</summary>
        /// <returns>保存成功、没有结算或明确非持久演示时为真。</returns>
        public bool TryFinalizeProfile() => buildFeedbackUi == null || buildFeedbackUi.TrySave();
        public UnityEngine.GameObject BuildFeedbackUiRoot => buildFeedbackUi?.Root;
    }
}
