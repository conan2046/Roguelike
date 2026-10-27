#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unity dead-code audit by GUID reference.

判定脚本是否为死代码时，Unity 的序列化引用依赖 .meta 中的 GUID，
直接 grep 类名会漏判（脚本可以通过 GUID 挂在 Scene/Prefab 上而类名不出现在任何 .cs 中）。

扫描维度：
  1. GUID 引用：cs 的 .meta GUID 是否出现在 Scene / Prefab / asset / 其他可序列化资源中
  2. 代码引用：类名是否出现在其他 .cs 文件中
  3. 特殊入口：[MenuItem] / [RuntimeInitializeOnLoadMethod] / [InitializeOnLoad] / [CreateAssetMenu]
  4. 字符串引用：脚本名是否以字符串形式出现在其他文件中（Resources.Load / AssetDatabase 等）

输出：控制台摘要 + JSON + Markdown 报告。不会删除任何文件。

Usage:
    python dead_code_audit.py [--root PROJECT_ROOT] [--out OUTPUT_DIR]
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from collections import defaultdict
from dataclasses import dataclass, field, asdict
from pathlib import Path

# Unity 序列化引用的 GUID 格式：{fileID: 11500000, guid: <32 hex>, type: 3}
GUID_RE = re.compile(r"guid:\s*([0-9a-fA-F]{32})")
META_GUID_RE = re.compile(r"^guid:\s*([0-9a-fA-F]{32})\s*$", re.MULTILINE)

# 跳过二进制与大文件，只读文本类资源
BINARY_SUFFIXES = {
    ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tga", ".psd", ".exr", ".hdr",
    ".dll", ".so", ".dylib", ".exe", ".pdb", ".mdb", ".zip", ".7z", ".rar",
    ".mp3", ".ogg", ".wav", ".aif", ".aiff", ".mp4", ".mov", ".avi", ".webm",
    ".ttf", ".otf", ".woff", ".woff2", ".fbx", ".obj", ".blend", ".dae",
    ".bytes", ".bundle", ".apk", ".aab", ".unitypackage", ".pdf",
}
MAX_FILE_BYTES = 8 * 1024 * 1024

# 默认排除的目录（生成代码与第三方不纳入死代码判定）
DEFAULT_EXCLUDE_DIRS = {"Generated", "ThirdParty", "Library", "Temp", "obj", "Logs"}


@dataclass
class ScriptRecord:
    """单个 C# 脚本的审计记录。"""

    rel_path: str
    guid: str
    type_names: list[str] = field(default_factory=list)
    guid_refs: list[str] = field(default_factory=list)      # 引用该 GUID 的资源文件
    code_refs: list[str] = field(default_factory=list)      # 引用类名的 .cs 文件
    string_refs: list[str] = field(default_factory=list)    # 以字符串形式命中的文件
    editor_entries: list[str] = field(default_factory=list)  # MenuItem / InitializeOnLoad 等
    ecs_entries: list[str] = field(default_factory=list)     # SystemBase / ISystem / Baker 等
    ecs_auto_created: bool = False                           # 是否会被 Entities 自动实例化
    in_test_assembly: bool = False
    reachable: bool = False                                  # 从 root 出发可达
    outgoing_refs: list[str] = field(default_factory=list)    # 本脚本引用了哪些脚本
    verdict: str = ""
    reasons: list[str] = field(default_factory=list)


def iter_files(root: Path, exclude_dirs: set[str]):
    """遍历项目文件，跳过排除目录与二进制后缀。"""
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in exclude_dirs and not d.startswith(".")]
        for name in filenames:
            p = Path(dirpath) / name
            if p.suffix.lower() in BINARY_SUFFIXES:
                continue
            try:
                if p.stat().st_size > MAX_FILE_BYTES:
                    continue
            except OSError:
                continue
            yield p


def read_text(path: Path) -> str:
    """读取文本文件，失败返回空串。"""
    try:
        return path.read_text(encoding="utf-8", errors="ignore")
    except Exception:
        return ""


def collect_script_guids(root: Path, exclude_dirs: set[str]) -> list[tuple[Path, str]]:
    """收集 (cs 文件, guid) 列表，无 .meta 的脚本 guid 记为空串。

    只收集 Assets 下的脚本：Unity 仅编译 Assets 内的 C#，
    Assets 外的工具项目（如 Tools/）不参与 Unity 序列化引用体系。
    """
    result = []
    assets = root / "Assets"
    if not assets.exists():
        assets = root
    for cs in assets.rglob("*.cs"):
        if any(part in exclude_dirs or part.startswith(".") for part in cs.relative_to(root).parts):
            continue
        meta = cs.with_suffix(cs.suffix + ".meta")
        guid = ""
        if meta.exists():
            m = META_GUID_RE.search(read_text(meta))
            if m:
                guid = m.group(1).lower()
        result.append((cs, guid))
    return result


