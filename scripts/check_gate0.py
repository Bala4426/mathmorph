"""Gate-0 check: compare a converter run's inventory/report against tests/corpus/expected.json.

Usage:
    .venv\\Scripts\\python.exe -I scripts\\check_gate0.py --expected tests\\corpus\\expected.json --out out\\<run>

For each document in expected.json reads <out>\\<stem>\\inventory.csv, report.csv, FINAL_REPORT.md and checks:
  (a) every inventory picture row has exactly one valid category
      (CANDIDATE, OLE_PREVIEW, EXCLUDED, UNSUPPORTED_FORMAT, NATIVE), and report.csv rows
      line up 1:1 with inventory rows (same part / paragraph_index / rel_id, same order);
  (b) number of pictures equals the expected number;
  (c) FINAL category (report.csv "category") equals expected_category, pictures matched by
      (part, para_index, rel_id) in document order (inventory "ordinal" within part);
  (d) FINAL_REPORT.md line "- Existing native equations: m:oMath=X, m:oMathPara=Y" equals
      expected native_omath / native_omathpara (the inventory "# native equations:" trailer is
      cross-checked too).
Writes <out>\\gate0_check.csv, prints a table. Exit 0 = all pass, 1 = any failure, 2 = usage error.
Read-only with respect to everything except <out>\\gate0_check.csv.
"""
from __future__ import annotations

import argparse
import csv
import io
import json
import os
import re
import sys
from collections import defaultdict

VALID_CATEGORIES = {"CANDIDATE", "OLE_PREVIEW", "EXCLUDED", "UNSUPPORTED_FORMAT", "NATIVE"}
NATIVE_RE = re.compile(r"^\s*-\s*Existing native equations:\s*m:oMath=(\d+),\s*m:oMathPara=(\d+)\s*$")
INV_NATIVE_RE = re.compile(r"^#\s*native equations:\s*m:oMath=(\d+),\s*m:oMathPara=(\d+)")


def unescape(v: str) -> str:
    """Undo the formula-injection guard: leading apostrophe before = + - @."""
    if len(v) >= 2 and v[0] == "'" and v[1] in "=+-@":
        return v[1:]
    return v


def read_csv(path: str) -> tuple[list[dict], list[str]]:
    """Return (rows, comment_lines). Lines starting with '#' are comments, not data."""
    with open(path, "r", encoding="utf-8-sig", newline="") as f:
        text = f.read()
    data_lines, comments = [], []
    for line in text.splitlines(keepends=True):
        if line.startswith("#"):
            comments.append(line.strip())
        else:
            data_lines.append(line)
    reader = csv.DictReader(io.StringIO("".join(data_lines)))
    rows = []
    for r in reader:
        if not any((v or "").strip() for v in r.values() if isinstance(v, str)):
            continue
        rows.append({k: unescape((v or "").strip()) for k, v in r.items() if k is not None})
    return rows, comments


def to_int(v, default=-1):
    try:
        return int(str(v).strip())
    except (TypeError, ValueError):
        return default


class Results:
    def __init__(self):
        self.rows: list[dict] = []

    def add(self, doc, check, ok, expected="", actual="", part="", para="", rid="", detail=""):
        self.rows.append({
            "document": doc, "check": check, "part": part, "para_index": para, "rel_id": rid,
            "expected": expected, "actual": actual, "result": "PASS" if ok else "FAIL", "detail": detail,
        })

    @property
    def failures(self):
        return [r for r in self.rows if r["result"] != "PASS"]


