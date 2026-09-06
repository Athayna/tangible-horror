#!/usr/bin/env python3
"""IPQ analysis for the Tangible Horror study.

Input: the Google Forms export _docu/ipq-results.csv (12 playthroughs,
7 participants: 5 played both conditions, 2 only the haptic condition).

Scoring follows the anchors of the administered form (_docu/ipq-form.pdf):
all items on 1-7 scales. Items 1 ("aware of the real world"), 11 ("still
paid attention to the real environment") and 13 ("just perceiving
pictures") are reverse-coded (8 - x). Item 6 needs no recoding because the
form's response anchors already run from "not felt present" (1) to "felt
present" (7).

Subscales (item numbers as printed on the form):
  G1   general presence: item 8
  SP   spatial presence: items 3, 6, 9, 10, 13r
  INV  involvement:      items 1r, 7, 11r, 14
  REAL experienced realism: items 2, 4, 5, 12

Participant assignment was reconstructed from submission timestamps and
confirmed by the experimenters:
  P1  Sep 1 14:12 baseline, 14:29 haptic   (baseline first)
  P2  Sep 1 15:45 haptic only
  P3  Sep 1 16:36 haptic,  16:54 baseline  (haptic first)
  P4  Sep 1 18:14 haptic,  18:25 baseline  (haptic first)
  P5  Sep 2 14:47 haptic,  15:03 baseline  (haptic first)
  P6  Sep 2 16:31 baseline, 16:52 haptic   (baseline first)
  P7  Sep 2 18:26 haptic only

Usage: python3 analyze_ipq.py path/to/ipq-results.csv
"""

import sys

import numpy as np
import pandas as pd
from scipy import stats

REVERSED = [1, 11, 13]
SUBSCALES = {
    "G1": [8],
    "SP": [3, 6, 9, 10, 13],
    "INV": [1, 7, 11, 14],
    "REAL": [2, 4, 5, 12],
}
# row index (0-based, in file order) -> (participant, condition)
ASSIGNMENT = {
    0: ("P1", "baseline"), 1: ("P1", "haptic"),
    2: ("P2", "haptic"),
    3: ("P3", "haptic"), 4: ("P3", "baseline"),
    5: ("P4", "haptic"), 6: ("P4", "baseline"),
    7: ("P5", "haptic"), 8: ("P5", "baseline"),
    9: ("P6", "baseline"), 10: ("P6", "haptic"),
    11: ("P7", "haptic"),
}
PAIRED = ["P1", "P3", "P4", "P5", "P6"]


def load(path):
    raw = pd.read_csv(path)
    items = raw.iloc[:, 2:16].astype(int)
    items.columns = list(range(1, 15))
    for it in REVERSED:
        items[it] = 8 - items[it]
    df = pd.DataFrame(index=items.index)
    df["participant"] = [ASSIGNMENT[i][0] for i in items.index]
    df["condition"] = [ASSIGNMENT[i][1] for i in items.index]
    for name, its in SUBSCALES.items():
        df[name] = items[its].mean(axis=1)
    df["items"] = [items.loc[i].tolist() for i in items.index]
    # sanity: reported condition column must match the assignment
    reported = raw.iloc[:, 1].str.contains("With ").map({True: "haptic", False: "baseline"})
    assert (reported == df["condition"]).all(), "condition mismatch vs. assignment"
    return df, items


def main(path):
    df, items = load(path)

    print("=== Descriptives (all playthroughs: haptic n=7, baseline n=5) ===")
    for name in SUBSCALES:
        for cond in ("haptic", "baseline"):
            vals = df.loc[df.condition == cond, name]
            print(f"{name:5s} {cond:9s} M={vals.mean():.2f} SD={vals.std(ddof=1):.2f}")

    print("\n=== Paired analysis (n=5 complete pairs) ===")
    for name in SUBSCALES:
        wide = df[df.participant.isin(PAIRED)].pivot(
            index="participant", columns="condition", values=name)
        d = wide["haptic"] - wide["baseline"]
        w = stats.wilcoxon(wide["haptic"], wide["baseline"],
                           alternative="two-sided", method="exact")
        t = stats.ttest_rel(wide["haptic"], wide["baseline"])
        dz = d.mean() / d.std(ddof=1)
        print(f"{name:5s} Mdiff={d.mean():+.2f} (SD={d.std(ddof=1):.2f}) "
              f"Wilcoxon W={w.statistic:.1f} p={w.pvalue:.4f} | "
              f"t({len(d)-1})={t.statistic:.2f} p={t.pvalue:.4f} | dz={dz:.2f} | "
              f"diffs={[f'{x:+.2f}' for x in d]}")

    print("\n=== Paired means (n=5 only, for reference) ===")
    for name in SUBSCALES:
        wide = df[df.participant.isin(PAIRED)].pivot(
            index="participant", columns="condition", values=name)
        print(f"{name:5s} haptic M={wide['haptic'].mean():.2f} "
              f"SD={wide['haptic'].std(ddof=1):.2f} | "
              f"baseline M={wide['baseline'].mean():.2f} "
              f"SD={wide['baseline'].std(ddof=1):.2f}")

    print("\n=== Per-item means (recoded, higher = more presence) ===")
    it_df = items.copy()
    it_df["condition"] = df["condition"]
    print(it_df.groupby("condition").mean().round(2).T)

    print("\n=== Order check (paired participants) ===")
    for p in PAIRED:
        rows = df[df.participant == p]
        first = rows.iloc[0].condition
        g = {r.condition: r for r in rows.itertuples()}
        print(f"{p}: first={first:9s} SP diff {g['haptic'].SP - g['baseline'].SP:+.2f} "
              f"REAL diff {g['haptic'].REAL - g['baseline'].REAL:+.2f}")

    df.drop(columns="items").to_csv("ipq_scored.csv", index=False)
    print("\nScored data written to ipq_scored.csv")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "../../ipq-results.csv")