def build_guid_reference_index(root: Path, exclude_dirs: set[str]) -> dict[str, set[str]]:
    """扫描全库，建立 guid -> 引用它的文件相对路径集合。

    自身定义处（该脚本自己的 .cs.meta）不计为引用。
    """
    index: dict[str, set[str]] = defaultdict(set)
    all_files = list(iter_files(root, exclude_dirs))
    print(f"  scanning {len(all_files)} text files for GUID references ...")
    for i, p in enumerate(all_files):
        if i and i % 3000 == 0:
            print(f"    {i}/{len(all_files)}")
        if p.suffix == ".meta":
            continue  # .meta 中的 guid 是定义，不是引用
        text = read_text(p)
        if "guid:" not in text:
            continue
        rel = str(p.relative_to(root)).replace("\\", "/")
        for g in GUID_RE.findall(text):
            index[g.lower()].add(rel)
    return index


def extract_type_names(text: str) -> list[str]:
    """提取脚本中声明的 class / struct / interface / enum 名称。"""
    names = []
    for m in re.finditer(
        r"^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected)?\s*"
        r"(?:abstract|sealed|static|partial|readonly)?\s*"
        r"(?:class|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)",
        text,
        re.MULTILINE,
    ):
        names.append(m.group(1))
    return names


def detect_editor_entries(text: str) -> list[str]:
    """检测不通过类名调用也能触发的入口特性。"""
    entries = []
    patterns = {
        "MenuItem": r"\[MenuItem\s*\(",
        "RuntimeInitializeOnLoadMethod": r"\[RuntimeInitializeOnLoadMethod",
        "InitializeOnLoad": r"\[InitializeOnLoad",
        "InitializeOnLoadMethod": r"\[InitializeOnLoadMethod",
        "CreateAssetMenu": r"\[CreateAssetMenu",
        "DidReloadScripts": r"\[DidReloadScripts",
        "PostProcessBuild": r"\[PostProcessBuild",
        "Preserve": r"\[UnityEngine\.Scripting\.Preserve\]",
        # 以下通过特性绑定目标类型，不会在代码中出现类名引用
        "CustomEditor": r"\[CustomEditor\s*\(",
        "CustomPropertyDrawer": r"\[CustomPropertyDrawer\s*\(",
        "CustomPreview": r"\[CustomPreview\s*\(",
        "CustomSceneViewGUI": r"\[CustomSceneViewGUI\s*\(",
    }
    for name, pat in patterns.items():
        if re.search(pat, text):
            entries.append(name)
    return entries


def detect_ecs_entries(text: str) -> list[str]:
    """检测 DOTS 隐式入口。

    Entities 1.0 会自动发现并实例化所有 SystemBase / ISystem，除非标记
    [DisableAutoCreation]。Baker 与系统组同理由框架驱动。
    这些脚本即使没有任何代码引用也是活的。
    """
    entries = []
    has_disable = bool(re.search(r"\[\s*DisableAutoCreation", text))
    patterns = {
        "SystemBase": r":\s*SystemBase\b",
        "ISystem": r":\s*(?:[^,\n{]*\b)?ISystem\b",
        "ISystemStartStop": r"\bISystemStartStop\b",
        "ComponentSystemGroup": r":\s*\w*ComponentSystemGroup\b",
        "Baker": r":\s*Baker\s*<",
        "ICustomBootstrap": r"\bICustomBootstrap\b",
        "IJobEntity": r"\bIJobEntity\b",
    }
    for name, pat in patterns.items():
        if re.search(pat, text):
            entries.append(name)
    if has_disable and entries:
        entries.append("DisableAutoCreation(已禁用自动创建)")
    return entries


def build_code_index(root: Path, exclude_dirs: set[str]) -> dict[str, str]:
    """建立所有 .cs 的相对路径 -> 内容 映射，用于类名引用扫描。"""
    code = {}
    for cs in root.rglob("*.cs"):
        if any(part in exclude_dirs or part.startswith(".") for part in cs.relative_to(root).parts):
            continue
        rel = str(cs.relative_to(root)).replace("\\", "/")
        code[rel] = read_text(cs)
    return code


