#!/usr/bin/env python3
"""INDEPENDENT verification of the IPQ analysis reported in the paper.

Deliberately re-implements everything from scratch: stdlib csv parsing,
hand-rolled arithmetic, exact Wilcoxon by enumerating all 2^n sign
patterns. It does NOT import analyze_ipq. scipy is used only as a second
opinion at the end.

Usage: python3 verify_ipq.py path/to/ipq-results.csv
"""

import csv
import itertools
import math
import sys

# --- scoring definition, re-derived from the administered form (ipq-form.pdf) ---
# Anchors as shown to participants decide the direction, NOT the item wording.
#   item 1  "how aware of the real world"   1=not aware at all .. 7=extremely aware  -> reverse
#   item 11 "still paid attention to real"  1=fully disagree .. 7=fully agree        -> reverse
#   item 13 "just perceiving pictures"      1=fully disagree .. 7=fully agree        -> reverse
#   item 6  "did not feel present"          1=not felt present .. 7=felt present     -> NO reverse
REVERSE = {1, 11, 13}
SUBSCALES = {
    "G1":   [8],
    "SP":   [3, 6, 9, 10, 13],
    "INV":  [1, 7, 11, 14],
    "REAL": [2, 4, 5, 12],
}

# participant / condition per CSV row, reconstructed from timestamps and
# confirmed by the experimenters
ROWS = [
    ("P1", "baseline"), ("P1", "haptic"),
    ("P2", "haptic"),
    ("P3", "haptic"), ("P3", "baseline"),
    ("P4", "haptic"), ("P4", "baseline"),
    ("P5", "haptic"), ("P5", "baseline"),
    ("P6", "baseline"), ("P6", "haptic"),
    ("P7", "haptic"),
]
FIRST_CONDITION = {"P1": "baseline", "P3": "haptic", "P4": "haptic",
                   "P5": "haptic", "P6": "baseline"}

# --- values as printed in the paper, to be checked against what we compute ---
PAPER_TABLE = {                 # subscale: (M_hap, SD_hap, M_base, SD_base, delta, t, dz)
    "G1":   (5.71, 0.49, 2.40, 0.55, 3.20, 6.53, 2.92),
    "SP":   (6.09, 0.70, 2.96, 0.50, 3.12, 6.87, 3.07),
    "INV":  (5.04, 0.88, 2.15, 0.63, 2.65, 7.39, 3.30),
    "REAL": (4.36, 1.17, 2.25, 0.47, 2.05, 5.05, 2.26),
}
PAPER_ITEM_GAPS = {6: 4.3, 4: 3.8, 1: 3.3, 12: 0.9}   # claimed largest/smallest


def mean(xs):
    return sum(xs) / len(xs)


def sd(xs):                                   # sample SD, ddof=1
    m = mean(xs)
    return math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1))


def pearson(xs, ys):
    mx, my = mean(xs), mean(ys)
    num = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    dx = math.sqrt(sum((x - mx) ** 2 for x in xs))
    dy = math.sqrt(sum((y - my) ** 2 for y in ys))
    return num / (dx * dy) if dx and dy else float("nan")


def student_t_sf(t, df):
    """Two-sided p for Student's t, via the regularized incomplete beta."""
    x = df / (df + t * t)
    return betainc(df / 2.0, 0.5, x)          # = 2 * P(T > |t|)


def betainc(a, b, x):                          # regularized incomplete beta
    if x <= 0:
        return 0.0
    if x >= 1:
        return 1.0
    lbeta = math.lgamma(a) + math.lgamma(b) - math.lgamma(a + b)
    front = math.exp(a * math.log(x) + b * math.log(1 - x) - lbeta) / a
    # Lentz's continued fraction
    f, c, d = 1.0, 1.0, 0.0
    for i in range(0, 300):
        m, num = i // 2, 0.0
        if i == 0:
            num = 1.0
        elif i % 2 == 0:
            num = (m * (b - m) * x) / ((a + 2 * m - 1) * (a + 2 * m))
        else:
            num = -((a + m) * (a + b + m) * x) / ((a + 2 * m) * (a + 2 * m + 1))
        d = 1.0 + num * d
        d = 1e-30 if abs(d) < 1e-30 else d
        d = 1.0 / d
        c = 1.0 + num / c
        c = 1e-30 if abs(c) < 1e-30 else c
        f *= c * d
        if abs(1.0 - c * d) < 1e-14:
            break
    result = front * (f - 1.0)
    return result if x < (a + 1) / (a + b + 2) else 1.0 - betainc(b, a, 1 - x)


