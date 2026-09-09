#!/usr/bin/env python3
"""Figures for the Tangible Horror paper (vector PDF, acmart column width).

Usage: python3 make_figures.py path/to/ipq-results.csv
Writes ../figures/ipq_subscales.pdf and ../figures/ipq_items.pdf
"""

import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from analyze_ipq import SUBSCALES, PAIRED, load

HAPTIC = "#2a78d6"    # blue
BASELINE = "#eb6834"  # orange
INK = "#0b0b0b"
MUTED = "#52514e"
GRID = "#e6e5e1"

COL_W = 3.35  # acmart \columnwidth in inches

plt.rcParams.update({
    "font.size": 7.5,
    "font.family": "sans-serif",
    "axes.edgecolor": MUTED,
    "axes.labelcolor": INK,
    "text.color": INK,
    "xtick.color": MUTED,
    "ytick.color": MUTED,
    "pdf.fonttype": 42,
})

NAMES = {"G1": "General presence", "SP": "Spatial presence",
         "INV": "Involvement", "REAL": "Experienced realism"}


def subscale_figure(df, out):
    fig, axes = plt.subplots(2, 2, figsize=(COL_W, 3.1), sharey=True)
    for ax, key in zip(axes.flat, SUBSCALES):
        wide = df[df.participant.isin(PAIRED)].pivot(
            index="participant", columns="condition", values=key)
        solo = df[(~df.participant.isin(PAIRED)) & (df.condition == "haptic")][key]

        for _, row in wide.iterrows():
            ax.plot([0, 1], [row["baseline"], row["haptic"]],
                    color=GRID, lw=1, zorder=1)
            ax.plot(0, row["baseline"], "o", ms=3.5, color=BASELINE, zorder=3)
            ax.plot(1, row["haptic"], "o", ms=3.5, color=HAPTIC, zorder=3)
        # haptic-only participants: open markers, slightly offset
        ax.plot([1.06] * len(solo), solo, "o", ms=3.5, mfc="none",
                mec=HAPTIC, mew=0.9, zorder=3)
        # condition means (all playthroughs) as short horizontal ticks
        for x, cond, c in ((0, "baseline", BASELINE), (1, "haptic", HAPTIC)):
            m = df.loc[df.condition == cond, key].mean()
            ax.plot([x - 0.13, x + 0.13], [m, m], color=c, lw=2, zorder=4,
                    solid_capstyle="butt")
            ax.annotate(f"{m:.1f}", (x, m), textcoords="offset points",
                        xytext=(-16 if x == 0 else 16, -2.5), ha="center",
                        fontsize=6.5, color=MUTED)

        ax.set_title(NAMES[key], fontsize=7.5, pad=3)
        ax.set_xlim(-0.45, 1.5)
        ax.set_ylim(0.8, 7.2)
        ax.set_xticks([0, 1], ["Baseline", "Haptic"])
        ax.set_yticks(range(1, 8))
        ax.tick_params(length=0)
        ax.grid(axis="y", color=GRID, lw=0.5)
        ax.set_axisbelow(True)
        for s in ("top", "right", "left"):
            ax.spines[s].set_visible(False)
        ax.spines["bottom"].set_color(GRID)
    fig.tight_layout(pad=0.4, h_pad=1.0)
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)


def item_figure(items, df, out):
    order = [(k, it) for k in ("G1", "SP", "INV", "REAL") for it in SUBSCALES[k]]
    it_df = items.copy()
    it_df["condition"] = df["condition"].values
    means = it_df.groupby("condition").mean()

    fig, ax = plt.subplots(figsize=(COL_W, 3.4))
    ys = np.arange(len(order))[::-1]
    for y, (scale, it) in zip(ys, order):
        b, h = means.loc["baseline", it], means.loc["haptic", it]
        ax.plot([b, h], [y, y], color=GRID, lw=1.2, zorder=1,
                solid_capstyle="butt")
        ax.plot(b, y, "o", ms=4, color=BASELINE, zorder=3)
        ax.plot(h, y, "o", ms=4, color=HAPTIC, zorder=3)
    labels = [f"{it}{'r' if it in (1, 11, 13) else ''}" for _, it in order]
    ax.set_yticks(ys, labels)
    ax.set_ylim(-0.7, len(order) - 0.3)

    # subscale group separators + labels
    bounds, start = [], 0
    for k in ("G1", "SP", "INV", "REAL"):
        n = len(SUBSCALES[k])
        bounds.append((k, start, start + n - 1))
        start += n
    for k, s, e in bounds:
        top, bot = ys[s] + 0.5, ys[e] - 0.5
        if e < len(order) - 1:
            ax.axhline(bot, color=GRID, lw=0.6)
        ax.text(7.5, (top + bot) / 2, k, va="center", ha="left",
                fontsize=6.5, color=MUTED)

    ax.set_xlim(1, 7.35)
    ax.set_xticks(range(1, 8))
    ax.set_xlabel("Mean item score (1–7, reverse-coded items marked r)",
                  fontsize=7)
    ax.tick_params(length=0)
    ax.grid(axis="x", color=GRID, lw=0.5)
    ax.set_axisbelow(True)
    for s in ("top", "right", "left"):
        ax.spines[s].set_visible(False)
    ax.spines["bottom"].set_color(GRID)
    # direct legend
    ax.text(0.02, 1.02, "Baseline", color=BASELINE, fontsize=7.5,
            transform=ax.transAxes, fontweight="bold")
    ax.text(0.22, 1.02, "Haptic", color=HAPTIC, fontsize=7.5,
            transform=ax.transAxes, fontweight="bold")
    fig.tight_layout(pad=0.4)
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)


if __name__ == "__main__":
    path = sys.argv[1] if len(sys.argv) > 1 else "../../ipq-results.csv"
    df, items = load(path)
    import os
    os.makedirs("../figures", exist_ok=True)
    subscale_figure(df, "../figures/ipq_subscales.pdf")
    item_figure(items, df, "../figures/ipq_items.pdf")
    print("figures written")
