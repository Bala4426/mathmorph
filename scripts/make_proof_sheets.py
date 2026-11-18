"""Proofreading sheets: every CONVERTED item of one output document, one row per distinct image
(original | render of the recognized LaTeX), with the paragraph indexes where it occurs.
Usage: python -I make_proof_sheets.py <outroot> <doc stem> <dest dir>
Writes <dest>\\proof_items.csv and <dest>\\proof_XX.png (8 rows per sheet)."""
import csv, glob, os, sys
from PIL import Image, ImageDraw, ImageFont

root, stem, dest = sys.argv[1], sys.argv[2], sys.argv[3]
work = os.path.join(root, "_work", stem)
rows = list(csv.DictReader(open(os.path.join(root, stem, "report.csv"), encoding="utf-8-sig", newline="")))
items, order = {}, []
for r in rows:
    if r["status"] != "CONVERTED":
        continue
    sha = r["image_sha256"]
    if sha not in items:
        items[sha] = dict(sha=sha, paras=[], latex=r["latex"], score=r["score"])
        order.append(sha)
    items[sha]["paras"].append(r["paragraph_index"])
os.makedirs(dest, exist_ok=True)
font = ImageFont.truetype("arial.ttf", 15)
with open(os.path.join(dest, "proof_items.csv"), "w", encoding="utf-8", newline="") as f:
    w = csv.writer(f)
    w.writerow(["id", "sha256", "paragraphs", "score", "latex"])
    for i, sha in enumerate(order, 1):
        it = items[sha]
        w.writerow([i, sha, " ".join(it["paras"]), it["score"], it["latex"]])
per = 8
for s in range(0, len(order), per):
    out = []
    for i, sha in enumerate(order[s:s + per], s + 1):
        it = items[sha]
        o = Image.open(glob.glob(os.path.join(work, "img", sha + ".*"))[0]).convert("RGB")
        g = Image.open(sorted(glob.glob(os.path.join(work, "render", sha + "_*.png")))[0]).convert("RGB")
        k = min(1.0, 570 / max(o.width, 1), 120 / max(o.height, 1))
        o = o.resize((max(1, int(o.width * k)), max(1, int(o.height * k))))
        g = g.resize((max(1, int(g.width * k)), max(1, int(g.height * k))))
        h = max(o.height, g.height) + 26
        row = Image.new("RGB", (1200, h), "white")
        d = ImageDraw.Draw(row)
        d.text((6, 3), f"#{i}  para {' '.join(it['paras'][:6])}", fill=(180, 0, 0), font=font)
        row.paste(o, (10, 22)); row.paste(g, (620, 22))
        d.line([(605, 0), (605, h)], fill=(160, 160, 160), width=2)
        out.append(row)
    sheet = Image.new("RGB", (1200, sum(r.height + 4 for r in out)), (200, 200, 200))
    y = 0
    for r in out:
        sheet.paste(r, (0, y)); y += r.height + 4
    sheet.save(os.path.join(dest, f"proof_{s // per + 1:02d}.png"))
print(f"converted rows={sum(len(items[s]['paras']) for s in order)} distinct={len(order)} sheets={(len(order) + per - 1) // per}")