def main() -> int:
    parser = argparse.ArgumentParser(description="Unity dead code audit by GUID reference")
    parser.add_argument("--root", default=str(Path(__file__).resolve().parents[2]),
                        help="Unity project root (default: repo root)")
    parser.add_argument("--out", default="", help="output directory (default: <root>/outputs/dead-code-audit)")
    parser.add_argument("--exclude", default="", help="extra dir names to exclude, comma separated")
    args = parser.parse_args()

    started = time.time()
    root = Path(args.root).resolve()
    out_dir = Path(args.out) if args.out else root / "outputs" / "dead-code-audit"
    out_dir.mkdir(parents=True, exist_ok=True)

    exclude_dirs = set(DEFAULT_EXCLUDE_DIRS)
    if args.exclude:
        exclude_dirs |= {d.strip() for d in args.exclude.split(",") if d.strip()}

    print(f"root: {root}")
    print(f"exclude dirs: {sorted(exclude_dirs)}")

    print("[1/4] collecting script GUIDs ...")
    scripts = collect_script_guids(root, exclude_dirs)
    print(f"  {len(scripts)} scripts found")

    print("[2/4] building GUID reference index ...")
    guid_index = build_guid_reference_index(root, exclude_dirs)
    print(f"  {len(guid_index)} distinct GUIDs referenced")

    print("[3/4] building code index ...")
    code_index = build_code_index(root, exclude_dirs)
    print(f"  {len(code_index)} code files indexed")

    print("[4/4] analysing ...")
    records: list[ScriptRecord] = []
    by_rel: dict[str, ScriptRecord] = {}

    # 阶段 A：采集每个脚本的信号
    for cs, guid in scripts:
        rel = str(cs.relative_to(root)).replace("\\", "/")
        text = code_index.get(rel, read_text(cs))
        types = extract_type_names(text)

        rec = ScriptRecord(rel_path=rel, guid=guid, type_names=types)
        rel_parts = rel.split("/")
        rec.in_test_assembly = "Tests" in rel_parts or any(t.endswith("Tests") for t in types)
        rec.editor_entries = detect_editor_entries(text)
        rec.ecs_entries = detect_ecs_entries(text)
        # 自动创建 = 有 DOTS 入口且未被 [DisableAutoCreation] 关闭
        rec.ecs_auto_created = bool(rec.ecs_entries) and not any(
            e.startswith("DisableAutoCreation") for e in rec.ecs_entries
        )

        if guid:
            rec.guid_refs = sorted(guid_index.get(guid, set()))

        records.append(rec)
        by_rel[rel] = rec

    # 阶段 B：建立 type -> 归属脚本 映射，用于构造引用图
    type_owner: dict[str, str] = {}
    for rec in records:
        for t in rec.type_names:
            if len(t) >= 4:
                type_owner.setdefault(t, rec.rel_path)

    # 阶段 C：构造出边（本脚本引用了哪些脚本）与入边（谁引用了本脚本）
    for rec in records:
        text = code_index.get(rec.rel_path, "")
        # 出边：本脚本文本中出现的其他脚本类型名
        outgoing = set()
        for t, owner in type_owner.items():
            if owner == rec.rel_path:
                continue
            if re.search(r"\b" + re.escape(t) + r"\b", text):
                outgoing.add(owner)
        rec.outgoing_refs = sorted(outgoing)

        # 入边与字符串引用
        stem = Path(rec.rel_path).stem
        for other_rel, other_text in code_index.items():
            if other_rel == rec.rel_path:
                continue
            for t in rec.type_names:
                if len(t) < 4:
                    continue
                if re.search(r"\b" + re.escape(t) + r"\b", other_text):
                    rec.code_refs.append(other_rel)
                    break
            if len(stem) >= 4 and ('"' + stem + '"' in other_text or f"'{stem}'" in other_text):
                rec.string_refs.append(other_rel)

    # 阶段 D：从 root 做可达性遍历。
    # root = 被 Scene/Prefab/asset 的 GUID 引用
    #      + 有编辑器或运行时自动入口
    #      + 会被 Entities 自动发现
    #      + 测试程序集（NUnit 自动发现）
    # 只沿出边传播，因此"死代码岛内部互相引用"不会被误判为存活。
    roots = [
        r.rel_path
        for r in records
        if r.guid_refs or r.editor_entries or r.ecs_auto_created or r.in_test_assembly
    ]
    graph = {r.rel_path: r.outgoing_refs for r in records}
    seen = set(roots)
    queue = list(roots)
    while queue:
        cur = queue.pop()
        for nxt in graph.get(cur, []):
            if nxt not in seen:
                seen.add(nxt)
                queue.append(nxt)
    for rec in records:
        rec.reachable = rec.rel_path in seen

    # 阶段 E：判定
    for rec in records:
        if rec.in_test_assembly:
            rec.verdict = "TEST"
            rec.reasons.append("测试程序集，由 NUnit 自动发现")
        elif rec.guid_refs:
            rec.verdict = "ROOT"
            rec.reasons.append(f"GUID 被 {len(rec.guid_refs)} 个资源引用")
        elif rec.ecs_auto_created:
            rec.verdict = "ROOT"
            rec.reasons.append("Entities 自动发现: " + ", ".join(
                e for e in rec.ecs_entries if not e.startswith("DisableAutoCreation")))
        elif rec.editor_entries:
            rec.verdict = "ROOT"
            rec.reasons.append("编辑器/运行时入口: " + ", ".join(rec.editor_entries))
        elif rec.reachable:
            rec.verdict = "REACHABLE"
            rec.reasons.append("从 root 可达（被存活代码引用）")
        elif rec.code_refs or rec.string_refs:
            # 有引用信号但不可达 = 死代码岛
            rec.verdict = "ORPHAN"
            rec.reasons.append("有引用信号但从 root 不可达，疑为死代码岛")
        else:
            rec.verdict = "ISOLATED"
            rec.reasons.append("无任何引用信号且从 root 不可达")

    order = {"ORPHAN": 0, "ISOLATED": 1, "TEST": 2, "REACHABLE": 3, "ROOT": 4}
    records.sort(key=lambda r: (order.get(r.verdict, 9), r.rel_path))

    counts = defaultdict(int)
    for r in records:
        counts[r.verdict] += 1

    elapsed = time.time() - started

    # 输出 JSON
    json_path = out_dir / "dead-code-audit.json"
    json_path.write_text(
        json.dumps(
            {
                "root": str(root),
                "elapsed_seconds": round(elapsed, 2),
                "counts": dict(counts),
                "records": [asdict(r) for r in records],
            },
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )

    # 输出 Markdown
    md = [
        "# Dead Code Audit (GUID based)",
        "",
        f"- root: `{root}`",
        f"- elapsed: {elapsed:.1f}s",
        f"- scripts scanned: {len(records)}",
        f"- ROOT: {counts['ROOT']} / REACHABLE: {counts['REACHABLE']} / "
        f"ORPHAN: {counts['ORPHAN']} / ISOLATED: {counts['ISOLATED']} / TEST: {counts['TEST']}",
        "",
        "> ROOT = 被 Scene/Prefab/asset 的 GUID 引用，或有编辑器/Entities 自动入口。",
        "> REACHABLE = 从 ROOT 沿引用图可达。",
        "> ORPHAN = 有引用信号但从 ROOT 不可达（死代码岛）。",
        "> ISOLATED = 无任何引用信号且不可达。",
        "> **ORPHAN / ISOLATED 仅表示不可达，不等于可删除。** 删除前仍需确认反射、",
        "> ICustomBootstrap、YooAsset collector、构建脚本路径字符串。",
        "",
        "## ORPHAN（死代码岛，优先人工确认）",
        "",
        "| 脚本 | GUID | 类型 | 信号 |",
        "|---|---|---|---|",
    ]
    for r in records:
        if r.verdict == "ORPHAN":
            sig = f"代码引用 {len(r.code_refs)} / 字符串 {len(r.string_refs)}"
            md.append(
                f"| `{r.rel_path}` | `{r.guid or 'NO_META'}` | {', '.join(r.type_names) or '-'} | {sig} |"
            )

    md += ["", "## ISOLATED（无引用信号）", "", "| 脚本 | GUID | 类型 |", "|---|---|---|"]
    for r in records:
        if r.verdict == "ISOLATED":
            md.append(
                f"| `{r.rel_path}` | `{r.guid or 'NO_META'}` | {', '.join(r.type_names) or '-'} |"
            )

    md += ["", "## ROOT（自动入口或资源引用）", "", "| 脚本 | 原因 |", "|---|---|"]
    for r in records:
        if r.verdict == "ROOT":
            md.append(f"| `{r.rel_path}` | {'; '.join(r.reasons)} |")

    md += ["", f"## REACHABLE（共 {counts['REACHABLE']}）", "", "| 脚本 |", "|---|"]
    for r in records:
        if r.verdict == "REACHABLE":
            md.append(f"| `{r.rel_path}` |")

    md_path = out_dir / "dead-code-audit.md"
    md_path.write_text("\n".join(md), encoding="utf-8")

    print()
    print(f"done in {elapsed:.1f}s")
    print(f"  ROOT      : {counts['ROOT']}")
    print(f"  REACHABLE : {counts['REACHABLE']}")
    print(f"  ORPHAN    : {counts['ORPHAN']}")
    print(f"  ISOLATED  : {counts['ISOLATED']}")
    print(f"  TEST      : {counts['TEST']}")
    print(f"  json: {json_path}")
    print(f"  md  : {md_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
