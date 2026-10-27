# ALog comparison-bench evaluation

Generated 2026-10-27 from 5 GitHub Actions runs and 9 runs on a separate Linux machine (runs with pauses marked as suspect; no verdict changes without them).

## Runs used

| environment | sub-env (owner) | folders | process runs |
|---|---|---|---|
| runner | runner | 1 | 5 |
| box | architecture-designer | 4 | 20 |
| box | repo-manager | 5 | 25 |

Box majority = at least 2 of 2 owners.

## Rules applied

- Ratio = ALog-async / competitor, per process run (same folder, same run index), geometric mean per environment / owner. < 1 = ALog better (all ranked metrics: lower is better).
- Pair WIN: ratio <= 0.95 on runner AND on >= majority of box owners; pair LOSS: ratio >= 1.05 on runner AND majority of box owners; else TIE. Row WIN needs WIN vs every competitor; row LOSS if any pair is LOSS.
- time/op = mean_ns_per_op (= total_ms·10⁶/N, incl. flush+dispose; same ratio as total_ms and inverse of events/s). alloc/op from details.jsonl `alloc_bytes`/n (exact; raw.csv rounds T5 to 0.00/0.01). CPU % is reported, not ranked.
- ALog sync is not ranked. log4net T2/T7 are n/a (competitor set there = NLog, Serilog).
- BDN: BEN-14 (all runs >= 5 % better and 99.9 % CIs separated; alloc has no CI), BEN-15 (0 ns invalid, T5 only from process, any logger median < 2 ns in any run -> EXCLUDED, shown as `< 2 ns`).

## Process verdicts (per task × metric)

