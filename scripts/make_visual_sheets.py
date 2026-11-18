"""Phase 2.3 helper: draw a fixed-seed random sample of scored items (up to 60) from one or more
converter output roots and compose labelled side-by-side sheets (original | local render).

Usage: python -I make_visual_sheets.py --seed 20261111 --n 60 --out <project root>\\out\\verify <outroot> [<outroot> ...]
Writes <out>\\visual_sample.csv (id, document, sha256, paragraph_index, ssim, latex, original, render)
and <out>\\sheets\\sheet_XX.png (6 pairs per sheet). Classification is done by a reviewer afterwards
into visual_check.csv.
"""
import argparse, csv, glob, os, random
from PIL import Image, ImageDraw, ImageFont


def rows_from(outroot):
    for rep in glob.glob(os.path.join(outroot, "*", "report.csv")):
        docdir = os.path.dirname(rep)
        work = os.path.join(outroot, "_work", os.path.basename(docdir))
        with open(rep, encoding="utf-8-sig", newline="") as f:
            for r in csv.DictReader(f):
                if not r["score"]:
                    continue
                sha = r["image_sha256"]
                orig = next(iter(glob.glob(os.path.join(work, "img", sha + ".*"))), None)
                rend = next(iter(sorted(glob.glob(os.path.join(work, "render", sha + "_*.png")))), None)
                if orig and rend:
                    yield dict(document=r["document"], sha256=sha, paragraph_index=r["paragraph_index"],
                               ssim=float(r["score"]), latex=r["latex"], original=orig, render=rend)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, required=True)
    ap.add_argument("--n", type=int, default=60)
    ap.add_argument("--out", required=True)
    ap.add_argument("roots", nargs="+")
    a = ap.parse_args()
    items, seen = [], set()
    for root in a.roots:
        for r in rows_from(root):
            key = (r["document"], r["sha256"])  # one item per distinct image per document
            if key not in seen:
                seen.add(key)
                items.append(r)
    items.sort(key=lambda r: (r["document"], r["sha256"]))
    rnd = random.Random(a.seed)
    sample = rnd.sample(items, min(a.n, len(items)))
    os.makedirs(os.path.join(a.out, "sheets"), exist_ok=True)
    with open(os.path.join(a.out, "visual_sample.csv"), "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f)
        w.writerow(["id", "document", "sha256", "paragraph_index", "ssim", "latex", "original", "render"])
        for i, r in enumerate(sample, 1):
            w.writerow([i, r["document"], r["sha256"], r["paragraph_index"], f'{r["ssim"]:.4f}', r["latex"], r["original"], r["render"]])
    try:
        font = ImageFont.truetype("arial.ttf", 16)
    except OSError:
        font = ImageFont.load_default()
    per = 6
    for s in range(0, len(sample), per):
        chunk = sample[s:s + per]
        rows = []
        for i, r in enumerate(chunk, s + 1):
            o = Image.open(r["original"]).convert("RGB")
            g = Image.open(r["render"]).convert("RGB")
            scale = min(1.0, 560 / max(o.width, 1), 140 / max(o.height, 1))
            o = o.resize((max(1, int(o.width * scale)), max(1, int(o.height * scale))))
            g = g.resize((max(1, int(g.width * scale)), max(1, int(g.height * scale))))
            h = max(o.height, g.height) + 30
            row = Image.new("RGB", (1200, h), "white")
            d = ImageDraw.Draw(row)
            d.text((6, 4), f"#{i}  ssim={r['ssim']:.3f}  {r['document'][:40]}", fill=(180, 0, 0), font=font)
            row.paste(o, (10, 26))
            row.paste(g, (620, 26))
            d.line([(605, 0), (605, h)], fill=(160, 160, 160), width=2)
            rows.append(row)
        sheet = Image.new("RGB", (1200, sum(r.height for r in rows) + 4 * len(rows)), (200, 200, 200))
        y = 0
        for r in rows:
            sheet.paste(r, (0, y)); y += r.height + 4
        sheet.save(os.path.join(a.out, "sheets", f"sheet_{s // per + 1:02d}.png"))
    print(f"population={len(items)} sample={len(sample)} seed={a.seed}")


if __name__ == "__main__":
    main()
