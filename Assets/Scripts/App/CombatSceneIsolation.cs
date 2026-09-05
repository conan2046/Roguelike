using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Roguelike.App
{
    /// <summary>显式战斗启动时在场景 Start 前停用原场景根节点；只改运行态，退出恢复。</summary>
    internal sealed class CombatSceneIsolation : IDisposable
    {
        private readonly GameObject owner;
        private readonly List<GameObject> suspended = new List<GameObject>();

        /// <summary>组合根安装时订阅后续场景，并隔离已经加载的普通场景。</summary>
        /// <param name="owner">不可停用的应用根节点。</param>
        /// <remarks>不处理 DontDestroyOnLoad 中第三方系统，不卸载或保存场景，不修改原先已停用的节点。</remarks>
        internal CombatSceneIsolation(GameObject owner)
        {
            this.owner = owner;
            SceneManager.sceneLoaded += OnSceneLoaded;
            for (int i = 0; i < SceneManager.sceneCount; i++) Suspend(SceneManager.GetSceneAt(i));
        }

        /// <summary>Unity 场景加载完成且 Start 尚未执行时隔离旧内容。</summary>
        /// <param name="scene">新加载场景。</param>
        /// <param name="mode">Unity 加载模式。</param>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) { Suspend(scene); }

        /// <summary>记录并停用普通场景中的活动根节点，避免旧相机/UI/示例脚本与战斗同时运行。</summary>
        /// <param name="scene">已加载运行时场景。</param>
        /// <remarks>只影响本次显式战斗启动期间的活动状态；Dispose 恢复仍然存在的节点。</remarks>
        private void Suspend(Scene scene)
        {
            if (!scene.isLoaded) return;
            foreach (var root in scene.GetRootGameObjects())
                if (root != owner && root.activeSelf) { suspended.Add(root); root.SetActive(false); }
        }

        /// <summary>退出战斗或初始化失败时取消订阅并恢复本对象停用的节点。</summary>
        /// <remarks>对象可能随场景卸载而失效，因此先检查 Unity 引用；允许重复调用。</remarks>
        public void Dispose()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            foreach (var root in suspended) if (root != null) root.SetActive(true);
            suspended.Clear();
        }
    }
}
