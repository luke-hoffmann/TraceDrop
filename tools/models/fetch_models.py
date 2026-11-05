"""Download the AI models listed in models.json into ../../models, verify them, and print their sha256.

Usage: python fetch_models.py [--models-dir DIR] [--only NAME] [--no-smoke]

Every model is optional: one that can't be downloaded or doesn't pass the check is skipped with a warning
and Tracedrop falls back to its classic method for that step. A model whose sha256 is pinned in models.json
must match exactly. The smoke test loads each model with onnxruntime (if installed) and runs one inference
on a blank picture, so a wrong or truncated file never gets bundled.
"""
import argparse
import hashlib
import json
import os
import sys
import tempfile
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def download(url, target):
    tmp = target + ".part"
    request = urllib.request.Request(url, headers={"User-Agent": "tracedrop-build"})
    with urllib.request.urlopen(request, timeout=120) as response, open(tmp, "wb") as out:
        total = int(response.headers.get("Content-Length") or 0)
        done = 0
        while True:
            chunk = response.read(1 << 20)
            if not chunk:
                break
            out.write(chunk)
            done += len(chunk)
            if total:
                print(f"\r  {done / 1e6:7.1f} / {total / 1e6:.1f} MB", end="", flush=True)
    print()
    os.replace(tmp, target)


def smoke(path, shape):
    try:
        import numpy as np
        import onnxruntime as ort
    except ImportError:
        print("  (onnxruntime not installed: skipping the smoke test)")
        return True
    session = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    inp = session.get_inputs()[0]
    dims = [d if isinstance(d, int) and d > 0 else s for d, s in zip(inp.shape, shape)]
    outputs = session.run(None, {inp.name: np.full(dims, 0.5, dtype=np.float32)})
    print(f"  smoke test: input {inp.name} {dims} -> outputs {[o.shape for o in outputs]}")
    return all(o.size > 0 for o in outputs)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--models-dir", default=os.path.join(ROOT, "models"))
    parser.add_argument("--only")
    parser.add_argument("--no-smoke", action="store_true")
    args = parser.parse_args()

    with open(os.path.join(HERE, "models.json"), encoding="utf-8") as f:
        manifest = json.load(f)
    os.makedirs(args.models_dir, exist_ok=True)

    missing = []
    for model in manifest["models"]:
        name = model["file"]
        if args.only and name != args.only:
            continue
        target = os.path.join(args.models_dir, name)
        pinned = model.get("sha256") or ""
        print(f"{name}: {model['purpose']} ({model['license']})")

        if os.path.exists(target) and (not pinned or sha256(target) == pinned):
            print(f"  present, sha256 {sha256(target)}")
            continue
        if model.get("committed"):
            print("  expected in the repository but missing")
            missing.append(name)
            continue

        ok = False
        for url in model.get("urls", []):
            try:
                print(f"  downloading {url}")
                download(url, target)
                digest = sha256(target)
                if pinned and digest != pinned:
                    print(f"  sha256 mismatch: got {digest}, expected {pinned}")
                    os.remove(target)
                    continue
                if not args.no_smoke and "smoke" in model and not smoke(target, model["smoke"]["input"]):
                    print("  smoke test failed")
                    os.remove(target)
                    continue
                print(f"  ok, sha256 {digest}")
                ok = True
                break
            except Exception as ex:  # network errors, bad files: try the next URL
                print(f"  failed: {ex}")
                if os.path.exists(target):
                    os.remove(target)
        if not ok:
            missing.append(name)
            print("  WARNING: not available; Tracedrop will use its classic method for this step")

    if missing:
        print("Missing models: " + ", ".join(missing))
    return 0


if __name__ == "__main__":
    sys.exit(main())
