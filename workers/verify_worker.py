"""Round-trip verification worker.

Protocol: one JSON object per stdin line {"orig": path, "latex": str, "out": path};
one JSON line per request on stdout: {"ok","ssim","render_path","error","seconds"}.

Renderer: LaTeX -> MathML with local Pandoc (tools\\pandoc\\pandoc.exe), MathML laid out by
headless Microsoft Edge (native MathML Core, font "Cambria Math"), screenshot to PNG.
Everything is local: the HTML page is a file:// URL with no external resources.

Score: both images are converted to grayscale, cropped to ink (gray < 200, 2px margin),
both crops are resampled to NORM_H=32 px height with the original's aspect ratio, both get a
Gaussian blur (SIGMA=1.5) to tolerate font/antialiasing differences, then SSIM (skimage,
gaussian weights, 7 px window, data_range 255). Display and inline renderings are both tried;
the higher score is kept. The rendered PNG is also saved at the original pixel size.
"""
import html
import json
import os
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PANDOC = os.environ.get("MATHMORPH_PANDOC", os.path.join(ROOT, "tools", "pandoc", "pandoc.exe"))
EDGE_CANDIDATES = [
    os.environ.get("MATHMORPH_EDGE", ""),
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
]
PROFILE = os.environ.get("MATHMORPH_EDGE_PROFILE", os.path.join(ROOT, "out", "edge_profile"))
NOWIN = 0x08000000 if os.name == "nt" else 0

_real_stdout = sys.stdout
sys.stdout = sys.stderr


def emit(obj):
    _real_stdout.write(json.dumps(obj, ensure_ascii=True) + "\n")
    _real_stdout.flush()


def find_browser():
    for c in EDGE_CANDIDATES:
        if c and os.path.isfile(c):
            return c
    return None


def latex_to_mathml(latex):
    src = "$$" + latex.replace("$", r"\$") + "$$\n"
    p = subprocess.run([PANDOC, "-f", "markdown", "-t", "html", "--mathml"], input=src.encode("utf-8"),
                       capture_output=True, timeout=60, creationflags=NOWIN)
    out = p.stdout.decode("utf-8", "replace")
    i, j = out.find("<math"), out.rfind("</math>")
    if p.returncode != 0 or i < 0 or j < 0:
        raise RuntimeError("pandoc could not convert LaTeX to MathML")
    return out[i:j + 7]


def screenshot(browser, mathml, png, font_px, w, h, tmpdir):
    page = os.path.join(tmpdir, "page.html")
    with open(page, "w", encoding="utf-8") as f:
        f.write("<!doctype html><html><head><meta charset='utf-8'><style>"
                "html,body{margin:0;padding:0;background:#fff;color:#000}"
                f"body{{padding:{font_px}px;white-space:nowrap}}"
                f"math{{font-family:'Cambria Math',math;font-size:{font_px}px;display:inline-block}}"
                "</style></head><body>" + mathml +
                "</body></html>")
    if os.path.exists(png):
        os.remove(png)
    url = "file:///" + page.replace("\\", "/")
    args = [browser, "--headless", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--disable-extensions", "--disable-background-networking", "--disable-component-update",
            "--disable-sync", "--disable-features=Translate,msEdgeSidebarV2",
            f"--user-data-dir={PROFILE}", "--hide-scrollbars", "--force-device-scale-factor=1",
            f"--window-size={w},{h}", f"--screenshot={png}", url]
    subprocess.run(args, capture_output=True, timeout=90, creationflags=NOWIN)
    t = time.time()
    while not os.path.exists(png) and time.time() - t < 10:
        time.sleep(0.1)
    if not os.path.exists(png):
        raise RuntimeError("browser produced no screenshot")


def ink_box(gray, thr=200, margin=2):
    import numpy as np
    ys, xs = np.where(gray < thr)
    if len(xs) == 0:
        return None
    y0, y1 = max(ys.min() - margin, 0), min(ys.max() + margin + 1, gray.shape[0])
    x0, x1 = max(xs.min() - margin, 0), min(xs.max() + margin + 1, gray.shape[1])
    return x0, y0, x1, y1