| Task | Metric | Verdict | vs NLog | vs Serilog | vs log4net |
|---|---|---|---|---|---|
| T1 | time/op | **WIN** | WIN R 0.59 / box 0.64 0.64 | WIN R 0.42 / box 0.38 0.37 | WIN R 0.43 / box 0.37 0.38 |
| T1 | CPU time | **WIN** | WIN R 0.54 / box 0.63 0.64 | WIN R 0.29 / box 0.28 0.27 | WIN R 0.55 / box 0.50 0.52 |
| T1 | peak WS | **WIN** | WIN R 0.86 / box 0.93 0.93 | WIN R 0.86 / box 0.92 0.93 | WIN R 0.86 / box 0.92 0.92 |
| T1 | alloc/op | **WIN** | WIN R 0.79 / box 0.79 0.79 | WIN R 0.22 / box 0.25 0.26 | WIN R 0.17 / box 0.17 0.17 |
| T1 | CPU % | **INFO (not ranked)** |  R 0.92 / box 0.99 0.99 |  R 0.70 / box 0.73 0.73 |  R 1.28 / box 1.35 1.37 |
| T2 | time/op | **WIN** | WIN R 0.80 / box 0.85 0.86 | WIN R 0.58 / box 0.57 0.55 | n/a |
| T2 | CPU time | **WIN** | WIN R 0.83 / box 0.91 0.92 | WIN R 0.49 / box 0.46 0.45 | n/a |
| T2 | peak WS | **TIE** | TIE R 1.04 / box 1.00 1.00 | TIE R 1.00 / box 1.00 1.00 | n/a |
| T2 | alloc/op | **LOSS** | LOSS R 1.38 / box 1.38 1.38 | WIN R 0.75 / box 0.79 0.81 | n/a |
| T2 | CPU % | **INFO (not ranked)** |  R 1.04 / box 1.06 1.07 |  R 0.83 / box 0.80 0.81 | n/a |
| T7 | time/op | **WIN** | WIN R 0.77 / box 0.91 0.91 | WIN R 0.24 / box 0.23 0.22 | n/a |
| T7 | CPU time | **WIN** | WIN R 0.61 / box 0.72 0.72 | WIN R 0.18 / box 0.18 0.18 | n/a |
| T7 | peak WS | **TIE** | TIE R 0.92 / box 0.95 0.96 | TIE R 0.83 / box 0.98 0.98 | n/a |
| T7 | alloc/op | **TIE** | TIE R 1.05 / box 1.05 1.05 | WIN R 0.14 / box 0.14 0.14 | n/a |
| T7 | CPU % | **INFO (not ranked)** |  R 0.80 / box 0.79 0.79 |  R 0.76 / box 0.80 0.80 | n/a |
| T3 | time/op | **WIN** | WIN R 0.78 / box 0.50 0.41 | WIN R 0.73 / box 0.61 0.60 | WIN R 0.70 / box 0.58 0.56 |
| T3 | CPU time | **WIN** | WIN R 0.73 / box 0.50 0.40 | WIN R 0.52 / box 0.47 0.47 | WIN R 0.92 / box 0.77 0.76 |
| T3 | peak WS | **TIE** | WIN R 0.83 / box 0.87 0.87 | WIN R 0.87 / box 0.87 0.88 | TIE R 1.04 / box 1.05 1.05 |
| T3 | alloc/op | **LOSS** | LOSS R 1.24 / box 1.07 1.07 | TIE R 1.05 / box 0.91 0.91 | TIE R 0.96 / box 0.83 0.83 |
| T3 | CPU % | **INFO (not ranked)** |  R 0.93 / box 1.01 0.98 |  R 0.71 / box 0.77 0.77 |  R 1.31 / box 1.33 1.34 |
| T4 | time/op | **WIN** | WIN R 0.53 / box 0.67 0.68 | WIN R 0.28 / box 0.20 0.26 | WIN R 0.43 / box 0.34 0.32 |
| T4 | CPU time | **WIN** | WIN R 0.57 / box 0.90 0.93 | WIN R 0.22 / box 0.28 0.37 | WIN R 0.54 / box 0.79 0.74 |
| T4 | peak WS | **WIN** | WIN R 0.88 / box 0.93 0.93 | WIN R 0.87 / box 0.92 0.92 | WIN R 0.86 / box 0.92 0.92 |
| T4 | alloc/op | **WIN** | WIN R 0.79 / box 0.79 0.79 | WIN R 0.23 / box 0.24 0.24 | WIN R 0.17 / box 0.17 0.17 |
| T4 | CPU % | **INFO (not ranked)** |  R 1.08 / box 1.34 1.37 |  R 0.79 / box 1.42 1.42 |  R 1.26 / box 2.30 2.31 |
| T5 | time/op | **TIE** | TIE R 0.99 / box 0.91 0.88 | WIN R 0.27 / box 0.22 0.22 | WIN R 0.11 / box 0.11 0.11 |
| T5 | CPU time | **TIE** | TIE R 1.11 / box 0.92 0.92 | WIN R 0.31 / box 0.23 0.23 | WIN R 0.13 / box 0.11 0.11 |
| T5 | peak WS | **WIN** | WIN R 0.83 / box 0.81 0.81 | WIN R 0.84 / box 0.81 0.81 | WIN R 0.82 / box 0.80 0.81 |
| T5 | alloc/op | **TIE** | WIN R 0.48 / box 0.10 0.13 | TIE R 1.16 / box 0.22 0.31 | WIN R 0.58 / box 0.11 0.16 |
| T5 | CPU % | **INFO (not ranked)** |  R 1.13 / box 1.02 1.04 |  R 1.16 / box 1.04 1.06 |  R 1.15 / box 1.03 1.05 |
| T6 | time/op | **WIN** | WIN R 0.25 / box 0.24 0.24 | WIN R 0.20 / box 0.21 0.20 | WIN R 0.27 / box 0.27 0.27 |
| T6 | CPU time | **WIN** | WIN R 0.32 / box 0.30 0.30 | WIN R 0.27 / box 0.27 0.26 | WIN R 0.36 / box 0.36 0.36 |
| T6 | peak WS | **WIN** | WIN R 0.92 / box 0.92 0.92 | WIN R 0.82 / box 0.81 0.81 | WIN R 0.81 / box 0.80 0.80 |
| T6 | alloc/op | **WIN** | WIN R 0.09 / box 0.07 0.08 | WIN R 0.12 / box 0.11 0.11 | WIN R 0.22 / box 0.18 0.19 |
| T6 | CPU % | **INFO (not ranked)** |  R 1.24 / box 1.24 1.24 |  R 1.31 / box 1.29 1.29 |  R 1.33 / box 1.32 1.32 |