def exact_wilcoxon(diffs):
    """Two-sided exact signed-rank p by enumerating all 2^n sign patterns.

    Under H0 each nonzero difference is equally likely to carry either sign,
    so every one of the 2^n signings of the ranks is equally likely. p is the
    share of signings whose min(W+, W-) is at most the observed one.
    """
    d = [x for x in diffs if x != 0]
    n = len(d)
    # ranks of |d|, midranks for ties
    order = sorted(range(n), key=lambda i: abs(d[i]))
    ranks = [0.0] * n
    i = 0
    while i < n:
        j = i
        while j + 1 < n and abs(d[order[j + 1]]) == abs(d[order[i]]):
            j += 1
        midrank = (i + j) / 2.0 + 1
        for k in range(i, j + 1):
            ranks[order[k]] = midrank
        i = j + 1
    w_plus = sum(r for r, x in zip(ranks, d) if x > 0)
    w_obs = min(w_plus, sum(ranks) - w_plus)
    hits = 0
    for signs in itertools.product([1, -1], repeat=n):
        wp = sum(r for r, s in zip(ranks, signs) if s > 0)
        if min(wp, sum(ranks) - wp) <= w_obs + 1e-9:
            hits += 1
    return w_obs, hits / 2 ** n, n


def main(path):
    with open(path, newline="", encoding="utf-8") as fh:
        rows = list(csv.reader(fh))
    header, body = rows[0], rows[1:]

    ok = True

    def check(label, got, want, tol):
        nonlocal ok
        good = abs(got - want) <= tol
        ok &= good
        print(f"  [{'OK ' if good else 'FAIL'}] {label:44s} computed {got:7.3f}  paper {want:6.2f}")

    print(f"Rows: {len(body)}  Columns: {len(header)}")
    assert len(body) == 12 and len(header) == 16, "unexpected CSV shape"

    # --- parse, cross-check the condition column against the assignment ---
    raw, cond, part = [], [], []
    for idx, r in enumerate(body):
        vals = [int(v) for v in r[2:16]]
        assert len(vals) == 14 and all(1 <= v <= 7 for v in vals), f"bad row {idx}"
        raw.append({i + 1: v for i, v in enumerate(vals)})
        reported = "haptic" if r[1].startswith("With ") else "baseline"
        p, c = ROWS[idx]
        assert reported == c, f"row {idx}: CSV says {reported}, assignment says {c}"
        cond.append(c)
        part.append(p)
    print("  [OK ] every row's self-reported condition matches the assignment")
    print(f"  [OK ] all 168 responses are integers within 1-7")

    # --- recode ---
    rec = [{i: (8 - v if i in REVERSE else v) for i, v in row.items()} for row in raw]

    # --- subscale scores ---
    score = [{k: mean([r[i] for i in items]) for k, items in SUBSCALES.items()}
             for r in rec]

    hap = [i for i, c in enumerate(cond) if c == "haptic"]
    bas = [i for i, c in enumerate(cond) if c == "baseline"]
    paired = [p for p in FIRST_CONDITION]
    print(f"\nHaptic playthroughs n={len(hap)}, baseline n={len(bas)}, "
          f"complete pairs n={len(paired)}")

    print("\n=== Table 1 ===")
    for k in ("G1", "SP", "INV", "REAL"):
        mh = [score[i][k] for i in hap]
        mb = [score[i][k] for i in bas]
        pm = {p: {} for p in paired}
        for i, (p, c) in enumerate(ROWS):
            if p in pm:
                pm[p][c] = score[i][k]
        d = [pm[p]["haptic"] - pm[p]["baseline"] for p in paired]
        t = mean(d) / (sd(d) / math.sqrt(len(d)))
        p_t = student_t_sf(t, len(d) - 1)
        w, p_w, n_w = exact_wilcoxon(d)
        want = PAPER_TABLE[k]
        print(f"\n{k}:")
        check(f"{k} haptic M (n={len(hap)})", mean(mh), want[0], 0.005)
        check(f"{k} haptic SD", sd(mh), want[1], 0.005)
        check(f"{k} baseline M (n={len(bas)})", mean(mb), want[2], 0.005)
        check(f"{k} baseline SD", sd(mb), want[3], 0.005)
        check(f"{k} paired delta", mean(d), want[4], 0.005)
        check(f"{k} paired t(4)", t, want[5], 0.005)
        check(f"{k} Cohen dz", mean(d) / sd(d), want[6], 0.005)
        print(f"         t p-value {p_t:.5f} (paper prints "
              f"{'.003' if k=='G1' else '.002' if k in ('SP','INV') else '.007'})")
        print(f"         exact Wilcoxon W={w:.1f} p={p_w:.4f} over 2^{n_w} signings")
        print(f"         per-participant diffs: {[round(x,2) for x in d]}  "
              f"all positive: {all(x > 0 for x in d)}")
        print(f"         mean difference of the two printed columns: "
              f"{mean(mh)-mean(mb):+.2f}  (paper's delta {want[4]:+.2f} is the paired one)")

    # --- the abstract's strongest claim ---
    print("\n=== Claim: every paired participant higher on ALL four subscales ===")
    for p in paired:
        rowh = [i for i, (q, c) in enumerate(ROWS) if q == p and c == "haptic"][0]
        rowb = [i for i, (q, c) in enumerate(ROWS) if q == p and c == "baseline"][0]
        diffs = {k: score[rowh][k] - score[rowb][k] for k in SUBSCALES}
        print(f"  {p} (first={FIRST_CONDITION[p]:8s}): "
              + "  ".join(f"{k} {v:+.2f}" for k, v in diffs.items())
              + f"   all>0: {all(v > 0 for v in diffs.values())}")

    # --- item-level gaps quoted in the text ---
    print("\n=== Item-level gaps quoted in Results ===")
    gaps = {}
    for i in range(1, 15):
        gh = mean([rec[r][i] for r in hap])
        gb = mean([rec[r][i] for r in bas])
        gaps[i] = gh - gb
    for i, want in PAPER_ITEM_GAPS.items():
        check(f"item {i} gap (haptic - baseline)", gaps[i], want, 0.05)
    ranked = sorted(gaps, key=lambda i: -gaps[i])
    print(f"  ranked largest->smallest: {[(i, round(gaps[i],2)) for i in ranked]}")
    print(f"  three largest are items {ranked[:3]} (paper claims 6, 4, 1)")
    print(f"  smallest is item {ranked[-1]} (paper claims 12)")

    # --- reverse-coding validated empirically ---
    print("\n=== Reverse-coding check: corrected item-total correlations ===")
    print("    (a wrongly flipped item would correlate NEGATIVELY with its subscale)")
    for k, items in SUBSCALES.items():
        if len(items) < 2:
            continue
        for i in items:
            others = [j for j in items if j != i]
            xs = [rec[r][i] for r in range(12)]
            ys = [mean([rec[r][j] for j in others]) for r in range(12)]
            r = pearson(xs, ys)
            flag = "OK " if r > 0 else "FAIL"
            note = " (reverse-coded)" if i in REVERSE else ""
            print(f"  [{flag}] {k} item {i:2d}: r = {r:+.3f}{note}")

    # --- baseline range claim ---
    print("\n=== Other numeric claims ===")
    bmeans = {k: mean([score[i][k] for i in bas]) for k in SUBSCALES}
    print(f"  baseline subscale means {[round(v,2) for v in bmeans.values()]} "
          f"-> paper says 'between 2.2 and 3.0': "
          f"{min(bmeans.values()):.2f}-{max(bmeans.values()):.2f}")
    spd = []
    for p in paired:
        rowh = [i for i, (q, c) in enumerate(ROWS) if q == p and c == "haptic"][0]
        rowb = [i for i, (q, c) in enumerate(ROWS) if q == p and c == "baseline"][0]
        spd.append(score[rowh]["SP"] - score[rowb]["SP"])
    print(f"  SP paired diffs {[round(x,2) for x in spd]} -> paper says "
          f"'between +1.4 and +3.8': {min(spd):+.2f} to {max(spd):+.2f}")
    dzs = [PAPER_TABLE[k][6] for k in PAPER_TABLE]
    print(f"  dz range printed {min(dzs)}-{max(dzs)} -> paper says '2.3 and 3.3'")

    # --- second opinion ---
    print("\n=== Cross-check against scipy ===")
    try:
        from scipy import stats
        for k in ("G1", "SP", "INV", "REAL"):
            pm = {p: {} for p in paired}
            for i, (p, c) in enumerate(ROWS):
                if p in pm:
                    pm[p][c] = score[i][k]
            h = [pm[p]["haptic"] for p in paired]
            b = [pm[p]["baseline"] for p in paired]
            t = stats.ttest_rel(h, b)
            w = stats.wilcoxon(h, b, method="exact")
            print(f"  {k:5s} scipy t={t.statistic:.3f} p={t.pvalue:.5f} | "
                  f"W={w.statistic:.1f} p={w.pvalue:.4f}")
    except Exception as exc:                       # noqa: BLE001
        print(f"  scipy unavailable: {exc}")

    print("\n" + ("ALL CHECKS PASSED" if ok else "SOME CHECKS FAILED"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "../../ipq-results.csv"))
