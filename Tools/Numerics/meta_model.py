# -*- coding: utf-8 -*-
"""局外养成 × 局内 Build 强度模型"""
exec(open('balance_model.py',encoding='utf-8').read())
import copy

# ---- 局外养成参数 ----
QC = {"白":1.00,"绿":1.04,"蓝":1.09,"紫":1.15,"金":1.22,"红":1.30}   # 角色品质：属性基线系数
QW = {"白":1.00,"绿":1.07,"蓝":1.15,"紫":1.25,"金":1.36,"红":1.50}   # 武器品质：武器攻击贡献系数
TALENT = {"无":1.00,"初":1.08,"中":1.17,"满":1.25}                    # 永久天赋
GEAR   = {"无":1.00,"有":1.18}                                        # 装备6槽(P1)
STAR_PER_WEAPON = {1:0.08,2:0.16,3:0.26,4:0.38,5:0.52}               # 单把武器星级攻击加成

def build(cls, qc="白", qw="白", talent="无", gear="无",
          weapon_slots=1, weapon_star=1):
    """返回该养成状态下的属性字典"""
    p = copy.deepcopy(cls)
    qc_v, qw_v, t_v, g_v = QC[qc], QW[qw], TALENT[talent], GEAR[gear]
    p['hp']      = p['hp'] * qc_v * g_v
    p['defense'] = p['defense'] * qc_v
    atk_char     = p['atk'] * qc_v * t_v
    weapon_mul   = 1.0 + weapon_slots * STAR_PER_WEAPON[weapon_star] * qw_v
    p['atk']     = atk_char * weapon_mul
    p['crit']    = p['crit'] * (1 + (t_v - 1) * 0.6)
    return p

BASE_CLS = dict(BASE)
CLASSES = {
 'B_0 重装': dict(hp=120, atk=16, defense=4,  aspd=0.90, crit=0,  evasion=0,  dparam=1.0, extra=1.00),
 'H_0 法术': dict(hp=78,  atk=23, defense=0,  aspd=1.00, crit=15, evasion=0,  dparam=1.0, extra=1.00),
 'J_0 连击': dict(hp=82,  atk=17, defense=0,  aspd=1.30, crit=0,  evasion=10, dparam=1.0, extra=1.00),
 'K_0 召唤': dict(hp=95,  atk=18, defense=0,  aspd=1.00, crit=0,  evasion=0,  dparam=1.0, extra=1.18),
 'Y_0 领域': dict(hp=82,  atk=20, defense=1,  aspd=1.00, crit=0,  evasion=0,  dparam=1.0, extra=1.09),
 'Z_0 增益': dict(hp=95,  atk=20, defense=0,  aspd=1.00, crit=12, evasion=0,  dparam=1.0, extra=1.00),
}
for v in CLASSES.values():
    for k, d in BASE.items():
        v.setdefault(k, d)

STAGES = {
 '① 新号·开局':   dict(qc="白", qw="白", talent="无", gear="无", weapon_slots=1, weapon_star=1),
 '② 新手·3局后':  dict(qc="绿", qw="绿", talent="初", gear="无", weapon_slots=2, weapon_star=2),
 '③ 中期·15h':    dict(qc="蓝", qw="紫", talent="中", gear="无", weapon_slots=4, weapon_star=3),
 '④ 后期·40h':    dict(qc="紫", qw="金", talent="满", gear="无", weapon_slots=6, weapon_star=4),
 '⑤ 满养成·图鉴全': dict(qc="红", qw="红", talent="满", gear="有", weapon_slots=6, weapon_star=5),
}

print(f"{'阶段':16s}{'EDPS(普)':>9s}{'EHP(普)':>9s}{'加权战力':>9s}{'相对新号':>9s}")
rows={}
for sname, cfg in STAGES.items():
    sc = sum(score(build(c, **cfg)) for c in CLASSES.values())/6
    rows[sname]=sc
first=None
for sname, cfg in STAGES.items():
    p = build(CLASSES['H_0 法术'], **cfg)
    d,e,_ = power(p, 8.0)
    if first is None: first=rows[sname]
    print(f"{sname:16s}{d:9.2f}{e:9.1f}{rows[sname]:9.2f}{rows[sname]/first:8.2f}x")

print("\n=== 局外单层贡献（其它层固定为「中期」）===")
mid = dict(qc="蓝", qw="紫", talent="中", gear="无", weapon_slots=4, weapon_star=3)
base_w = score(build(CLASSES['H_0 法术'], **mid))
for layer, opts in [('角色品质',list(QC)),('武器品质',list(QW)),('永久天赋',list(TALENT)),('装备6槽',list(GEAR))]:
    outs=[]
    for o in opts:
        c=dict(mid)
        key={'角色品质':'qc','武器品质':'qw','永久天赋':'talent','装备6槽':'gear'}[layer]
        c[key]=o
        outs.append(f"{o}:{score(build(CLASSES['H_0 法术'],**c))/base_w:.3f}")
    print(f"{layer:8s} " + "  ".join(outs))

print("\n=== 满养成下六系平衡复核 ===")
full = STAGES['⑤ 满养成·图鉴全']
scs={n: score(build(c, **full)) for n,c in CLASSES.items()}
avg=sum(scs.values())/6
for n,s in sorted(scs.items(), key=lambda x:-x[1]):
    print(f"{n:12s}{s:9.2f}{'':2s}{(s/avg-1)*100:+6.2f}%")