def check_doc(doc: dict, out_root: str, res: Results) -> None:
    fname = doc["file"]
    stem = os.path.splitext(fname)[0]
    folder = os.path.join(out_root, stem)
    inv_path = os.path.join(folder, "inventory.csv")
    rep_path = os.path.join(folder, "report.csv")
    fin_path = os.path.join(folder, "FINAL_REPORT.md")
    exp_pics = doc.get("pictures", [])

    missing = [p for p in (inv_path, rep_path, fin_path) if not os.path.isfile(p)]
    if missing:
        res.add(fname, "files_present", False, "inventory.csv, report.csv, FINAL_REPORT.md",
                "missing: " + ", ".join(os.path.basename(m) for m in missing))
        if not os.path.isfile(inv_path) or not os.path.isfile(rep_path):
            return
    else:
        res.add(fname, "files_present", True, "3 files", "3 files")

    inv, inv_comments = read_csv(inv_path)
    rep, _ = read_csv(rep_path)

    # (a) categories in inventory (pre-recognition) and report (final)
    bad_inv = [r for r in inv if r.get("category", "") not in VALID_CATEGORIES]
    res.add(fname, "a_inventory_category_valid", not bad_inv, "one of " + "/".join(sorted(VALID_CATEGORIES)),
            "ok" if not bad_inv else "; ".join(f"{r.get('part')}#{r.get('ordinal')}={r.get('category')!r}" for r in bad_inv))
    bad_rep = [r for r in rep if r.get("category", "") not in VALID_CATEGORIES]
    res.add(fname, "a_report_category_valid", not bad_rep, "one of " + "/".join(sorted(VALID_CATEGORIES)),
            "ok" if not bad_rep else "; ".join(f"{r.get('part')}:{r.get('paragraph_index')}:{r.get('rel_id')}={r.get('category')!r}" for r in bad_rep))

    # report.csv must be 1:1 with inventory.csv, same order
    align_problems = []
    if len(rep) != len(inv):
        align_problems.append(f"row count inventory={len(inv)} report={len(rep)}")
    for i, (a, b) in enumerate(zip(inv, rep)):
        ka = (a.get("part"), to_int(a.get("paragraph_index")), a.get("rel_id"))
        kb = (b.get("part"), to_int(b.get("paragraph_index")), b.get("rel_id"))
        if ka != kb:
            align_problems.append(f"row {i}: inventory {ka} vs report {kb}")
        if b.get("document") and b.get("document") != fname:
            align_problems.append(f"row {i}: report document {b.get('document')!r}")
        sa, sb = a.get("sha256", ""), b.get("image_sha256", "")
        if sa and sb and sa != sb:
            align_problems.append(f"row {i}: sha256 differs")
    res.add(fname, "a_report_aligned_with_inventory", not align_problems, f"{len(inv)} rows in same order",
            "ok" if not align_problems else "; ".join(align_problems[:5]))

    # (b) count. Inventory rows are picture elements; NATIVE rows (if any) are not pictures.
    pic_idx = [i for i, r in enumerate(inv) if r.get("category") != "NATIVE"]
    res.add(fname, "b_picture_count", len(pic_idx) == len(exp_pics), str(len(exp_pics)), str(len(pic_idx)))

    # (c) per-picture final category, matched by (part, para_index, rel_id) in document order
    # Document order within a part = inventory ordinal; parts keep their first-appearance order.
    part_rank: dict[str, int] = {}
    for i in pic_idx:
        part_rank.setdefault(inv[i].get("part", ""), len(part_rank))
    ordered = sorted(pic_idx, key=lambda i: (part_rank[inv[i].get("part", "")], to_int(inv[i].get("ordinal"), i), i))
    groups: dict[tuple, list[int]] = defaultdict(list)
    for i in ordered:
        r = inv[i]
        groups[(r.get("part"), to_int(r.get("paragraph_index")), r.get("rel_id"))].append(i)
    used: dict[tuple, int] = defaultdict(int)
    for p in exp_pics:
        key = (p["part"], int(p["para_index"]), p["rid"])
        k = used[key]
        used[key] += 1
        cands = groups.get(key, [])
        if k >= len(cands):
            res.add(fname, "c_final_category", False, p["expected_category"], "NOT FOUND",
                    p["part"], p["para_index"], p["rid"], f"occurrence {k + 1} of key not in inventory")
            continue
        i = cands[k]
        final = rep[i].get("category", "") if i < len(rep) else "<no report row>"
        status = rep[i].get("status", "") if i < len(rep) else ""
        pre = inv[i].get("category", "")
        detail = f"inventory={pre} status={status}"
        if pre != final:
            detail += " (changed by content check)"
        res.add(fname, "c_final_category", final == p["expected_category"], p["expected_category"], final,
                p["part"], p["para_index"], p["rid"], detail)
    for key, idxs in groups.items():
        extra = idxs[used.get(key, 0):]
        for i in extra:
            res.add(fname, "c_unexpected_picture", False, "(none)", rep[i].get("category", "") if i < len(rep) else "",
                    key[0], key[1], key[2], f"inventory ordinal {inv[i].get('ordinal')} not in expected.json")

    # (d) native counts from FINAL_REPORT.md
    exp_native = f"m:oMath={doc.get('native_omath', 0)}, m:oMathPara={doc.get('native_omathpara', 0)}"
    if os.path.isfile(fin_path):
        got = None
        with open(fin_path, "r", encoding="utf-8-sig") as f:
            for line in f:
                m = NATIVE_RE.match(line.rstrip("\r\n"))
                if m:
                    got = f"m:oMath={int(m.group(1))}, m:oMathPara={int(m.group(2))}"
                    break
        res.add(fname, "d_native_final_report", got == exp_native, exp_native, got or "line not found")
    inv_native = None
    for c in inv_comments:
        m = INV_NATIVE_RE.match(c)
        if m:
            inv_native = f"m:oMath={int(m.group(1))}, m:oMathPara={int(m.group(2))}"
    res.add(fname, "d_native_inventory_trailer", inv_native == exp_native, exp_native, inv_native or "line not found")


