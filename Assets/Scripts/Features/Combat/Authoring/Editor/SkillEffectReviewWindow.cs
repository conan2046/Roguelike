using System;
using System.IO;
using System.Linq;
using cfg;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>独立技能效果审核场景与四格编辑器；仅编辑器运行，不参与战斗启动与 Player 构建。</summary>
    public sealed class SkillEffectReviewWindow : EditorWindow
    {
        public const string ScenePath = "Assets/Scenes/SkillEffectReview.unity";
        private const int PageSize = 4;
        private static readonly string[] FilterNames = { "全部技能", "待确认", "使用", "不用" };
        [SerializeField] private int page;
        [SerializeField] private int decisionFilter;
        [SerializeField] private string search = "";
        [SerializeField] private float speed = 1f;
        [SerializeField] private float zoom = 1f;
        [SerializeField] private bool paused;
        [SerializeField] private int[] selectedClips = new int[PageSize];
        [SerializeField] private int[] playbackModes = new int[PageSize];
        private static readonly string[] PlaybackModeNames = { "组合播放", "分段查看" };
        private SkillEffectReviewCatalog catalog;
        private SkillConfig[] filtered = Array.Empty<SkillConfig>();
        private CombatPresentationConfig presentation;
        private readonly SkillEffectReviewSequence[] players = new SkillEffectReviewSequence[PageSize];
        private readonly double[] playbackStarts = new double[PageSize];
        private readonly SkillConfig[] current = new SkillConfig[PageSize];
        private readonly int[] actions = new int[PageSize];
        private readonly bool[] resourceDetails = new bool[PageSize];
        private string error;
        private bool dirty;
        private double lastEdit;
        private double lastTick;
        private double lastRepaint;
        private double playTime;
        private int PageCount => Math.Max(1, (filtered.Length + PageSize - 1) / PageSize);

        /// <summary>用户从菜单打开时创建或载入专用技能场景；已有脏场景不被自动保存或关闭。</summary>
        /// <remarks>只创建用户要求的场景与一个正式相机，不序列化临时效果资源，不加入 Build Settings。</remarks>
        [MenuItem("Roguelike/技能效果审核/打开审核场景")]
        public static void OpenReviewScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("技能效果审核在编辑模式播放，请先退出 Play Mode。"); return;
            }
            var scene = SceneManager.GetSceneByPath(ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                bool dirtyScene = Enumerable.Range(0, SceneManager.sceneCount).Any(index => SceneManager.GetSceneAt(index).isDirty);
                if (!File.Exists(ScenePath))
                {
                    scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, dirtyScene ? NewSceneMode.Additive : NewSceneMode.Single);
                    var cameraObject = new GameObject("Skill Effect Review Camera");
                    SceneManager.MoveGameObjectToScene(cameraObject, scene);
                    cameraObject.transform.position = new Vector3(0, 0, -10);
                    var camera = cameraObject.AddComponent<Camera>(); camera.orthographic = true;
                    camera.orthographicSize = 5; camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(.1f, .12f, .16f);
                    if (!EditorSceneManager.SaveScene(scene, ScenePath))
                        throw new IOException("技能审核场景保存失败。");
                }
                else scene = EditorSceneManager.OpenScene(ScenePath, dirtyScene ? OpenSceneMode.Additive : OpenSceneMode.Single);
            }
            SceneManager.SetActiveScene(scene);
            ShowWindow();
        }

        /// <summary>场景入口调用时展示一个独立四格审核窗口。</summary>
        /// <remarks>窗口关闭释放内存；用户决定已写入文档，不依赖窗口布局或场景存档。</remarks>
        private static void ShowWindow()
        {
            var window = GetWindow<SkillEffectReviewWindow>("技能效果审核");
            window.minSize = new Vector2(1000, 780);
            window.Show(); window.Focus();
        }

        /// <summary>Unity 创建或恢复窗口时读取正式配置与用户记录，并启动编辑器播放时钟。</summary>
        /// <remarks>注册窗口专属更新、清场与 Play 回调；OnDisable 对称解除并释放资源。</remarks>
        private void OnEnable()
        {
            lastTick = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            EditorSceneManager.sceneClosing += SceneClosing;
            ReloadCatalog();
        }

        /// <summary>Unity 关闭窗口或重载程序集前保存未写入的审核决定并释放四格内存资源。</summary>
        /// <remarks>不保存 Scene/Prefab，不清理其他工程或用户对象。</remarks>
        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            EditorSceneManager.sceneClosing -= SceneClosing;
            SaveNow(); DisposePlayers();
        }

        /// <summary>用户开始 Play 时保存审核决定并关闭编辑工具，不在回到编辑模式后自动弹出。</summary>
        /// <param name="state">Unity 状态切换阶段。</param>
        /// <remarks>保存失败时保留窗口和内存内容；正常关闭释放四格内存。窗口仅由用户菜单入口打开，不接管正式战斗或测试还原。</remarks>
        private void PlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode || !SaveNow()) return;
            DisposePlayers(); Close();
        }

        /// <summary>审核场景关闭前释放属于该场景的临时根，避免编辑器残留不可见渲染对象。</summary>
        /// <param name="scene">将被关闭的场景。</param><param name="removingScene">是否移除场景。</param>
        /// <remarks>文档保存后关闭审核窗口；不干预其他场景。</remarks>
        private void SceneClosing(Scene scene, bool removingScene)
        {
            if (scene.path == ScenePath) { SaveNow(); DisposePlayers(); Close(); }
        }

        /// <summary>打开或主动重载时读取 TbSkill 与唯一 TbCombatPresentation；读取失败保持原文件并显示错误。</summary>
        /// <remarks>初始化新目录时只创建人工审核文本，默认全部待确认，不将已有业务分类视为人工认可。</remarks>
        public void ReloadCatalog()
        {
            try
            {
                if (dirty && !SaveNow()) return;
                var tables = CombatCylinderPipeline.ReadTables();
                var presentations = tables.TbPerformanceScenario.DataList.Where(item => item.Kind == EPerformanceKind.Combat)
                    .Select(item => item.PresentationId_Ref).GroupBy(item => item.Id).Select(group => group.First()).ToArray();
                if (presentations.Length != 1) throw new InvalidOperationException("需要唯一战斗表现方案以确定正式 ANI 时钟。");
                presentation = presentations[0];
                catalog = SkillEffectReviewCatalog.Load(tables);
                error = null; dirty = true;
                ApplyFilter(); SaveNow();
            }
            catch (Exception exception) { catalog = null; DisposePlayers(); error = exception.Message; }
        }

        /// <summary>修改后或关闭前保存审核文本；失败时保留内存决定与未保存提示，等待用户重试。</summary>
        /// <returns>当前修改是否已完整保存。</returns>
        /// <remarks>不吞掉写入失败，也不把失败误报为自动保存成功。</remarks>
        public bool SaveNow()
        {
            if (!dirty || catalog == null) return true;
            try { catalog.Save(); dirty = false; error = null; return true; }
            catch (Exception exception) { error = "审核未保存：" + exception.Message; return false; }
        }

        /// <summary>编辑器更新时推进四组连续组合并限制重绘频率，暂停保持各组阶段和帧，仍处理备注保存。</summary>
        /// <remarks>半秒合并文本输入；播放频率为工具显示技术参数，不进入正式战斗配置。</remarks>
        private void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            double delta = now - lastTick; lastTick = now;
            if (dirty && now - lastEdit >= .5) { SaveNow(); lastEdit = now; }
            if (catalog == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!paused) playTime += Math.Max(0, delta) * speed;
            if (now - lastRepaint < 1d / 30d) return;
            // 用真实编辑器经过时间更新，重绘节流不能丢失中间时间。
            for (int slot = 0; slot < PageSize; slot++) players[slot]?.Sample(playTime - playbackStarts[slot]);
            lastRepaint = now; Repaint();
            if (!paused) SceneView.RepaintAll();
        }

        /// <summary>筛选条件变化时计算技能分页，并使当前页始终落在有效范围内。</summary>
        private void ApplyFilter()
        {
            filtered = catalog.Filter(search, decisionFilter == 0 ? (int?)null : decisionFilter - 1);
            page = Mathf.Clamp(page, 0, PageCount - 1); BuildPage();
        }

        /// <summary>翻页时释放上一页后准备最多四组技能；后缀选择仅决定查看画面，不参与整组审核筛选。</summary>
        /// <remarks>只为屏幕上的片段读取图片，不缓存整个资源库。</remarks>
        private void BuildPage()
        {
            DisposePlayers(); playTime = 0;
            for (int slot = 0; slot < PageSize; slot++)
            {
                int index = page * PageSize + slot;
                current[slot] = index < filtered.Length ? filtered[index] : null;
                if (current[slot] == null) continue;
                var clips = current[slot].VisualSetId_Ref.ClipIds_Ref;
                var selected = clips.FirstOrDefault(clip => clip.Id == selectedClips[slot]);
                if (selected == null) selected = clips.FirstOrDefault();
                if (selected == null) continue;
                selectedClips[slot] = selected.Id; actions[slot] = 0; LoadPlayer(slot);
            }
        }

        /// <summary>组、查看方式、变体或动作切换时为该卡片建立组合播放器并从起手重播，其他卡片继续播放。</summary>
        /// <param name="slot">四格中的索引。</param>
        /// <remarks>新播放器持有的 Unity 对象仅存在内存，场景失效时仍可在窗口查看。</remarks>
        private void LoadPlayer(int slot)
        {
            players[slot]?.Dispose(); players[slot] = new SkillEffectReviewSequence();
            var clip = current[slot].VisualSetId_Ref.ClipIds_Ref.First(item => item.Id == selectedClips[slot]);
            players[slot].Load(current[slot], clip, presentation, actions[slot], SceneManager.GetSceneByPath(ScenePath), slot,
                playbackModes[slot] == 0, false, catalog.FindGroup(current[slot].Id).ProjectileFacingMode);
            playbackStarts[slot] = playTime; players[slot].Sample(0);
        }

        /// <summary>窗口重绘时将顶部控制区与四张审核卡片放入独立固定区域，避免布局事件的占位矩形导致内容覆盖翻页按钮。</summary>
        private void OnGUI()
        {
            if (catalog == null)
            {
                EditorGUILayout.HelpBox(error ?? "正在读取正式目录。", MessageType.Error);
                if (GUILayout.Button("重新读取目录")) ReloadCatalog(); return;
            }
            float headerHeight = string.IsNullOrEmpty(error) ? 140f : 180f;
            GUILayout.BeginArea(new Rect(8, 8, position.width - 16, headerHeight));
            DrawToolbar();
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            EditorGUILayout.HelpBox("随身施法层跟着黄色十字所示的施法者移动，与飞行同时开始，不等待前摇；飞行 2 秒后命中。落地爆炸仍留在落点。整组标记不变，此处为编辑器演示。", MessageType.Info);
            GUILayout.EndArea();
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorGUI.HelpBox(new Rect(8, headerHeight + 16, position.width - 16, 40),
                    "当前为 Play Mode，审核已暂停；请退出 Play 后继续。", MessageType.Warning); return;
            }
            // GetLastRect 在 Layout 阶段只返回占位矩形，不能用于决定 BeginArea 的位置；所有事件共用固定边界。
            float top = headerHeight + 16;
            float width = (position.width - 24) / 2;
            float height = (position.height - top - 30) / 2;
            for (int slot = 0; slot < PageSize; slot++)
                DrawCard(slot, new Rect(8 + slot % 2 * (width + 8), top + slot / 2 * (height + 8), width, height));
            GUI.Label(new Rect(10, position.height - 20, position.width - 20, 20),
                (dirty ? "未保存" : "已自动保存") + "  ·  " + SkillEffectReviewCatalog.DirectoryPath + "/" + SkillEffectReviewCatalog.MarkdownName);
        }

        /// <summary>在顶部独立区域内分行绘制检索、显著的分页按钮和播放控制，翻页时先保存人工决定。</summary>
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUI.BeginChangeCheck();
            search = GUILayout.TextField(search, GUI.skin.FindStyle("ToolbarSearchTextField") ?? EditorStyles.toolbarTextField, GUILayout.MinWidth(150));
            decisionFilter = EditorGUILayout.Popup(decisionFilter, FilterNames, GUILayout.Width(100));
            if (EditorGUI.EndChangeCheck()) { page = 0; ApplyFilter(); }
            if (GUILayout.Button("刷新目录", EditorStyles.toolbarButton, GUILayout.Width(70))) ReloadCatalog();
            if (GUILayout.Button("保存备注", EditorStyles.toolbarButton, GUILayout.Width(70))) SaveNow();
            if (GUILayout.Button("查看备注文档", EditorStyles.toolbarButton, GUILayout.Width(100)))
            {
                if (SaveNow()) EditorUtility.RevealInFinder(Path.GetFullPath(Path.Combine(SkillEffectReviewCatalog.DirectoryPath, SkillEffectReviewCatalog.MarkdownName)));
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = page > 0;
            if (GUILayout.Button("◀ 上一页", GUILayout.Width(100), GUILayout.Height(28))) { SaveNow(); page--; BuildPage(); }
            GUI.enabled = true;
            GUILayout.Label($"第 {page + 1} / {PageCount} 页 · {filtered.Length} 组技能", GUILayout.Width(200), GUILayout.Height(28));
            GUI.enabled = page + 1 < PageCount;
            if (GUILayout.Button("下一页 ▶", GUILayout.Width(100), GUILayout.Height(28))) { SaveNow(); page++; BuildPage(); }
            GUI.enabled = true;
            if (GUILayout.Button("从待确认开始", GUILayout.Width(110), GUILayout.Height(28)))
            { SaveNow(); decisionFilter = 1; search = ""; page = 0; ApplyFilter(); }
            GUILayout.FlexibleSpace(); EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            paused = GUILayout.Toggle(paused, "暂停", "Button", GUILayout.Width(50));
            if (GUILayout.Button("重播本页", GUILayout.Width(80)))
            { playTime = 0; for (int slot = 0; slot < PageSize; slot++) { playbackStarts[slot] = 0; players[slot]?.Sample(0); } }
            GUILayout.Label("速度", GUILayout.Width(30)); speed = EditorGUILayout.Slider(speed, .1f, 2f, GUILayout.Width(130));
            GUILayout.Label("缩放", GUILayout.Width(30)); zoom = EditorGUILayout.Slider(zoom, .25f, 2f, GUILayout.Width(130));
            GUILayout.FlexibleSpace(); EditorGUILayout.EndHorizontal();
        }

        /// <summary>绘制整组连续组合或分段查看、当前播放阶段与统一审核决定，技术 ID 收入资源详情。</summary>
        /// <param name="slot">四格索引。</param><param name="rect">该卡片在窗口中的位置。</param>
        /// <remarks>所有分类都绑定 TbSkill.id 的整组记录，切换后缀保持同一审核状态；不再编辑旧片段决定。</remarks>
        private void DrawCard(int slot, Rect rect)
        {
            GUILayout.BeginArea(rect, GUI.skin.box);
            var skill = current[slot];
            if (skill == null) { GUILayout.Label("没有更多技能"); GUILayout.EndArea(); return; }
            var clips = skill.VisualSetId_Ref.ClipIds_Ref;
            var entry = catalog.FindGroup(skill.Id);
            GUILayout.Label($"{entry.ResourceGroup}  {skill.Name}    整组：{SkillEffectReviewCatalog.Decisions[entry.Decision]}", EditorStyles.boldLabel);
            int selected = clips.FindIndex(clip => clip.Id == selectedClips[slot]);
            string[] names = clips.Select(clip => Path.GetFileNameWithoutExtension(clip.AniResourceId_Ref.Path)).ToArray();
            EditorGUILayout.BeginHorizontal();
            int mode = EditorGUILayout.Popup(playbackModes[slot], PlaybackModeNames, GUILayout.Width(115));
            int next = EditorGUILayout.Popup(selected, names);
            EditorGUILayout.EndHorizontal();
            if (next != selected || mode != playbackModes[slot])
            { playbackModes[slot] = mode; selectedClips[slot] = clips[next].Id; actions[slot] = 0; LoadPlayer(slot); }
            var player = players[slot];
            var clipConfig = player?.CurrentClip ?? clips.First(clip => clip.Id == selectedClips[slot]);
            GUILayout.Label(new GUIContent(player?.Summary ?? "正在准备组合", player?.Summary), EditorStyles.miniLabel);
            bool hasLegacyType = !string.IsNullOrEmpty(entry.SkillType);
            bool hasFlight = clips.Any(clip => SkillEffectReviewSequence.ReadSuffix(clip) == "f" ||
                SkillEffectReviewSequence.ReadSuffix(clip).StartsWith("f_", StringComparison.Ordinal));
            float reservedHeight = (hasLegacyType ? 302 : 284) + (resourceDetails[slot] ? 40 : 0) +
                (string.IsNullOrEmpty(entry.MigrationWarning) ? 0 : 38) + (hasFlight ? 18 : 0);
            Rect previewRect = GUILayoutUtility.GetRect(10, Math.Max(30, rect.height - reservedHeight), GUILayout.ExpandWidth(true));
            player?.Draw(previewRect, zoom);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"{player?.StageLabel} · {player?.Elapsed:F2}/{player?.Duration:F2}s · 帧 {player?.FrameIndex + 1}/{player?.FrameCount}", GUILayout.MinWidth(180));
            if (player != null && player.ActionCount > 1)
            {
                int action = EditorGUILayout.IntSlider("动作", actions[slot], 0, player.ActionCount - 1);
                if (action != actions[slot]) { actions[slot] = action; LoadPlayer(slot); }
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Label($"整组包含 {clips.Count} 段 · 以下标记对全部后缀生效", EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(entry.MigrationWarning)) EditorGUILayout.HelpBox(entry.MigrationWarning, MessageType.Warning);
            EditorGUI.BeginChangeCheck();
            entry.Decision = EditorGUILayout.Popup("整组是否使用", entry.Decision, SkillEffectReviewCatalog.Decisions);
            int typeMask = SkillEffectReviewCatalog.GetTypeMask(entry);
            int nextMask = EditorGUILayout.MaskField("技能类型（可多选）", typeMask, SkillEffectReviewCatalog.TypeChoices);
            if (nextMask != typeMask) SkillEffectReviewCatalog.SetTypeMask(entry, nextMask);
            int elementIndex = Array.IndexOf(SkillEffectReviewCatalog.ElementChoices, entry.Element);
            int nextElement = EditorGUILayout.Popup("元素属性", Math.Max(0, elementIndex), SkillEffectReviewCatalog.ElementChoices);
            if (nextElement != Math.Max(0, elementIndex))
                entry.Element = nextElement == 0 ? "" : SkillEffectReviewCatalog.ElementChoices[nextElement];
            int originalFacing = entry.ProjectileFacingMode;
            if (hasFlight) entry.ProjectileFacingMode = EditorGUILayout.Popup("飞行朝向", entry.ProjectileFacingMode, SkillEffectReviewCatalog.FacingModes);
            if (hasLegacyType) EditorGUILayout.LabelField("原填写类型（保留）", entry.SkillType, EditorStyles.miniLabel);
            GUILayout.Label("整组备注（表现特征、适用场景、限制）", EditorStyles.miniLabel);
            entry.Notes = EditorGUILayout.TextArea(entry.Notes, GUILayout.Height(44));
            if (EditorGUI.EndChangeCheck()) { dirty = true; lastEdit = EditorApplication.timeSinceStartup; }
            if (originalFacing != entry.ProjectileFacingMode) LoadPlayer(slot);
            resourceDetails[slot] = EditorGUILayout.Foldout(resourceDetails[slot], "资源详情（技术 ID）");
            if (resourceDetails[slot])
            {
                GUILayout.Label($"技能 ID：{skill.Id}  · 当前片段 ID：{clipConfig.Id}", EditorStyles.miniLabel);
                GUILayout.Label(clipConfig.AniResourceId_Ref.Path, EditorStyles.miniLabel);
            }
            GUILayout.EndArea();
        }

        /// <summary>翻页、窗口关闭或 Play 切换时释放四个播放器的独占内存。</summary>
        /// <remarks>不修改场景持久内容、正式技能资源或其他窗口。</remarks>
        private void DisposePlayers()
        {
            for (int index = 0; index < players.Length; index++) { players[index]?.Dispose(); players[index] = null; }
        }
    }
}
