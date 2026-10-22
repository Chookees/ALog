#!/usr/bin/env python3
"""Merge ALog comparison results.

Reads every <results>/<agent>-<ts>/raw.csv (directories starting with "_" such as _smoke are skipped unless
--include-underscore) and writes:
  merged.csv   all rows of all runs, fixed columns plus "source" (the run directory)
  summary.csv  valid rows only, grouped
                 scope=agent   per runner x framework x mode x scenario
                 scope=overall per framework x mode x scenario across all runners
               with count, mean, stddev (sample), min, max for every metric
Invalid and n/a rows are counted (invalid, n_a) but never enter the statistics.
Ranking: ALog sync is an extra row (decided 2026-10-22). It gets ranked=no and is excluded from
rank_total_ms. rank_total_ms orders the ranked rows of one scope/runner/scenario by total_ms_mean (1 = lowest);
only groups with valid rows get a rank. Use only ranked=yes rows for the evaluation.
The win rule (>= 5 % better than every competitor, on the runner and the majority of environments) is applied
in the evaluation, not here.
Standard library only.
"""
import argparse
import csv
import os
import statistics
import sys

COLUMNS = ["runner", "run", "framework", "version", "scenario", "mode", "n", "total_ms", "cpu_ms", "cpu_pct",
           "peak_ws_mb", "alloc_bytes_per_op", "mean_ns_per_op", "events_per_s", "valid"]
METRICS = ["total_ms", "cpu_ms", "cpu_pct", "peak_ws_mb", "alloc_bytes_per_op", "mean_ns_per_op", "events_per_s"]
# (framework, mode) pairs that are reported but never ranked or counted as a win; Adapters.cs ILib.Ranked agrees.
UNRANKED = {("ALog", "sync")}
SCENARIO_ORDER = {s: i for i, s in enumerate(["T1", "T2", "T7", "T3", "T4", "T5", "T6"])}


def read_runs(root, include_underscore):
    rows = []
    for name in sorted(os.listdir(root)):
        path = os.path.join(root, name, "raw.csv")
        if not os.path.isfile(path) or (name.startswith("_") and not include_underscore):
            continue
        with open(path, newline="", encoding="utf-8") as f:
            reader = csv.DictReader(f)
            if reader.fieldnames != COLUMNS:
                sys.exit(f"{path}: columns {reader.fieldnames} differ from the fixed column list {COLUMNS}")
            for r in reader:
                r["source"] = name
                rows.append(r)
    return rows


def stats(values):
    if not values:
        return {"mean": "", "stddev": "", "min": "", "max": ""}
    return {
        "mean": round(statistics.fmean(values), 4),
        "stddev": round(statistics.stdev(values), 4) if len(values) > 1 else 0.0,
        "min": round(min(values), 4),
        "max": round(max(values), 4),
    }


def summarize(rows, key_fields, scope):
    groups = {}
    for r in rows:
        groups.setdefault(tuple(r[k] for k in key_fields), []).append(r)
    out = []
    for key, items in groups.items():
        valid = [r for r in items if r["valid"] == "true"]
        line = {"scope": scope, "runner": key[0] if scope == "agent" else "*"}
        k = dict(zip(key_fields, key))
        line.update({"framework": k["framework"], "mode": k["mode"], "scenario": k["scenario"],
                     "versions": " ".join(sorted({r["version"] for r in items})),
                     "n": " ".join(sorted({r["n"] for r in items})),
                     "ranked": "no" if (k["framework"], k["mode"]) in UNRANKED else "yes",
                     "rank_total_ms": "",
                     "runners": len({r["runner"] for r in valid}),
                     "count_valid": len(valid),
                     "invalid": sum(1 for r in items if r["valid"] == "false"),
                     "n_a": sum(1 for r in items if r["valid"] == "n/a")})
        for m in METRICS:
            s = stats([float(r[m]) for r in valid if r[m] != ""])
            for stat, v in s.items():
                line[f"{m}_{stat}"] = v
        out.append(line)
    by_cell = {}
    for line in out:
        if line["ranked"] == "yes" and line["total_ms_mean"] != "":
            by_cell.setdefault((line["runner"], line["scenario"]), []).append(line)
    for lines in by_cell.values():
        for i, line in enumerate(sorted(lines, key=lambda l: l["total_ms_mean"]), start=1):
            line["rank_total_ms"] = i
    out.sort(key=lambda l: (l["scope"] != "agent", l["runner"], SCENARIO_ORDER.get(l["scenario"], 99),
                            l["framework"], l["mode"]))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--results", required=True, help="results root containing <agent>-<ts>/raw.csv")
    ap.add_argument("--out", help="output directory (default: the results root)")
    ap.add_argument("--include-underscore", action="store_true", help="also read _smoke and other _* directories")
    a = ap.parse_args()
    rows = read_runs(a.results, a.include_underscore)
    if not rows:
        sys.exit(f"no raw.csv under {a.results}")
    out = a.out or a.results
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, "merged.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=COLUMNS + ["source"], lineterminator="\n")
        w.writeheader()
        w.writerows(rows)
    summary = summarize(rows, ["runner", "framework", "mode", "scenario"], "agent")
    summary += summarize(rows, ["framework", "mode", "scenario"], "overall")
    fields = ["scope", "runner", "framework", "mode", "scenario", "versions", "n", "ranked", "rank_total_ms", "runners", "count_valid",
              "invalid", "n_a"] + [f"{m}_{s}" for m in METRICS for s in ("mean", "stddev", "min", "max")]
    with open(os.path.join(out, "summary.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields, lineterminator="\n")
        w.writeheader()
        w.writerows(summary)
    print(f"{len(rows)} rows from {len({r['source'] for r in rows})} run directories -> "
          f"{os.path.join(out, 'merged.csv')}, {os.path.join(out, 'summary.csv')}")


if __name__ == "__main__":
    main()