def print_table(rows: list[dict]) -> None:
    cols = ["document", "check", "part", "para_index", "rel_id", "expected", "actual", "result"]
    widths = {c: max(len(c), *(len(str(r[c])) for r in rows)) if rows else len(c) for c in cols}
    widths["expected"] = min(widths["expected"], 40)
    widths["actual"] = min(widths["actual"], 40)

    def fmt(r):
        return "  ".join(str(r[c])[: widths[c]].ljust(widths[c]) for c in cols)

    print(fmt({c: c for c in cols}))
    print("  ".join("-" * widths[c] for c in cols))
    for r in rows:
        print(fmt(r))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--expected", required=True)
    ap.add_argument("--out", required=True, help="output root of a converter run")
    ap.add_argument("--quiet", action="store_true", help="print only failing rows and the summary")
    a = ap.parse_args()
    if not os.path.isfile(a.expected) or not os.path.isdir(a.out):
        print(f"error: missing --expected file or --out folder", file=sys.stderr)
        return 2
    with open(a.expected, "r", encoding="utf-8-sig") as f:
        expected = json.load(f)

    res = Results()
    for doc in expected["docs"]:
        try:
            check_doc(doc, a.out, res)
        except Exception as e:  # keep going, report as failure
            res.add(doc.get("file", "?"), "exception", False, "", f"{type(e).__name__}: {e}")

    out_csv = os.path.join(a.out, "gate0_check.csv")
    cols = ["document", "check", "part", "para_index", "rel_id", "expected", "actual", "result", "detail"]
    with open(out_csv, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=cols, quoting=csv.QUOTE_ALL)
        w.writeheader()
        w.writerows(res.rows)

    print_table(res.failures if a.quiet else res.rows)
    fails = res.failures
    print()
    print(f"gate0: {len(res.rows) - len(fails)}/{len(res.rows)} checks passed, {len(fails)} failed -> {out_csv}")
    if fails:
        print("FAILURES:")
        for r in fails:
            loc = f" {r['part']}:{r['para_index']}:{r['rel_id']}" if r["part"] else ""
            print(f"  {r['document']} {r['check']}{loc}: expected {r['expected']!r}, got {r['actual']!r} {r['detail']}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