Ratio cells: runner geo-mean / box geo-mean per owner (architecture-designer, repo-manager).

## Process absolute values (mean ± sd over all runs of the environment)

| Task | Metric | Env | ALog async | NLog | Serilog | log4net |
|---|---|---|---|---|---|---|
| T1 | time/op (ns) | runner | 599.934 ± 11.186 | 1,020 ± 45.362 | 1,446 ± 86.129 | 1,403 ± 20.795 |
| T1 | time/op (ns) | box | 563.985 ± 73.626 | 876.824 ± 48.005 | 1,491 ± 177.704 | 1,500 ± 114.944 |
| T1 | CPU time (ms) | runner | 868.041 ± 14.865 | 1,612 ± 37.180 | 2,991 ± 169.412 | 1,590 ± 26.440 |
| T1 | CPU time (ms) | box | 828.618 ± 115.003 | 1,300 ± 72.030 | 2,996 ± 320.453 | 1,620 ± 118.895 |
| T1 | peak WS (MB) | runner | 61.042 ± 0.060 | 70.920 ± 1.961 | 71.324 ± 1.747 | 71.044 ± 0.130 |
| T1 | peak WS (MB) | box | 122.385 ± 0.409 | 131.089 ± 0.254 | 132.332 ± 0.990 | 132.736 ± 0.492 |
| T1 | alloc/op (B) | runner | 152.328 ± 0.001 | 193.443 ± 0.583 | 680.094 ± 9.170 | 923.009 ± 0.636 |
| T1 | alloc/op (B) | box | 152.328 ± 0.003 | 193.063 ± 0.592 | 603.187 ± 21.955 | 922.313 ± 1.520 |
| T1 | CPU % (%) | runner | 144.720 ± 3.082 | 158.174 ± 4.936 | 206.946 ± 0.870 | 113.320 ± 0.588 |
| T1 | CPU % (%) | box | 146.894 ± 3.914 | 148.279 ± 2.961 | 201.130 ± 2.639 | 108.047 ± 1.694 |
| T2 | time/op (ns) | runner | 1,050 ± 31.985 | 1,317 ± 69.579 | 1,798 ± 105.330 | n/a |
| T2 | time/op (ns) | box | 966.579 ± 85.712 | 1,127 ± 91.044 | 1,728 ± 232.288 | n/a |
| T2 | CPU time (ms) | runner | 1,752 ± 150.949 | 2,106 ± 143.660 | 3,602 ± 195.746 | n/a |
| T2 | CPU time (ms) | box | 1,550 ± 127.558 | 1,694 ± 127.432 | 3,434 ± 439.340 | n/a |
| T2 | peak WS (MB) | runner | 70.620 ± 0.019 | 68.032 ± 0.100 | 70.626 ± 0.113 | n/a |
| T2 | peak WS (MB) | box | 132.468 ± 0.856 | 132.481 ± 1.431 | 132.709 ± 0.756 | n/a |
| T2 | alloc/op (B) | runner | 631.604 ± 0.014 | 457.182 ± 0.715 | 838.622 ± 29.673 | n/a |
| T2 | alloc/op (B) | box | 631.609 ± 0.008 | 456.950 ± 0.529 | 787.619 ± 24.086 | n/a |
| T2 | CPU % (%) | runner | 166.802 ± 12.707 | 159.840 ± 2.833 | 200.388 ± 1.017 | n/a |
| T2 | CPU % (%) | box | 160.547 ± 7.124 | 150.511 ± 4.378 | 198.879 ± 2.411 | n/a |
| T7 | time/op (ns) | runner | 1,301 ± 58.288 | 1,699 ± 83.303 | 5,451 ± 71.809 | n/a |
| T7 | time/op (ns) | box | 1,215 ± 250.511 | 1,326 ± 128.883 | 5,349 ± 587.332 | n/a |
| T7 | CPU time (ms) | runner | 1,888 ± 71.326 | 3,092 ± 202.917 | 10,453 ± 141.119 | n/a |
| T7 | CPU time (ms) | box | 1,744 ± 274.062 | 2,414 ± 228.212 | 9,608 ± 949.451 | n/a |
| T7 | peak WS (MB) | runner | 68.242 ± 0.129 | 74.044 ± 0.646 | 82.048 ± 0.672 | n/a |
| T7 | peak WS (MB) | box | 130.177 ± 0.423 | 136.156 ± 0.986 | 132.941 ± 1.011 | n/a |
| T7 | alloc/op (B) | runner | 631.611 ± 0.004 | 602.286 ± 1.055 | 4,642 ± 0.488 | n/a |
| T7 | alloc/op (B) | box | 631.611 ± 0.003 | 602.137 ± 1.156 | 4,641 ± 1.927 | n/a |
| T7 | CPU % (%) | runner | 145.132 ± 2.821 | 181.920 ± 4.441 | 191.772 ± 0.110 | n/a |
| T7 | CPU % (%) | box | 144.238 ± 4.895 | 182.131 ± 5.323 | 179.811 ± 2.326 | n/a |
| T3 | time/op (ns) | runner | 22,403 ± 482.846 | 28,831 ± 1,226 | 30,674 ± 1,055 | 31,841 ± 361.323 |
| T3 | time/op (ns) | box | 19,312 ± 2,689 | 1,009,990 ± 3,751,718 | 31,679 ± 3,154 | 33,559 ± 2,077 |
| T3 | CPU time (ms) | runner | 3,286 ± 73.421 | 4,532 ± 104.693 | 6,352 ± 201.968 | 3,560 ± 30.445 |
| T3 | CPU time (ms) | box | 2,797 ± 358.634 | 237,287 ± 929,105 | 5,959 ± 435.434 | 3,643 ± 214.738 |
| T3 | peak WS (MB) | runner | 77.722 ± 0.823 | 93.670 ± 2.048 | 89.708 ± 0.658 | 74.378 ± 0.134 |
| T3 | peak WS (MB) | box | 143.140 ± 1.934 | 164.641 ± 0.987 | 163.847 ± 1.186 | 136.669 ± 0.162 |
| T3 | alloc/op (B) | runner | 16,422 ± 0.173 | 13,281 ± 0.747 | 15,568 ± 10.001 | 17,030 ± 1.898 |
| T3 | alloc/op (B) | box | 17,123 ± 3.015 | 16,071 ± 0.563 | 18,798 ± 8.513 | 20,715 ± 3.200 |
| T3 | CPU % (%) | runner | 146.688 ± 0.787 | 157.300 ± 4.066 | 207.100 ± 0.804 | 111.824 ± 0.430 |
| T3 | CPU % (%) | box | 145.064 ± 4.402 | 147.526 ± 26.228 | 188.516 ± 4.733 | 108.586 ± 1.831 |
| T4 | time/op (ns) | runner | 719.542 ± 5.896 | 1,362 ± 25.185 | 2,539 ± 37.854 | 1,689 ± 35.614 |
| T4 | time/op (ns) | box | 661.910 ± 78.717 | 983.700 ± 124.046 | 26,596 ± 161,572 | 2,155 ± 1,566 |
| T4 | CPU time (ms) | runner | 1,598 ± 16.355 | 2,802 ± 58.910 | 7,136 ± 105.709 | 2,967 ± 59.727 |
| T4 | CPU time (ms) | box | 2,669 ± 176.409 | 2,913 ± 166.218 | 103,422 ± 646,106 | 3,632 ± 1,475 |
| T4 | peak WS (MB) | runner | 61.218 ± 0.071 | 69.764 ± 0.180 | 70.658 ± 0.084 | 71.340 ± 0.104 |
| T4 | peak WS (MB) | box | 122.681 ± 0.107 | 131.597 ± 0.385 | 132.792 ± 0.293 | 133.233 ± 0.302 |
| T4 | alloc/op (B) | runner | 152.744 ± 0.004 | 193.706 ± 0.000 | 664.143 ± 1.930 | 920.168 ± 0.689 |
| T4 | alloc/op (B) | box | 152.505 ± 0.059 | 193.112 ± 0.729 | 637.488 ± 8.916 | 920.352 ± 1.478 |
| T4 | CPU % (%) | runner | 222.152 ± 1.926 | 205.716 ± 4.000 | 281.124 ± 0.624 | 175.682 ± 1.946 |
| T4 | CPU % (%) | box | 405.583 ± 25.543 | 298.725 ± 24.233 | 285.975 ± 19.221 | 176.163 ± 12.903 |
| T5 | time/op (ns) | runner | 3.474 ± 0.075 | 3.522 ± 0.057 | 12.824 ± 0.429 | 31.474 ± 0.326 |
| T5 | time/op (ns) | box | 2.613 ± 0.758 | 2.884 ± 0.382 | 11.695 ± 0.662 | 23.941 ± 3.736 |
| T5 | CPU time (ms) | runner | 40.625 ± 1.333 | 36.510 ± 0.513 | 129.621 ± 4.392 | 320.121 ± 3.766 |
| T5 | CPU time (ms) | box | 27.793 ± 8.110 | 29.668 ± 3.916 | 117.932 ± 7.019 | 244.316 ± 39.156 |
| T5 | peak WS (MB) | runner | 38.584 ± 0.047 | 46.340 ± 0.051 | 46.152 ± 0.142 | 46.814 ± 0.102 |
| T5 | peak WS (MB) | box | 36.152 ± 0.146 | 44.464 ± 0.097 | 44.453 ± 0.201 | 44.911 ± 0.147 |
| T5 | alloc/op (B) | runner | 0.002 ± 0.000 | 0.004 ± 0.000 | 0.002 ± 0.000 | 0.004 ± 0.000 |
| T5 | alloc/op (B) | box | 0.001 ± 0.001 | 0.004 ± 0.000 | 0.002 ± 0.000 | 0.004 ± 0.000 |
| T5 | CPU % (%) | runner | 116.844 ± 1.440 | 103.708 ± 0.423 | 101.086 ± 0.059 | 101.710 ± 0.197 |
| T5 | CPU % (%) | box | 106.196 ± 3.619 | 102.873 ± 1.008 | 100.828 ± 0.756 | 102.005 ± 1.450 |
| T6 | time/op (ns) | runner | 25,991,580 ± 534,183 | 101,964,220 ± 1,844,031 | 127,608,480 ± 1,007,178 | 94,755,740 ± 2,192,206 |
| T6 | time/op (ns) | box | 20,222,084 ± 3,282,204 | 83,701,158 ± 13,172,276 | 97,388,142 ± 13,326,037 | 73,348,362 ± 10,642,978 |
| T6 | CPU time (ms) | runner | 34.586 ± 0.769 | 109.336 ± 2.107 | 130.039 ± 1.051 | 94.818 ± 2.181 |
| T6 | CPU time (ms) | box | 26.698 ± 4.610 | 89.056 ± 13.775 | 99.242 ± 13.622 | 73.328 ± 10.667 |
| T6 | peak WS (MB) | runner | 37.288 ± 0.038 | 40.400 ± 0.087 | 45.204 ± 0.061 | 45.774 ± 0.102 |
| T6 | peak WS (MB) | box | 35.301 ± 0.251 | 38.547 ± 0.111 | 43.461 ± 0.173 | 43.901 ± 0.146 |
| T6 | alloc/op (B) | runner | 34,712 ± 0.000 | 375,456 ± 0.000 | 278,408 ± 0.000 | 160,872 ± 0.000 |
| T6 | alloc/op (B) | box | 31,230 ± 7,423 | 394,175 ± 867.584 | 278,680 ± 295.990 | 164,100 ± 334.897 |
| T6 | CPU % (%) | runner | 133.066 ± 0.581 | 107.230 ± 0.514 | 101.904 ± 0.046 | 100.066 ± 0.030 |
| T6 | CPU % (%) | box | 131.819 ± 3.870 | 106.435 ± 0.932 | 101.901 ± 0.319 | 99.968 ± 0.100 |

