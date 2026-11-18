"""pix2text recognition worker.

Protocol: reads one image path per line from stdin; writes exactly one JSON line per
input line to stdout: {"path","ok","latex","error", "seconds", "width","height",
"colorful_fraction","midtone_fraction"}. Model is loaded once. All logs go to stderr.
An empty line or EOF ends the worker. Runs fully offline once models are cached.
"""
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
MODEL_ROOT = os.environ.get("MATHMORPH_MODEL_ROOT", os.path.join(ROOT, "tools", "models"))
os.environ.setdefault("YOLO_CONFIG_DIR", os.path.join(MODEL_ROOT, "ultralytics"))
os.environ.setdefault("HF_HOME", os.path.join(MODEL_ROOT, "hf"))
os.environ.setdefault("CNSTD_HOME", os.path.join(MODEL_ROOT, "cnstd"))
os.environ.setdefault("CNOCR_HOME", os.path.join(MODEL_ROOT, "cnocr"))
os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")
os.environ.setdefault("WANDB_MODE", "disabled")

# keep stdout clean for the protocol: everything printed by libraries goes to stderr
_real_stdout = sys.stdout
sys.stdout = sys.stderr


def emit(obj):
    _real_stdout.write(json.dumps(obj, ensure_ascii=True) + "\n")
    _real_stdout.flush()


def image_stats(img):
    """Cheap content statistics used by the engine's logo/figure heuristic."""
    import numpy as np
    rgb = np.asarray(img.convert("RGB"), dtype=np.float32)
    mx = rgb.max(axis=2)
    mn = rgb.min(axis=2)
    sat = (mx - mn) / np.maximum(mx, 1.0)
    colorful = float(((sat > 0.35) & (mx > 60)).mean())
    gray = rgb.mean(axis=2)
    midtone = float(((gray > 70) & (gray < 190)).mean())
    return colorful, midtone


def load_image(path):
    from PIL import Image
    Image.MAX_IMAGE_PIXELS = 50_000_000  # untrusted input: refuse decompression bombs
    img = Image.open(path)
    img.load()
    if img.mode in ("RGBA", "LA", "P", "PA"):
        img = img.convert("RGBA")
        bg = Image.new("RGBA", img.size, (255, 255, 255, 255))
        bg.alpha_composite(img)
        img = bg
    return img.convert("RGB")


def main():
    from pix2text import LatexOCR
    import pix2text.__version__ as v
    t0 = time.time()
    ocr = LatexOCR(model_name="mfr-1.5", model_backend="onnx", device="cpu",
                   root=os.path.join(MODEL_ROOT, "pix2text"))
    sys.stderr.write(f"[worker] pix2text {v.__version__} model loaded in {time.time() - t0:.1f}s\n")
    sys.stderr.flush()
    emit({"ready": True, "pix2text": v.__version__, "load_seconds": round(time.time() - t0, 2)})
    sys.stdin.reconfigure(encoding="utf-8")
    for line in sys.stdin:
        path = line.strip().lstrip("\ufeff")
        if not path:
            break
        res = {"path": path, "ok": False, "latex": "", "error": ""}
        t = time.time()
        try:
            img = load_image(path)
            res["width"], res["height"] = img.size
            res["colorful_fraction"], res["midtone_fraction"] = image_stats(img)
            out = ocr.recognize(img)
            latex = out.get("text", "") if isinstance(out, dict) else str(out)
            latex = (latex or "").strip()
            if latex:
                res["ok"] = True
                res["latex"] = latex
            else:
                res["error"] = "empty result"
        except Exception as e:  # report and continue
            res["error"] = f"{type(e).__name__}: {e}"[:500]
        res["seconds"] = round(time.time() - t, 3)
        emit(res)


if __name__ == "__main__":
    main()
