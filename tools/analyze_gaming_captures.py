"""Analyze existing, user-captured frame-time CSVs. Does not launch or inspect any process."""
import argparse
import csv
import json
import hashlib
import math
import statistics
from pathlib import Path


def percentile(values, fraction):
    values = sorted(values)
    index = (len(values) - 1) * fraction
    lower = math.floor(index)
    return values[lower] + (values[math.ceil(index)] - values[lower]) * (index - lower)


def metrics(values):
    if len(values) < 100 or any(not math.isfinite(v) or v <= 0 for v in values):
        raise ValueError("Need at least 100 positive, finite frame times in milliseconds; invalid rows are not silently discarded.")
    slowest = sorted(values, reverse=True)[:math.ceil(len(values) * .01)]
    return {"frames": len(values), "durationSeconds": sum(values) / 1000,
            "averageFps": 1000 / statistics.mean(values), "onePercentLowFps": 1000 / statistics.mean(slowest),
            "medianFrameMs": statistics.median(values), "p95FrameMs": percentile(values, .95), "p99FrameMs": percentile(values, .99)}


def analyze(manifest_path):
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if not manifest.get("conditions") or not manifest.get("captureTool"):
        raise ValueError("Record capture tool/version and matched conditions in the manifest.")
    runs = []
    seen = set()
    for item in manifest["runs"]:
        if item["condition"] not in {"closed", "running", "minimized"}:
            raise ValueError("Foregrounding Project Legion changes game focus and is not a matched gameplay condition.")
        path = (manifest_path.parent / item["file"]).resolve()
        if path in seen:
            raise ValueError("A capture file cannot be reused as independent repeated runs.")
        seen.add(path)
        with path.open(encoding="utf-8-sig", newline="") as stream:
            reader = csv.DictReader(stream)
            column = item.get("column", "MsBetweenPresents")
            if column not in (reader.fieldnames or []):
                raise ValueError(f"Missing explicit millisecond column {column}: {path}")
            rows = [row for row in reader if not item.get("application") or row.get("Application", "").casefold() == item["application"].casefold()]
            if item.get("swapChain"):
                rows = [row for row in rows if row.get("SwapChainAddress") == item["swapChain"]]
            if len({row.get("SwapChainAddress") for row in rows}) > 1:
                raise ValueError("Multiple swap chains were captured; explicitly select the gameplay swap chain.")
            values = [float(row[column]) for row in rows]
        result = metrics(values)
        if result["durationSeconds"] < 30:
            raise ValueError("Use at least 30 seconds of matched gameplay per run; 60 seconds is recommended.")
        runs.append({"file": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "condition": item["condition"], **result})
    groups = {}
    for name in sorted({run["condition"] for run in runs}):
        selected = [run for run in runs if run["condition"] == name]
        if len(selected) < 3:
            raise ValueError("Use at least three independent runs per condition.")
        groups[name] = {key: {"median": statistics.median(run[key] for run in selected),
                              "min": min(run[key] for run in selected), "max": max(run[key] for run in selected)}
                        for key in ("averageFps", "onePercentLowFps", "p95FrameMs", "p99FrameMs")}
    if not {"closed", "running"}.issubset(groups):
        raise ValueError("Both Project Legion closed and running conditions are required.")
    return {"captureTool": manifest["captureTool"], "userDeclaredConditions": manifest["conditions"], "runs": runs, "conditions": groups,
            "runningVersusClosedMedianFpsPercent": 100 * (groups["running"]["averageFps"]["median"] / groups["closed"]["averageFps"]["median"] - 1),
            "limitation": "CSV analysis only; hardware/settings consistency is user-declared. Variability is shown, not an FPS-neutrality or causal claim."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = analyze(args.manifest.resolve())
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)
    print(json.dumps(result["conditions"], indent=2))