def load_gray(path):
    from PIL import Image
    import numpy as np
    Image.MAX_IMAGE_PIXELS = 50_000_000
    img = Image.open(path)
    img.load()
    if img.mode in ("RGBA", "LA", "P", "PA"):
        img = img.convert("RGBA")
        bg = Image.new("RGBA", img.size, (255, 255, 255, 255))
        bg.alpha_composite(img)
        img = bg
    return np.asarray(img.convert("L"), dtype=np.uint8)


NORM_H = 32      # both crops are resampled to this height (original's aspect ratio)
SIGMA = 1.5      # Gaussian blur before SSIM, tolerates font/antialiasing differences


def score(orig_gray, rend_gray):
    """SSIM of ink-cropped images normalised to height NORM_H with the original's aspect."""
    import numpy as np
    from PIL import Image
    from scipy.ndimage import gaussian_filter
    from skimage.metrics import structural_similarity
    bo, br = ink_box(orig_gray), ink_box(rend_gray)
    if bo is None or br is None:
        return 0.0
    o = orig_gray[bo[1]:bo[3], bo[0]:bo[2]]
    r = rend_gray[br[1]:br[3], br[0]:br[2]]
    w = max(8, round(NORM_H * o.shape[1] / o.shape[0]))
    O = np.asarray(Image.fromarray(o).resize((w, NORM_H), Image.LANCZOS), dtype=np.float64)
    R = np.asarray(Image.fromarray(r).resize((w, NORM_H), Image.LANCZOS), dtype=np.float64)
    O, R = gaussian_filter(O, SIGMA), gaussian_filter(R, SIGMA)
    return float(structural_similarity(O, R, data_range=255.0, gaussian_weights=True, win_size=7))


def handle(req, browser, tmpdir):
    import numpy as np
    from PIL import Image
    orig = load_gray(req["orig"])
    if ink_box(orig) is None:
        raise RuntimeError("original image is blank")
    mathml_block = latex_to_mathml(req["latex"])
    best = None
    # render in display style and in inline style; keep the better-matching one
    for mathml in (mathml_block, mathml_block.replace('display="block"', 'display="inline"')):
        raw = os.path.join(tmpdir, "raw.png")
        font = 40
        for _ in range(3):
            screenshot(browser, mathml, raw, font, 3200, 900, tmpdir)
            g = load_gray(raw)
            b = ink_box(g)
            if b is None:
                raise RuntimeError("rendered image is blank")
            if b[2] < g.shape[1] - 4 and b[3] < g.shape[0] - 4:
                break
            font = max(10, font // 2)
        s = score(orig, g)
        if best is None or s > best[0]:
            best = (s, g, b)
    s, g, b = best
    # save the render at the original pixel size (content fitted, centred)
    crop = Image.fromarray(g[b[1]:b[3], b[0]:b[2]])
    H, W = orig.shape
    k = min((W - 2) / crop.width, (H - 2) / crop.height)
    k = max(k, 1e-3)
    crop = crop.resize((max(1, int(crop.width * k)), max(1, int(crop.height * k))), Image.LANCZOS)
    canvas = Image.new("L", (W, H), 255)
    canvas.paste(crop, ((W - crop.width) // 2, (H - crop.height) // 2))
    out = req["out"]
    os.makedirs(os.path.dirname(out), exist_ok=True)
    canvas.save(out)
    return s, out


def main():
    browser = find_browser()
    emit({"ready": True, "browser": browser or "", "pandoc": PANDOC if os.path.isfile(PANDOC) else ""})
    sys.stdin.reconfigure(encoding="utf-8")
    tmpdir = tempfile.mkdtemp(prefix="eqv_", dir=os.environ.get("MATHMORPH_TMP") or None)
    for line in sys.stdin:
        line = line.strip().lstrip("\ufeff")
        if not line:
            break
        t = time.time()
        res = {"ok": False, "ssim": -1.0, "render_path": "", "error": ""}
        try:
            req = json.loads(line)
            if not browser:
                raise RuntimeError("no headless browser (Edge/Chrome) found")
            s, out = handle(req, browser, tmpdir)
            res.update(ok=True, ssim=round(s, 4), render_path=out)
        except Exception as e:
            res["error"] = f"{type(e).__name__}: {e}"[:500]
        res["seconds"] = round(time.time() - t, 3)
        emit(res)


if __name__ == "__main__":
    main()
