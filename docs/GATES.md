# Gate results

Every gate records PASS or FAIL with evidence. Where a gate failed or a fallback applied, it says what happened next. All numbers below were measured on this machine on 2026-11-11, and each one names the file it came from.

Runs referenced:
- **Official.** Threshold from `config\threshold.json` (0.95). The real document went to `D:\eq\output`; the corpus went to `out\official_corpus`; the second pass went to `out\official_second`.
- **Mechanics validation.** `--min-score 0` forces every recognisable candidate through conversion, to test the conversion machinery: splice, OMML, package, LibreOffice, validator and idempotence. It says nothing about recognition accuracy, and its outputs are not deliverables. Corpus: `out\mech_corpus` / `out\mech_second`. Real document: `out\mech2_real` / `out\mech2_second`. An earlier real-document mechanics run in `out\mech_real` is kept as evidence of the FAIL described under Gate 3.

## Gate 0 — every picture element in exactly one category: **PASS**

- **Corpus.** `scripts\check_gate0.py` compares inventory.csv and report.csv against the independently written `tests\corpus\expected.json`. It passed 141/141 checks on `out\dry_corpus`, again on `out\official_corpus`, and again on `out\mech_corpus` (`gate0_check.csv` in each). The checks cover: a valid single category per picture, picture counts per document, final category per picture, and native m:oMath/m:oMathPara counts.
- **Real document.** 149 picture elements: 123 CANDIDATE (36 in table cells, 87 inline in body text) and 26 EXCLUDED (all "taller than 1 inch"). Native equations: 0. Each row of `D:\eq\output\real_sample_manuscript\inventory.csv` has exactly one category.
- **Hashes.** SHA-256 is recorded for every picture with image data (inventory.csv column `sha256`). There are 102 distinct candidate images.
- **Detection fixes made during the gate.** None were needed for categorisation. The unit-test subagent found two lookup bugs that affect unusual packages: part names differing in case, and percent-encoded part names. Both were fixed before the official run.

## Gate 1 — ≥ 90% of candidates return LaTeX without error: **PASS**

- **Real document:** 123/123 (100.0%). Median 0.79 s per image, maximum 17.82 s, on CPU (dry run, `out\dry_real`).
- **Corpus:** 44/44 equation candidates (100%). An earlier report showed 44/45 because the colourful logo, which is excluded by content statistics, was counted as an attempt. That counting was fixed.
- **Model:** pix2text 1.1.7, mfr-1.5 ONNX on CPU. The model loads in about 0.7–0.8 s once cached. The first download took 45.6 s.
- **Accuracy where ground truth exists (corpus only, `scripts\recognition_accuracy.py`):** 28/44 (63.6%) normalised exact match, mean normalised character similarity 0.926. "Normalised" means whitespace, braces, `\left`/`\right`, spacing commands, `\mathrm` and `\operatorname` were removed before comparing. The real document has no ground truth, so no accuracy figure is stated for it.

## Gate 2 — no MISMATCH in the visual sample above the threshold: **PASS (with the 0.95 fallback)**

- **Sample.** 60 items, drawn by `scripts\make_visual_sheets.py --seed 20261111` from 130 distinct scored images in `out\dry_corpus` and `out\dry_real`. The lead engineer (an AI model) classified each original-vs-render pair by viewing the sheets. **This is an automated check, not a human review.** Results are in `out\verify\visual_check.csv`.
- **Result:** 55 MATCH, 2 MINOR_DIFF, 3 MISMATCH. The mismatches were at SSIM 0.583 (subscript x lost), 0.811 (italic f recognised as bold), and 0.249 (subscript 5 read as s).
- **Threshold rule.** The lowest SSIM with no MISMATCH at or above it is 0.841. Only 3 sampled items lie above the 0.811 mismatch (0.841, 0.844 and 0.861). That is too few to support a threshold, so **0.95 was used**, as the brief requires when the sample is too small.
- **Gate check.** No MISMATCH is at or above 0.95, so the gate passes.
- **Auto-accept rate:** 0/123 on the real document (maximum SSIM observed 0.897) and 0/44 on the corpus (maximum 0.844). **The data does not support automatic conversion at any SSIM value observed.**
- **Why SSIM is weak here.** A pilot on 10 synthetic correct/wrong pairs (DECISIONS D28) showed overlapping scores. The references are rendered in Cambria Math while the originals use other fonts. In addition, single-glyph errors (5 vs s, bold vs italic) change only a few pixels.

## Gate 3 — per-document unit tests, byte identity, LibreOffice open, Open XML validator

| Run | Documents | Result | Evidence |
|---|---|---|---|
| Official (real document + 12 corpus) | 13 | **PASS 13/13** | FINAL_REPORT.md of each document. Nothing was converted (threshold 0.95), so every entry is byte-identical; LibreOffice converts each to PDF; 0 validator errors in input and output. |
| Mechanics, corpus | 12 | **PASS 12/12** | 42 conversions (display, inline, table cell, mc:AlternateContent Choice, inline VML, OLE PNG preview). Only `word/document.xml` changed in each converted file; validator reports 0 errors. |
| Mechanics, real document (first attempt, `out\mech_real`) | 1 | **FAIL** | 114 conversions. Byte identity, re-inventory and LibreOffice passed. The Open XML validator reported 3 `Sch_UnexpectedElementContentExpectingComplex` errors: Pandoc emits `m:mcPr` children in the order (mcJc, count), and the schema requires (count, mcJc). This affects matrix/array equations. The output was renamed `.GATE3_FAILED.docx`; the input was untouched. |
| Mechanics, real document after fix (`out\mech2_real`) | 1 | **PASS** | Fix: `OmmlConverter.Normalize` reorders property children into schema order, with 3 regression tests added. Byte identity: 140 entries, only `word/document.xml` changed. Re-inventory: 149 → 35 pictures, m:oMath 0 → 114. LibreOffice: PDF produced. Validator: 0 errors. |

- **Unit tests:** 82 passed, 0 failed (`tests\unit_result.json`). The first test run had 4 failures that exposed real engine bugs: a CSV cell not escaped, a part-name case mismatch on save, percent-encoded part names, and the "Fig." alt-text pattern. All four were fixed.
- **Visual confirmation of rendering.** The LibreOffice PDFs of mechanics outputs 01 and 04 were rasterised and inspected. Display equations are centred, inline equations sit in the running text, and the equation in the AlternateContent Choice renders.

## Gate 4 — idempotence and count reconciliation: **PASS**

- **Official:** 13/13 identical on the second pass (`MathMorph.exe compare`: same entries, same bytes, same order), with 0 conversions on the second pass. The final run with the shipped binary gave real document 1/1 and corpus 12/12, logged in `out\phase4_official_final.log`. `out\official_second\idempotence_check.csv` holds only the corpus result, because both checks write to the same file.
- **Mechanics, corpus:** 12/12 identical on the second pass. CONVERTED in the first pass equals m:oMath(after) − m:oMath(before) for every document.
- **Mechanics, real document:** identical on the second pass. 114 converted = 114 new m:oMath (`out\mech2_second\idempotence_check.csv`).
- **Inventory counts reconcile:** pictures after = pictures before − converted, for every document (G3.reinventory).