## BenchmarkDotNet cells

| Task | Metric | Verdict | vs NLog | vs Serilog | vs log4net | note |
|---|---|---|---|---|---|---|
| T1Plain | time | **TIE** | TIE gm 0.72 (R 0.91, box 0.70), 6/10 runs ≥5 % & CI-sep | WIN gm 0.33 (R 0.60, box 0.30), 10/10 runs ≥5 % & CI-sep | WIN gm 0.23 (R 0.42, box 0.21), 10/10 runs ≥5 % & CI-sep |  |
| T1Plain | alloc | **WIN** | WIN gm 0.80 (R 0.81, box 0.80), 10/10 runs ≥5 % & CI-sep | WIN gm 0.35 (R 0.36, box 0.35), 10/10 runs ≥5 % & CI-sep | WIN gm 0.15 (R 0.14, box 0.15), 10/10 runs ≥5 % & CI-sep |  |
| T2Structured | time | **TIE** | TIE gm 1.41 (R 1.56, box 1.39), 2/10 runs ≥5 % & CI-sep | TIE gm 0.45 (R 1.09, box 0.40), 8/10 runs ≥5 % & CI-sep | n/a |  |
| T2Structured | alloc | **LOSS** | LOSS gm 2.11 (R 2.43, box 2.07), 0/10 runs ≥5 % & CI-sep | TIE gm 1.05 (R 0.92, box 1.07), 2/10 runs ≥5 % & CI-sep | n/a |  |
| T3Exception | time | **LOSS** | LOSS gm 2.17 (R 3.04, box 2.09), 0/10 runs ≥5 % & CI-sep | TIE gm 0.49 (R 0.83, box 0.46), 9/10 runs ≥5 % & CI-sep | WIN gm 0.01 (R 0.01, box 0.01), 10/10 runs ≥5 % & CI-sep |  |
| T3Exception | alloc | **TIE** | TIE gm 1.19 (R 1.33, box 1.18), 4/10 runs ≥5 % & CI-sep | WIN gm 0.37 (R 0.43, box 0.36), 10/10 runs ≥5 % & CI-sep | WIN gm 0.01 (R 0.01, box 0.01), 10/10 runs ≥5 % & CI-sep |  |
| T4Threads | time | **TIE** | TIE gm 0.91 (R 0.64, box 0.94), 4/10 runs ≥5 % & CI-sep | WIN gm 0.44 (R 0.30, box 0.46), 10/10 runs ≥5 % & CI-sep | WIN gm 0.19 (R 0.14, box 0.19), 10/10 runs ≥5 % & CI-sep |  |
| T4Threads | alloc | **WIN** | WIN gm 0.86 (R 0.84, box 0.86), 10/10 runs ≥5 % & CI-sep | WIN gm 0.39 (R 0.38, box 0.39), 10/10 runs ≥5 % & CI-sep | WIN gm 0.15 (R 0.15, box 0.15), 10/10 runs ≥5 % & CI-sep |  |
| T5Filtered | time | **EXCLUDED** | < 2 ns (gm ratio 0.73) | < 2 ns (gm ratio 0.97) | < 2 ns (gm ratio 0.17) | EXCLUDED (BEN-15: T5Filtered ranked from process only) | 0 ns invalid in 1 value(s); 0 ns at: developer-20261027T134634Z:nlog |
| T5Filtered | alloc | **EXCLUDED** | – gm 1.00 (R 1.00, box 1.00), 0/10 runs ≥5 % & CI-sep | – gm 1.00 (R 1.00, box 1.00), 0/10 runs ≥5 % & CI-sep | – gm 1.00 (R 1.00, box 1.00), 0/10 runs ≥5 % & CI-sep | EXCLUDED (BEN-15: T5Filtered ranked from process only) |
| T7Json | time | **TIE** | TIE gm 0.38 (R 0.35, box 0.38), 8/10 runs ≥5 % & CI-sep | TIE gm 0.71 (R 0.45, box 0.74), 5/10 runs ≥5 % & CI-sep | n/a |  |
| T7Json | alloc | **TIE** | TIE gm 0.91 (R 0.88, box 0.92), 8/10 runs ≥5 % & CI-sep | WIN gm 0.82 (R 0.83, box 0.82), 10/10 runs ≥5 % & CI-sep | n/a |  |

