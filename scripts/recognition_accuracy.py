"""Measures recognition accuracy on the synthetic corpus, where the true LaTeX is known.

Matching: expected.json pictures and report.csv rows are paired per document in document order
for part word/document.xml (both lists follow the same order). Metric: "normalized exact match":
both strings with whitespace removed, \\left/\\right, \\, \\; \\! \\quad spacing, \\displaystyle,
and all braces removed, then compared. Also reports mean character-level similarity (difflib ratio)
on the normalized strings. Usage: python -I recognition_accuracy.py <expected.json> <outroot>
"""
import csv, difflib, json, os, re, sys


def norm(s):
    s = s or ""
    s = re.sub(r"\\(left|right|displaystyle|textstyle|,|;|!|quad|qquad)(?![a-zA-Z])", "", s)
    s = s.replace("\\mathrm", "").replace("\\operatorname", "").replace("\\mathit", "")
    s = re.sub(r"[\s{}]", "", s)
    return s


def main():
    exp = json.load(open(sys.argv[1], encoding="utf-8"))
    root = sys.argv[2]
    n = exact = 0
    ratios = []
    rows_out = []
    for d in exp["docs"]:
        stem = os.path.splitext(d["file"])[0]
        rep = os.path.join(root, stem, "report.csv")
        if not os.path.exists(rep):
            continue
        rows = list(csv.DictReader(open(rep, encoding="utf-8-sig", newline="")))
        for part in sorted({p["part"] for p in d["pictures"]}):
            ep = [p for p in d["pictures"] if p["part"] == part]
            rp = [r for r in rows if r["part"] == part]
            for e, r in zip(ep, rp):
                if not e.get("truth_latex") or not r["latex"]:
                    continue
                n += 1
                a, b = norm(e["truth_latex"]), norm(r["latex"].lstrip("'"))
                ok = a == b
                exact += ok
                ratio = difflib.SequenceMatcher(None, a, b).ratio()
                ratios.append(ratio)
                rows_out.append([d["file"], r["paragraph_index"], r["score"], int(ok), f"{ratio:.3f}", e["truth_latex"], r["latex"]])
    out = os.path.join(root, "recognition_accuracy.csv")
    with open(out, "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f)
        w.writerow(["document", "paragraph_index", "ssim", "normalized_exact", "char_similarity", "truth", "recognized"])
        w.writerows(rows_out)
    if n:
        print(f"items={n} normalized_exact={exact} ({100*exact/n:.1f}%) mean_char_similarity={sum(ratios)/n:.3f} -> {out}")
    else:
        print("no comparable items")


if __name__ == "__main__":
    main()
