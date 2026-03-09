#!/usr/bin/env python3
"""Plot non-target-lag features from part2_supervised_matrix.csv.

Produces one PNG per feature group under artifacts/plots/.
Shows a 5000-row window at full 15-min resolution (~52 days).
"""

import os
import sys
import pandas as pd
import matplotlib
matplotlib.use("Agg")  # headless
import matplotlib.pyplot as plt
import matplotlib.dates as mdates
import numpy as np

CSV_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "part2_supervised_matrix.csv")
OUT_DIR  = os.path.join(os.path.dirname(__file__), "..", "artifacts", "plots")

# Feature groups (display_name -> column list)
GROUPS = {
    "1_target_and_weather": {
        "title": "Target & Weather Covariates",
        "cols": ["TargetAtT", "Temperature", "Windspeed", "SolarIrradiation"],
    },
    "2_calendar": {
        "title": "Calendar Features",
        "cols": ["HourOfDay", "DayOfWeek", "IsHoliday"],
    },
    "3_cyclical": {
        "title": "Cyclical Encodings",
        "cols": ["HourSin", "HourCos", "WeekdaySin", "WeekdayCos"],
    },
    "4_lag_raw": {
        "title": "Raw Lag Features",
        "cols": ["TargetLag192", "TargetLag672"],
    },
    "5_lag_rolling": {
        "title": "Rolling-Window Lag Statistics",
        "cols": [
            "TargetLag192Mean16", "TargetLag192Std16",
            "TargetLag192Mean96", "TargetLag192Std96",
            "TargetLag672Mean16", "TargetLag672Std16",
            "TargetLag672Mean96", "TargetLag672Std96",
        ],
    },
}

MAX_PLOT_POINTS = 5000  # rows shown at full resolution


def plot_group(df, group_key, group_info, out_dir):
    cols = [c for c in group_info["cols"] if c in df.columns]
    if not cols:
        print(f"  skipping {group_key}: no matching columns")
        return
    n = len(cols)
    fig, axes = plt.subplots(n, 1, figsize=(18, 3.2 * n), sharex=True)
    if n == 1:
        axes = [axes]

    time = df["anchorUtcTime"]
    for ax, col in zip(axes, cols):
        series = df[col]
        ax.plot(time, series, linewidth=0.5, color="#1f77b4")
        ax.set_ylabel(col, fontsize=9)
        ax.tick_params(labelsize=8)
        ax.grid(True, alpha=0.3)
        vmin, vmax = series.min(), series.max()
        ax.text(0.01, 0.95, f"min={vmin:.1f}  max={vmax:.1f}  n={len(series)}",
                transform=ax.transAxes, fontsize=7, va="top",
                bbox=dict(boxstyle="round,pad=0.2", fc="white", alpha=0.8))

    axes[-1].xaxis.set_major_formatter(mdates.DateFormatter("%Y-%m-%d"))
    axes[-1].xaxis.set_major_locator(mdates.AutoDateLocator())
    fig.autofmt_xdate(rotation=30)
    fig.suptitle(group_info["title"], fontsize=13, y=1.0)
    fig.tight_layout()

    path = os.path.join(out_dir, f"features_{group_key}.png")
    fig.savefig(path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"  saved {path}  ({os.path.getsize(path) / 1024:.0f} KB)")


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    print(f"Reading {CSV_PATH} ...")
    df = pd.read_csv(CSV_PATH, sep=";", low_memory=False)
    df["anchorUtcTime"] = pd.to_datetime(df["anchorUtcTime"])
    print(f"  {len(df)} rows, {len(df.columns)} columns")

    # Take a 5000-row slice at full resolution from the middle of the dataset
    n = len(df)
    start = max(0, n // 2 - MAX_PLOT_POINTS // 2)
    end = min(n, start + MAX_PLOT_POINTS)
    df = df.iloc[start:end].reset_index(drop=True)
    t0 = df["anchorUtcTime"].iloc[0]
    t1 = df["anchorUtcTime"].iloc[-1]
    print(f"  plotting {len(df)} rows at full resolution: {t0} → {t1}")

    # Add train/validation coloring via Split column
    for key, info in GROUPS.items():
        print(f"Plotting {key} ...")
        plot_group(df, key, info, OUT_DIR)

    print("Done.")


if __name__ == "__main__":
    main()