### BDN absolute values (mean of runs; time ns, alloc B)

| Task | Metric | Competitor | ALog runner | comp runner | ALog box (±sd) | comp box (±sd) |
|---|---|---|---|---|---|---|
| T1Plain | time | NLog | 373.28 | 409.58 | 195.78 ± 40.41 | 275.55 ± 41.08 |
| T1Plain | time | Serilog | 373.28 | 619.51 | 195.78 ± 40.41 | 632.04 ± 56.09 |
| T1Plain | time | log4net | 373.28 | 893.86 | 195.78 ± 40.41 | 916.73 ± 124.87 |
| T1Plain | alloc | NLog | 128.00 | 159.00 | 136.78 ± 8.36 | 170.33 ± 15.27 |
| T1Plain | alloc | Serilog | 128.00 | 359.00 | 136.78 ± 8.36 | 388.22 ± 29.29 |
| T1Plain | alloc | log4net | 128.00 | 911.00 | 136.78 ± 8.36 | 911.00 ± 0.00 |
| T2Structured | time | NLog | 1,214 | 777.30 | 395.59 ± 257.15 | 309.48 ± 189.71 |
| T2Structured | time | Serilog | 1,214 | 1,114 | 395.59 ± 257.15 | 858.73 ± 100.08 |
| T2Structured | alloc | NLog | 639.00 | 263.00 | 589.89 ± 19.85 | 287.22 ± 48.14 |
| T2Structured | alloc | Serilog | 639.00 | 697.00 | 589.89 ± 19.85 | 553.78 ± 60.11 |
| T3Exception | time | NLog | 197.15 | 64.75 | 108.69 ± 15.50 | 51.94 ± 7.89 |
| T3Exception | time | Serilog | 197.15 | 236.97 | 108.69 ± 15.50 | 237.49 ± 33.44 |
| T3Exception | time | log4net | 197.15 | 14,098 | 108.69 ± 15.50 | 14,515 ± 974.22 |
| T3Exception | alloc | NLog | 211.00 | 159.00 | 197.67 ± 64.84 | 159.00 ± 0.00 |
| T3Exception | alloc | Serilog | 211.00 | 487.00 | 197.67 ± 64.84 | 520.89 ± 53.49 |
| T3Exception | alloc | log4net | 211.00 | 16,684 | 197.67 ± 64.84 | 20,363 ± 0.00 |
| T4Threads | time | NLog | 174.03 | 273.92 | 260.60 ± 11.40 | 279.79 ± 52.67 |
| T4Threads | time | Serilog | 174.03 | 574.14 | 260.60 ± 11.40 | 583.98 ± 141.43 |
| T4Threads | time | log4net | 174.03 | 1,217 | 260.60 ± 11.40 | 1,379 ± 372.09 |
| T4Threads | alloc | NLog | 134.00 | 160.00 | 140.89 ± 8.07 | 164.22 ± 6.76 |
| T4Threads | alloc | Serilog | 134.00 | 357.00 | 140.89 ± 8.07 | 361.44 ± 9.45 |
| T4Threads | alloc | log4net | 134.00 | 911.00 | 140.89 ± 8.07 | 911.00 ± 0.00 |
| T5Filtered | time | NLog | 11.00 | 0.00 | 1.03 ± 0.19 | 2.96 ± 3.70 |
| T5Filtered | time | Serilog | 11.00 | 1.27 | 1.03 ± 0.19 | 1.35 ± 0.27 |
| T5Filtered | time | log4net | 11.00 | 8.18 | 1.03 ± 0.19 | 7.73 ± 0.74 |
| T5Filtered | alloc | NLog | 0.00 | 0.00 | 0.00 ± 0.00 | 0.00 ± 0.00 |
| T5Filtered | alloc | Serilog | 0.00 | 0.00 | 0.00 ± 0.00 | 0.00 ± 0.00 |
| T5Filtered | alloc | log4net | 0.00 | 0.00 | 0.00 ± 0.00 | 0.00 ± 0.00 |
| T7Json | time | NLog | 293.05 | 836.69 | 493.44 ± 214.67 | 1,363 ± 611.19 |
| T7Json | time | Serilog | 293.05 | 646.35 | 493.44 ± 214.67 | 613.00 ± 59.56 |
| T7Json | alloc | NLog | 575.00 | 652.00 | 588.67 ± 27.60 | 642.56 ± 9.88 |
| T7Json | alloc | Serilog | 575.00 | 696.00 | 588.67 ± 27.60 | 717.78 ± 81.63 |

