using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using ProcessWindowStyle = System.Diagnostics.ProcessWindowStyle;

namespace Roguelike.Tests
{
    /// <summary>编辑器测试入口；普通回归由 NUnit 排除 Explicit 压力项，保存 XML 与耗时榜。</summary>
    [InitializeOnLoad]
    public sealed class RegressionMenu : ICallbacks
    {
        private const string StageKey = "Roguelike.Regression.Stage";
        private const string DirectoryKey = "Roguelike.Regression.Directory";
        private const string HeartbeatFileName = "heartbeat.txt";
        private const string CurrentTestFileName = "current-test.txt";
        private const string CompletedFileName = "completed.txt";
        private const int InactivityTimeoutSeconds = 90;
        private const int AbsoluteTimeoutSeconds = 300;
        private static readonly TestRunnerApi Api;

        /// <summary>Unity 加载编辑器程序集时注册回调；域重载后通过 SessionState 恢复本轮阶段。</summary>
        /// <remarks>只创建内存 API 对象，不保存 Unity 资产，不启动测试。</remarks>
        static RegressionMenu()
        {
            Api = ScriptableObject.CreateInstance<TestRunnerApi>();
            Api.hideFlags = HideFlags.HideAndDontSave;
            Api.RegisterCallbacks(new RegressionMenu());
            if (SessionState.GetInt(StageKey, 0) != 0) WriteProgress("domain-reload", null);
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
            if (HasDirtyScenes())
                throw new InvalidOperationException("自动超时可能关闭 Unity，请先保存或放弃当前 Scene/Prefab 的未保存修改。");
            string directory = Path.GetFullPath("outputs/regression/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(directory);
            SessionState.SetString(DirectoryKey, directory);
            SessionState.SetInt(StageKey, withPlayMode ? 1 : 2);
            try
            {
                WriteProgress("scheduled-editmode", null);
                StartWatchdog(directory);
                Api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, groupNames = groups }));
            }
            catch
            {
                SessionState.SetInt(StageKey, 0);
                CompleteWatchdog(directory, "start-failed");
                throw;
            }
        }

        /// <summary>测试框架开始执行时记录状态，便于区分等待和已完成。</summary>
        /// <param name="testsToRun">框架发现的测试树；不将发现数量误当作通过数量。</param>
        public void RunStarted(ITestAdaptor testsToRun)
        {
            if (SessionState.GetInt(StageKey, 0) == 0) return;
            WriteProgress("run-started", testsToRun.FullName);
            Debug.Log("REGRESSION_RUNNING");
        }

        /// <summary>每个测试开始时由框架调用；细节由最终 XML 保存。</summary>
        /// <param name="test">当前测试节点。</param>
        public void TestStarted(ITestAdaptor test)
        {
            if (SessionState.GetInt(StageKey, 0) != 0) WriteProgress("test-started", test.FullName);
        }

        /// <summary>每个测试结束时由框架调用；不因单项通过重复运行其他测试。</summary>
        /// <param name="result">当前测试结果。</param>
        public void TestFinished(ITestResultAdaptor result)
        {
            if (SessionState.GetInt(StageKey, 0) != 0)
                WriteProgress("test-finished:" + result.ResultState, result.FullName);
        }

        /// <summary>整批完成后保存原始结果与耗时榜；只有 EditMode 成功才接续 PlayMode。</summary>
        /// <param name="result">真实执行结果，包含跳过与失败项。</param>
        /// <remarks>报告写入 outputs；接续通过 delayCall 让框架先完成当前任务清理。</remarks>
        public void RunFinished(ITestResultAdaptor result)
        {
            int stage = SessionState.GetInt(StageKey, 0);
            if (stage == 0) return;
            string mode = stage == 3 ? "playmode" : "editmode";
            string directory = SessionState.GetString(DirectoryKey, "");
            bool continuePlayMode = stage == 1 && result.ResultState == "Passed" && result.PassCount > 0;
            try
            {
                File.WriteAllText(Path.Combine(directory, mode + ".xml"), result.ToXml().OuterXml, new UTF8Encoding(false));
                var text = new StringBuilder($"Result: {result.ResultState}\nPassed: {result.PassCount}\nFailed: {result.FailCount}\nSkipped: {result.SkipCount}\nDuration: {result.Duration:F3}s\n\n");
                foreach (var leaf in Leaves(result).OrderByDescending(item => item.Duration))
                    text.AppendLine($"{leaf.Duration:F3}s | {leaf.ResultState} | {leaf.FullName}");
                File.WriteAllText(Path.Combine(directory, mode + "-timings.txt"), text.ToString(), new UTF8Encoding(false));
                WriteProgress("run-finished:" + mode, result.FullName);
                Debug.Log($"REGRESSION_FINISHED {mode}: {result.ResultState}, {result.Duration:F3}s; {directory}");
            }
            catch (Exception exception)
            {
                continuePlayMode = false;
                Debug.LogException(exception);
            }
            finally
            {
                SessionState.SetInt(StageKey, continuePlayMode ? 3 : 0);
                if (!continuePlayMode) CompleteWatchdog(directory, "finished:" + mode);
            }
            if (continuePlayMode)
            {
                EditorApplication.delayCall += ContinuePlayMode;
            }
        }

        /// <summary>EditMode 清理完成后启动 PlayMode；启动失败则解除本入口占用并保留异常。</summary>
        /// <remarks>框架控制临时测试场景和域重载，不构建 Win64。</remarks>
        private static void ContinuePlayMode()
        {
            string directory = SessionState.GetString(DirectoryKey, "");
            try
            {
                WriteProgress("scheduled-playmode", null);
                Api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.PlayMode }));
            }
            catch
            {
                SessionState.SetInt(StageKey, 0);
                CompleteWatchdog(directory, "playmode-start-failed");
                throw;
            }
        }

        /// <summary>为本轮回归启动独立 PowerShell 守护，确保 Unity 主线程卡死时仍能留下诊断并结束进程。</summary>
        /// <param name="directory">本轮唯一报告目录，也是守护读取心跳和写入超时记录的位置。</param>
        /// <remarks>启动隐藏子进程；正常完成时由 completed.txt 通知退出，超时才会强制结束当前 Unity Editor。</remarks>
        /// <exception cref="FileNotFoundException">项目缺少受版本控制的守护脚本。</exception>
        /// <exception cref="InvalidOperationException">PowerShell 守护进程无法启动。</exception>
        private static void StartWatchdog(string directory)
        {
            string script = Path.GetFullPath("Tools/Tests/watch-unity-regression.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("缺少 Unity 回归守护脚本。", script);
            using (var unity = Process.GetCurrentProcess())
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "pwsh.exe",
                    Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {Quote(script)} " +
                                $"-UnityProcessId {unity.Id} -UnityProcessStartUtcTicks {unity.StartTime.ToUniversalTime().Ticks} " +
                                $"-ReportDirectory {Quote(directory)} -InactivityTimeoutSeconds {InactivityTimeoutSeconds} " +
                                $"-AbsoluteTimeoutSeconds {AbsoluteTimeoutSeconds}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process watchdog = Process.Start(startInfo))
                {
                    if (watchdog == null) throw new InvalidOperationException("无法启动 Unity 回归守护进程。");
                }
            }
        }

        /// <summary>更新跨域重载可见的回归心跳，并记录当前测试，供外部守护判断是否仍有进展。</summary>
        /// <param name="phase">当前回归阶段或回调事件。</param>
        /// <param name="testName">当前测试全名；没有具体测试时为空。</param>
        /// <remarks>仅写 outputs 下本轮报告目录，不写 Unity 资产或项目设置。</remarks>
        private static void WriteProgress(string phase, string testName)
        {
            string directory = SessionState.GetString(DirectoryKey, "");
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            string text = $"Utc: {DateTime.UtcNow:O}\nPhase: {phase}\nTest: {testName ?? string.Empty}\n";
            File.WriteAllText(Path.Combine(directory, CurrentTestFileName), text, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, HeartbeatFileName), DateTime.UtcNow.ToString("O"), new UTF8Encoding(false));
        }

        /// <summary>写入正常终止标记，让独立守护停止监控当前 Unity 进程。</summary>
        /// <param name="directory">本轮回归报告目录。</param>
        /// <param name="reason">完成、失败或启动异常等终止原因。</param>
        /// <remarks>标记只存在 outputs 报告目录；不会终止任何进程。</remarks>
        private static void CompleteWatchdog(string directory, string reason)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            File.WriteAllText(Path.Combine(directory, CompletedFileName),
                $"Utc: {DateTime.UtcNow:O}\nReason: {reason}\n", new UTF8Encoding(false));
        }

        /// <summary>将受控本地路径包装为 PowerShell 进程参数，保留其中的空格。</summary>
        /// <param name="value">不包含双引号的项目内绝对路径。</param>
        /// <returns>可直接拼入 ProcessStartInfo.Arguments 的带引号参数。</returns>
        private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        /// <summary>检查当前编辑器是否存在尚未写盘的 Scene 或 Prefab Stage，避免超时结束进程时丢失编辑内容。</summary>
        /// <returns>任一已加载场景或当前 Prefab Stage 为脏状态时返回 true。</returns>
        private static bool HasDirtyScenes()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                if (SceneManager.GetSceneAt(index).isDirty) return true;
            }
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            return prefabStage != null && prefabStage.scene.isDirty;
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
