# Verification checks

Independent of the converter engine; all are read-only except for the one result CSV each writes.

## OoxmlValidator (tools\OoxmlValidator\OoxmlValidator.exe)

Validates a .docx against the Open XML schemas and semantic rules (DocumentFormat.OpenXml 3.5.1,
`OpenXmlValidator` at the newest format version the SDK offers, currently `Microsoft365`). Opens the
file read-only (`FileAccess.Read` and `isEditable: false`), so the file is never changed.

    tools\OoxmlValidator\OoxmlValidator.exe <file.docx>

stdout: one JSON object per error per line, `{"part","id","description","path","type"}` (path is the
XPath of the node). Nothing else goes to stdout. The summary goes to stderr.
Exit codes: `0` no errors, `1` errors found, `2` cannot open the file or another failure.
Source is in `tools\OoxmlValidator\src`. It is a self-contained single-file exe, so no .NET runtime is needed.

## check_gate0.py

Compares a converter run with `tests\corpus\expected.json`. For each document it reads
`<out>\<stem>\inventory.csv`, `report.csv` and `FINAL_REPORT.md` and checks:
(a) every picture has exactly one category from CANDIDATE/OLE_PREVIEW/EXCLUDED/UNSUPPORTED_FORMAT/NATIVE,
and the report rows match the inventory rows one to one; (b) the picture count matches;
(c) the final category in `report.csv` matches `expected_category`. Pictures are matched on
(part, para_index, rel_id) in document order. (d) the native `m:oMath`/`m:oMathPara` counts in
FINAL_REPORT.md match, and so does the inventory `# native equations:` trailer.

    .venv\Scripts\python.exe -I scripts\check_gate0.py --expected tests\corpus\expected.json --out out\<run> [--quiet]

Writes `<out>\gate0_check.csv` and prints a table (`--quiet` prints only the failures).
Exit codes: `0` all checks pass, `1` any failure, `2` usage error.

## check_idempotence.ps1

Run 2 converts the .docx files that run 1 produced. For each subfolder of `-FirstOut` that holds a .docx
(`_work` and `review` are ignored), the script checks:
1. `MathMorph.exe compare` of the run-1 and run-2 .docx exits 0 with `identical: true`.
2. Run 2's `report.csv` has 0 rows with status CONVERTED.
3. The run-1 CONVERTED count equals run-2 m:oMath minus run-1 m:oMath, both taken from the
   "Existing native equations" line in FINAL_REPORT.md.

The script does not run the converter.

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check_idempotence.ps1 -FirstOut out\<run1> -SecondOut out\<run2>

Writes `<SecondOut>\idempotence_check.csv` and prints a table.
Exit codes: `0` all documents pass, `1` any failure (or no .docx found), `2` usage error.