## Rows eligible for the README table

Process: T1 time/op, T1 CPU time, T1 peak WS, T1 alloc/op, T2 time/op, T2 CPU time, T7 time/op, T7 CPU time, T3 time/op, T3 CPU time, T4 time/op, T4 CPU time, T4 peak WS, T4 alloc/op, T5 peak WS, T6 time/op, T6 CPU time, T6 peak WS, T6 alloc/op

BDN: T1Plain alloc, T4Threads alloc

## Anomalies

- Process outliers (time/op > 2× or < 0.5× the median of the same lib/task/owner): 8
  - `architecture-designer-20261027T171205Z` run 3 ALog/async T5: 7.1 ns/op vs median 2.5 (×2.90)
  - `architecture-designer-20261027T172540Z` run 2 ALog/async T7: 2,722 ns/op vs median 1,138 (×2.39)
  - `architecture-designer-20261027T172540Z` run 5 Serilog/async T4: 1,086,366 ns/op vs median 2,443 (×444.74)
  - `architecture-designer-20261027T184207Z` run 2 NLog/async T3: 15,490,851 ns/op vs median 27,784 (×557.54)
  - `architecture-designer-20261027T184207Z` run 3 ALog/sync T1: 1,400 ns/op vs median 447.0 (×3.13)
  - `repo-manager-20261027T152721Z` run 4 NLog/async T3: 16,781,411 ns/op vs median 28,581 (×587.16)
  - `repo-manager-20261027T152721Z` run 4 log4net/buffered T4: 12,341 ns/op vs median 1,881 (×6.56)
  - `repo-manager-20261027T161838Z` run 2 NLog/async T3: 11,983,511 ns/op vs median 28,581 (×419.29)
