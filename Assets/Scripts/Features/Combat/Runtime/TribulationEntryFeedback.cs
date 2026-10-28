using System;
using System.Linq;
using Roguelike.Features.Combat.Run;

namespace Roguelike.Features.Combat.Runtime
{
    public sealed partial class TribulationEntryController
    {
        private BuildFeedbackProfileStore profileStoreForFeedback;

        /// <summary>正式选角绑定目标和解锁开关；单局启动前才允许修改，测试显式注入隔离存储。</summary>
        /// <param name="profile">可选Profile；空值不进行磁盘访问。</param>
        /// <exception cref="InvalidOperationException">正式Prefab缺少本功能绑定。</exception>
        private void InitializeFeedbackLobby(BuildFeedbackProfileStore profile)
        {
            profileStoreForFeedback = profile;
            if (lobby.ContentToggles == null || lobby.ContentToggles.Length != entry.StageIds.Count ||
                lobby.GoalDescriptions == null || lobby.GoalDescriptions.Length != entry.StageIds.Count)
                throw new InvalidOperationException("Feedback lobby bindings are missing.");
            for (int i = 0; i < entry.StageIds.Count; i++)
            { int index = i; lobby.ContentToggles[i].onValueChanged.AddListener(value => ChangeEnabledContent(index, value)); }
            RefreshFeedbackLobby();
        }

        /// <summary>加载、返回或保存开关后读取Profile快照刷新目标与可启用状态，不修改基础卡池。</summary>
        private void RefreshFeedbackLobby()
        {
            for (int i = 0; i < entry.StageIds.Count; i++)
            {
                var policy = tables.TbDraftPolicy.DataList.Single(p => p.StageId == entry.StageIds[i]);
                var goal = policy.GoalId_Ref; var ui = policy.UiId_Ref;
                bool unlocked = profileStoreForFeedback != null && profileStoreForFeedback.UnlockedIds.Contains(goal.Id);
                lobby.GoalDescriptions[i].text = string.Format(System.Globalization.CultureInfo.InvariantCulture, ui.StartGoalFormat, goal.Name, goal.Description);
                lobby.ContentLabels[i].text = unlocked ? ui.EnabledText : profileStoreForFeedback == null ? ui.DemoText : ui.LockedText;
                lobby.ContentToggles[i].SetIsOnWithoutNotify(unlocked && profileStoreForFeedback.EnabledIds.Contains(goal.Id));
                lobby.ContentToggles[i].interactable = unlocked && !busy && ActiveRunner == null;
            }
        }

        /// <summary>开局前原子保存已解锁修饰的启用开关；失败恢复原状态，不提前更新候选池。</summary>
        /// <param name="index">入口角色索引。</param><param name="value">期望启用状态。</param>
        private void ChangeEnabledContent(int index, bool value)
        {
            if (profileStoreForFeedback == null || busy || ActiveRunner != null) { RefreshFeedbackLobby(); return; }
            var policy = tables.TbDraftPolicy.DataList.Single(p => p.StageId == entry.StageIds[index]);
            try { profileStoreForFeedback.SetEnabled(policy.GoalId, value); lobby.ErrorText.text = string.Empty; }
            catch (Exception exception) when (exception is System.IO.IOException || exception is UnauthorizedAccessException || exception is System.Runtime.Serialization.SerializationException)
            { lobby.ErrorText.text = policy.UiId_Ref.PendingSaveText; }
            RefreshFeedbackLobby();
        }
    }
}
