# -*- coding: utf-8 -*-
"""rouge 六系平衡模型：基于项目真实战斗公式 CombatMath.Damage"""
import math

RAND = 1.0                 # (8000+12000)/2/10000
CRIT_MULT = 1.5            # 15000bp
MIN_INTERVAL = 0.05
BASE_INTERVAL = 0.5        # Projectile
ENEMY_HIT = 100.0          # 怪命中值
ENEMY_CRITRES = 100.0

def dmg(atk, defense, dparam):
    return atk * atk / (atk + defense * dparam) * RAND

def dps(atk, aspd, crit, defense, dparam, extra=1.0):
    interval = max(BASE_INTERVAL / aspd, MIN_INTERVAL)
    crate = crit / (crit + ENEMY_CRITRES) if (crit + ENEMY_CRITRES) > 0 else 0.0
    return dmg(atk, defense, dparam) / interval * (1 + (CRIT_MULT - 1) * crate) * extra

def ehp(hp, defense, dparam, evasion, enemy_atk):
    dr = defense * dparam / (enemy_atk + defense * dparam) if (enemy_atk + defense * dparam) > 0 else 0.0
    evr = evasion / (evasion + ENEMY_HIT) if (evasion + ENEMY_HIT) > 0 else 0.0
    return hp / max(1 - dr, 1e-9) / max(1 - evr, 1e-9)

# 三类敌人（实际值，表值/1000）
SCENES = [
    ("普通怪", 8.0,  0.60),   # 地面怪 ATK 8
    ("精英怪", 12.0, 0.25),   # 快速高攻 ATK 12
    ("Boss",   12.0, 0.15),   # Boss ATK 12
]

BASE = dict(hp=100.0, atk=20.0, defense=0.0, dparam=1.0, aspd=1.0, crit=0.0, evasion=0.0, spd=400.0, extra=1.0)

def power(p, enemy_atk):
    d = dps(p['atk'], p['aspd'], p['crit'], p.get('tgt_def', 0.0), p.get('tgt_dparam', 1.0), p.get('extra', 1.0))
    e = ehp(p['hp'], p['defense'], p['dparam'], p['evasion'], enemy_atk)
    return d, e, (d ** 0.6) * (e ** 0.4)

def score(p):
    """场景加权战力，普通怪/精英/Boss = 60/25/15"""
    tot = 0.0
    for name, ea, w in SCENES:
        d, e, pw = power(p, ea)
        tot += w * pw
    return tot

def report(classes):
    base = score(BASE)
    print(f"{'系':22s}{'EDPS(普)':>9s}{'EHP(普)':>9s}{'EDPS(Boss)':>11s}{'EHP(Boss)':>10s}{'加权战力':>9s}{'偏差':>8s}")
    for name, p in classes.items():
        d1, e1, _ = power(p, 8.0)
        d2, e2, _ = power(p, 12.0)
        s = score(p)
        print(f"{name:22s}{d1:9.2f}{e1:9.1f}{d2:11.2f}{e2:10.1f}{s:9.2f}{(s/base-1)*100:+7.1f}%")