- BDN T5Filtered time: 37 median(s) < 2 ns, 1 zero value(s)

## Sensitivity: with vs without suspect runs

Suspect (box paused during the run, load-log gaps of 18 to 51 min): `repo-manager-20261027T134742Z`, `repo-manager-20261027T152721Z`, `repo-manager-20261027T161838Z`, `architecture-designer-20261027T172540Z`, `architecture-designer-20261027T184207Z`.

Not counted: one run (exit 1) in which Serilog T7 hit the 1800 s hang guard while the machine was paused.

- process: no pair verdict changes
- bdn: no pair verdict changes
- README-eligible rows with all runs: Process: T1 time/op, T1 CPU time, T1 peak WS, T1 alloc/op, T2 time/op, T2 CPU time, T7 time/op, T7 CPU time, T3 time/op, T3 CPU time, T4 time/op, T4 CPU time, T4 peak WS, T4 alloc/op, T5 peak WS, T6 time/op, T6 CPU time, T6 peak WS, T6 alloc/op BDN: T1Plain alloc, T4Threads alloc; without suspect runs: Process: T1 time/op, T1 CPU time, T1 peak WS, T1 alloc/op, T2 time/op, T2 CPU time, T7 time/op, T7 CPU time, T3 time/op, T3 CPU time, T4 time/op, T4 CPU time, T4 peak WS, T4 alloc/op, T5 peak WS, T6 time/op, T6 CPU time, T6 peak WS, T6 alloc/op BDN: T1Plain alloc, T4Threads alloc
