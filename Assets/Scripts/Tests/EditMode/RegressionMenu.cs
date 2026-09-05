using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>编辑器测试入口；普通回归由 NUnit 排除 Explicit 压力项，保存 XML 与耗时榜。</summary>
    [InitializeOnLoad]
    public sealed class RegressionMenu : ICallbacks
    {
        private const string StageKey = "Roguelike.Regression.Stage";
        private const string DirectoryKey = "Roguelike.Regression.Directory";
        private static readonly TestRunnerApi Api;

        /// <summary>Unity 加载编辑器程序集时注册回调；域重载后通过 SessionState 恢复本轮阶段。</summary>
        /// <remarks>只创建内存 API 对象，不保存 Unity 资产，不启动测试。</remarks>
        static RegressionMenu()
        {
            Api = ScriptableObject.CreateInstance<TestRunnerApi>();
            Api.hideFlags = HideFlags.HideAndDontSave;
            Api.RegisterCallbacks(new RegressionMenu());
        }

        /// <summary>每轮代码修改后运行普通 EditMode 回归；不包含 Explicit 压力测试。</summary>
        [MenuItem("Roguelike/Tests/快速回归 EditMode")]
        public static void Quick() => Start(false, null);

        /// <summary>阶段收口时先跑 EditMode，通过后自动接 PlayMode；不构建 Player。</summary>
        [MenuItem("Roguelike/Tests/阶段回归 EditMode + PlayMode")]
        public static void Acceptance() => Start(true, null);

        /// <summary>碰撞与模拟改动后只运行战斗核心相关夹具。</summary>
        [MenuItem("Roguelike/Tests/专项/战斗与碰撞")]
        public static void Combat() => Start(false, new[] { "^Roguelike.Tests.Combat(Session|Cylinder|Foundation)Tests\\." });

        /// <summary>动画或表现改动后运行资源、动画、渲染和图形相关夹具。</summary>
        [MenuItem("Roguelike/Tests/专项/动画与表现")]
        public static void Visual() => Start(false, new[] { "^Roguelike.Tests.(AniResource|CombatAnimation|CombatVisual|CombatVisualCapture)Tests\\." });

        /// <summary>菜单触发时建立独立报告目录并安排测试，不覆盖已有报告。</summary>
        /// <param name="withPlayMode">是否在 EditMode 通过后接续 PlayMode。</param>
        /// <param name="groups">夹具正则筛选；空值运行普通 EditMode 全集。</param>
        /// <remarks>写入 SessionState 与 outputs 下的报告目录；不保存当前场景。</remarks>
        /// <exception cref="InvalidOperationException">编辑器正在运行、编译或本入口已有任务。</exception>
        private static void Start(bool withPlayMode, string[] groups)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                SessionState.GetInt(StageKey, 0) != 0)
                throw new InvalidOperationException("请等待当前测试或编译结束，并退出 Play Mode。");
            string directory = Path.GetFullPath("outputs/regression/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(directory);
            SessionState.SetString(DirectoryKey, directory);
            SessionState.SetInt(StageKey, withPlayMode ? 1 : 2);
            try { Api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, groupNames = groups })); }
            catch { SessionState.SetInt(StageKey, 0); throw; }
        }

        /// <summary>测试框架开始执行时记录状态，便于区分等待和已完成。</summary>
        /// <param name="testsToRun">框架发现的测试树；不将发现数量误当作通过数量。</param>
        public void RunStarted(ITestAdaptor testsToRun)
        {
            if (SessionState.GetInt(StageKey, 0) != 0) Debug.Log("REGRESSION_RUNNING");
        }

        /// <summary>每个测试开始时由框架调用；细节由最终 XML 保存。</summary>
        /// <param name="test">当前测试节点。</param>
        public void TestStarted(ITestAdaptor test) { }

        /// <summary>每个测试结束时由框架调用；不因单项通过重复运行其他测试。</summary>
        /// <param name="result">当前测试结果。</param>
        public void TestFinished(ITestResultAdaptor result) { }

        /// <summary>整批完成后保存原始结果与耗时榜；只有 EditMode 成功才接续 PlayMode。</summary>
        /// <param name="result">真实执行结果，包含跳过与失败项。</param>
        /// <remarks>报告写入 outputs；接续通过 delayCall 让框架先完成当前任务清理。</remarks>
        public void RunFinished(ITestResultAdaptor result)
        {
            int stage = SessionState.GetInt(StageKey, 0);
            if (stage == 0) return;
            string mode = stage == 3 ? "playmode" : "editmode";
            string directory = SessionState.GetString(DirectoryKey, "");
            try
            {
                File.WriteAllText(Path.Combine(directory, mode + ".xml"), result.ToXml().OuterXml, new UTF8Encoding(false));
                var text = new StringBuilder($"Result: {result.ResultState}\nPassed: {result.PassCount}\nFailed: {result.FailCount}\nSkipped: {result.SkipCount}\nDuration: {result.Duration:F3}s\n\n");
                foreach (var leaf in Leaves(result).OrderByDescending(item => item.Duration))
                    text.AppendLine($"{leaf.Duration:F3}s | {leaf.ResultState} | {leaf.FullName}");
                File.WriteAllText(Path.Combine(directory, mode + "-timings.txt"), text.ToString(), new UTF8Encoding(false));
                Debug.Log($"REGRESSION_FINISHED {mode}: {result.ResultState}, {result.Duration:F3}s; {directory}");
            }
            finally { SessionState.SetInt(StageKey, 0); }
            if (stage == 1 && result.ResultState == "Passed" && result.PassCount > 0)
            {
                SessionState.SetInt(StageKey, 3);
                EditorApplication.delayCall += ContinuePlayMode;
            }
        }

        /// <summary>EditMode 清理完成后启动 PlayMode；启动失败则解除本入口占用并保留异常。</summary>
        /// <remarks>框架控制临时测试场景和域重载，不构建 Win64。</remarks>
        private static void ContinuePlayMode()
        {
            try { Api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.PlayMode })); }
            catch { SessionState.SetInt(StageKey, 0); throw; }
        }

        /// <summary>写耗时报告时递归枚举叶子测试，避免套件与子测试重复累计。</summary>
        /// <param name="result">待遍历结果节点。</param>
        /// <returns>原始叶子结果，包括跳过、失败与通过。</returns>
        private static IEnumerable<ITestResultAdaptor> Leaves(ITestResultAdaptor result)
        {
            if (!result.HasChildren) { yield return result; yield break; }
            foreach (var child in result.Children)
                foreach (var leaf in Leaves(child)) yield return leaf;
        }
    }
}
