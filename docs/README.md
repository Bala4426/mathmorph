# MathMorph

MathMorph converts equation **pictures** in Word `.docx` files into native, editable Word equations (OMML, `m:oMath`).
It runs locally on Windows. No document content leaves the machine.

## What it does and does not do

| Does | Does not |
|---|---|
| Finds every picture element in the body, tables and text boxes, and puts each one in exactly one category | Touch headers, footers, footnotes, endnotes or comments (inventoried, always EXCLUDED) |
| Recognises inline PNG/JPEG equation pictures with pix2text (local ONNX model, CPU) | Convert EMF/WMF or other formats (reported UNSUPPORTED_FORMAT) |
| Re-renders the LaTeX locally and scores it against the original (SSIM). Only items at or above the threshold are replaced | Prove that the LaTeX is semantically correct. A high SSIM means "looks alike", nothing more |
| Replaces only the run that holds the picture. Every other byte of the package is unchanged | Remove the original image part. It stays in the package, unused |
| Writes per-document and batch reports and an HTML review page | Edit equations inside tracked changes, runs with other content, anchored/floating pictures or `mc:Fallback` copies |
| Leaves the input file untouched | Carry the picture's alt text over. OMML has no alt-text attribute |

For measured results, see `BATCH_SUMMARY.md` and each `FINAL_REPORT.md` from a run. This README gives no accuracy figures.

## Folder layout (`<MathMorph folder>`)

| Path | Contents |
|---|---|
| `bin\` | `MathMorph.exe`, the command-line converter. Self-contained .NET 8 (win-x64); needs no installed .NET |
| `gui_bin\` | `MathMorph.Gui.exe`, a Windows Forms front end that runs `MathMorph.exe` (self-contained) |
| `.venv\` | Python 3.13 environment for the workers (`.venv\Scripts\python.exe`) |
| `workers\` | `pix2text_worker.py` (recognition) and `verify_worker.py` (render + SSIM) |
| `tools\pandoc\` | `pandoc.exe` (3.9, from the `pypandoc_binary` wheel). Used for LaTeX→OMML and LaTeX→MathML |
| `tools\models\` | pix2text model files and redirected library caches (`pix2text`, `hf`, `ultralytics`) |
| `tools\dotnet8\` | Local .NET 8 SDK. **Build only**, not needed at run time |
| `tools\dotnet_home\`, `tools\nuget\` | `DOTNET_CLI_HOME` and `NUGET_PACKAGES` for builds |
| `tools\OoxmlValidator\` | `OoxmlValidator.exe` (Open XML SDK validator, used by Gate 3) and its source in `src\` |
| `config\threshold.json` | The auto-accept threshold (`min_score`) and the reason for it (`basis`) |
| `src\` | Engine and CLI source (`MathMorph.csproj`, built-in libraries only) |
| `gui\` | GUI source |
| `tests\Unit\` | Unit tests (hand-written runner) and `run_tests.ps1` |
| `tests\corpus\` | Synthetic test documents and `expected.json` (see `tests\corpus\README.md`) |
| `scripts\` | Corpus generator, threshold sample tool, checks, model download (see `scripts\README_checks.md`) |
| `docs\` | This file, `ARCHITECTURE.md`, `DECISIONS.md` |
| `out\` | Scratch output: `edge_profile` (Edge profile for rendering), `lo_profile` (LibreOffice profile), `verify\` (visual check), logs, test runs |

`MathMorph.exe` finds the project root by walking up from its own folder to the first folder that holds
`workers\pix2text_worker.py`. To run it from elsewhere, set `MATHMORPH_HOME=<MathMorph folder>`.

## Requirements

- Windows x64.
- **Microsoft Edge** (or Chrome). It is needed for the verification render. Without it, no item can be scored and nothing is converted.
- **LibreOffice** (optional). Used only for the Gate 3 "opens in LibreOffice" check. When it is missing, that check is SKIPPED.
- Everything else (Python environment, models, Pandoc, validator) is in the project folder.

## Command line

```
MathMorph.exe [<input .docx or folder>] [<output-folder>] [--min-score <v>] [--dry-run] [--force] [--no-gates] [--mathtype convert|skip]
MathMorph.exe compare <a.docx> <b.docx>     entry-by-entry package comparison (JSON to stdout)
MathMorph.exe inventory <a.docx>            print inventory categories (no recognition)
MathMorph.exe mathtype-count <.docx|folder> count MathType objects: prints "MATHTYPE <objects> <documents with MathType> <documents>"
MathMorph.exe mathtype <a.docx>             print the LaTeX read from each MathType object (nothing written)
MathMorph.exe --help
```

### MathType equations (`--mathtype convert`, the default)

MathType objects (`w:object` with ProgID `Equation.DSMT*`) store the exact equation as MTEF data in the
"Equation Native" stream of their embedded `oleObject*.bin`. With `--mathtype convert`, MathMorph reads that
data (MTEF version 5, MathType 4 and later), writes it as LaTeX and converts it to OMML with Pandoc. No
picture recognition and no SSIM threshold are involved: the result is exact. The object's run is replaced
in place, like any other conversion; the `.bin` and WMF preview stay in the package, unused.

- Body, tables and text boxes only (headers, footers and notes are never changed), never an `mc:Fallback` copy.
- Anything the reader does not understand (unknown template, unmapped MathType symbol, MTEF version 3 from
  Equation Editor 3.0) leaves the object unchanged with status NEEDS_REVIEW and the reason in `note`.
- Default is `convert`. With `--mathtype skip`, MathType objects stay as they are and are reported UNSUPPORTED_FORMAT for their WMF/EMF preview.
- `report.csv` note for converted objects: "MathType equation converted from its embedded data (...)"; `latex` holds the LaTeX used.

| Item | Behaviour |
|---|---|
| Input | A `.docx` file, or a folder. Default `D:\eq\input` |
| Output folder | Default `D:\eq\output`. Created if missing |
| Folder mode | Only `*.docx` directly in the folder (**not recursive**). Word lock files `~$*.docx` are skipped. Files are processed in name order |
| `--min-score <v>` | Overrides `config\threshold.json` for this run. Must be between -1 and 1 |
| `--dry-run` | Runs inventory, recognition and scoring and writes the reports, but no `.docx`. Gate 3 is N/A |
| `--force` | Processes documents whose output subfolder already has files (deletes those files first). Overwrites `BATCH_SUMMARY.md` and `review\index.html` |
| `--no-gates` | Skips the Gate 3 checks after saving |

Path rules: in folder mode the output folder may not be the input folder or inside it. In single-file
mode the output folder may not be the file's own folder.

Exit codes (convert mode):

| Code | Meaning |
|---|---|
| 0 | Every document finished with status OK or DRY_RUN |
| 2 | At least one document is FAILED, SKIPPED_EXISTS, REJECTED_LIMITS or GATE3_FAILED |
| 3 | Error before or outside document processing (bad option, input not found, path rule broken, ...) |

`compare` exits 0 when the packages are identical (same entries, same bytes, same order) and 1 otherwise.

## GUI

Start it with `Start-MathMorph.bat` (or `gui_bin\MathMorph.Gui.exe`). It has three steps: **Add documents**,
**Convert**, **Review**. All work is done by `MathMorph.exe`; the GUI finds it in its own folder, `..\bin`,
`..\..\bin`, or `%MATHMORPH_HOME%\bin` (`EQCONV_HOME` is still accepted). It has no `--no-gates` option.

**Convert screen.** Drop `.docx` files or folders on the list. Modes: *Convert all, then proofread*
(`--min-score 0`), *Safe* (the configured threshold) or a custom threshold. "Convert MathType equation objects"
(on by default) passes `--mathtype convert`; the GUI counts MathType objects in the background with
`mathtype-count`. Right-click a document (or press Ctrl+Enter) to convert only that document, open it in Review
or open its results folder — for example, convert a second document while you proofread the first.

**Review screen.** Each equation shows the original picture, the rendered LaTeX and the LaTeX text.

- **Check LaTeX** runs `MathMorph.exe preview`: it converts the LaTeX with the same Pandoc step a conversion
  uses and renders it next to the picture. "✓ Converts to a Word equation" is the check that matters; web
  previews such as the MathJax demo accept a different LaTeX dialect, so a formula can work there and not
  here (or the other way round).
- **Save & check** stores the fix in `<output>\_corrections\<document>.overrides.csv` and checks it. The row
  then shows **Fixed – apply pending**: the Word document is not changed yet. Saving the recognised LaTeX
  unchanged accepts it as is.
- **Apply corrections & re-convert** re-runs the document with `--latex-overrides`. Recognition and render
  results are cached in `<output>\_work\cache\`, so a re-run only processes changed items and takes seconds.
  Applied fixes then show as **Converted**.

## Output layout

```
<output>\
  BATCH_SUMMARY.md            one row per document, totals, next steps
  review\index.html           NEEDS_REVIEW items: original, local render, score, LaTeX (img\ beside it)
  <document stem>\
    <name>.docx               converted copy (or <stem>.GATE3_FAILED.docx)
    inventory.csv             every picture element, its category and reason
    report.csv                one row per picture element, final status
    FINAL_REPORT.md           environment, inventory, SSIM distribution, threshold, gates, review list
  _work\
    batch_results.json        machine-readable batch result
    logs\pix2text_worker.stderr.log, verify_worker.stderr.log
    <document stem>\img\      extracted candidate images (<sha256>.png/.jpg)
    <document stem>\render\   local renders (<sha256>_<latex hash>.png)
    <document stem>\tmp\      Pandoc temp files
    <document stem>\lo\       LibreOffice PDF from Gate 3
```

Without `--force`, an existing `BATCH_SUMMARY.md` or `review\index.html` is kept and the new one gets a
timestamp suffix (`BATCH_SUMMARY.20261111-153000.md`).

Document statuses: `OK`, `DRY_RUN`, `FAILED` (exception; see Error), `SKIPPED_EXISTS` (output subfolder
not empty, no `--force`), `REJECTED_LIMITS` (package failed a safety limit), `GATE3_FAILED`.

For a SKIPPED_EXISTS document, nothing in the existing subfolder is written or changed. The skip is
listed only in the batch summary.

## report.csv

Columns: `document, part, paragraph_index, rel_id, image_sha256, category, status, score, latex, note`.

- `part`: zip part, e.g. `word/document.xml`. `paragraph_index`: 0-based index of the `w:p` among all `w:p` in that part (tables, text boxes and both AlternateContent branches included). It does not change after conversion.
- `rel_id` and `image_sha256` trace each equation back to its source picture.
- `category`: detection category (CANDIDATE, OLE_PREVIEW, EXCLUDED, UNSUPPORTED_FORMAT, NATIVE).
- `score`: SSIM (4 decimals), empty when not scored. `latex`: recognised LaTeX. Cells that start with `= + - @` get a leading `'` (CSV-injection guard).

| Status | Meaning |
|---|---|
| CONVERTED | Replaced by an OMML equation in the output. OLE previews carry the note "OLE preview replaced" |
| NEEDS_REVIEW | A candidate left as a picture. `note` says why: recognition failed, render failed, score below threshold, structure not replaceable (tracked change, run with other content, unsupported parent), OMML conversion failed, or rewrite failed |
| UNSUPPORTED_FORMAT | EMF/WMF or another format that is not PNG/JPEG |
| EXCLUDED | Not an equation candidate: wrong container, mc:Fallback copy, anchored/floating, taller than 1 inch, logo/figure by name or by pixel statistics, or a picture inside a native equation |
| NO_IMAGE | The picture element has no embedded image (missing part or external link) |
| OLE_PREVIEW | Defined in the schema but not emitted by the current pipeline; OLE previews end as one of the statuses above |

## Threshold

An item is replaced only if its SSIM score is **≥ `min_score`**. The value comes from
`config\threshold.json` (`min_score`, `basis`); `basis` is printed in every report. Without the file the
default is 0.95. `--min-score` (or the GUI field) overrides it for one run.

SSIM across different fonts is weak evidence (see `DECISIONS.md` D28), so the threshold must come from a
labelled sample, not from guessing. To re-derive it:

1. Dry run over representative documents into a scratch folder:
   `bin\MathMorph.exe D:\eq\input <MathMorph folder>\out\dry_real --dry-run --force`
2. Draw a fixed-seed sample and make side-by-side sheets:
   `.venv\Scripts\python.exe -I scripts\make_visual_sheets.py --seed 20261111 --n 60 --out <MathMorph folder>\out\verify <MathMorph folder>\out\dry_real`
   This writes `out\verify\visual_sample.csv` and `out\verify\sheets\sheet_XX.png` (original left, render right).
3. A person classifies each pair and saves `out\verify\visual_check.csv` with columns
   `id,document,sha256,paragraph_index,ssim,classification,latex,note`; classification is `MATCH`, `MINOR_DIFF` or `MISMATCH`.
   FINAL_REPORT.md summarises this file per document.
4. Choose a `min_score` above every MISMATCH score, with enough MATCH items above it to support it.
   Edit `config\threshold.json` (`min_score`, `basis`; `sample_file` and `derived` are for the record), or pass `--min-score`.

## Safety properties

- **Input is never written.** It is opened read-only. The output path may never equal the input path (see path rules above).
- **Zip limits** (checked on the bytes actually inflated): at most 10,000 entries, 1 GiB total, 512 MiB per entry, compression ratio 200 for entries over 1 MiB, 512 MiB input file. Entry names with `\`, a leading `/` or `..`, and duplicate names are rejected. A failure gives REJECTED_LIMITS.
- **XML hardening:** DTDs prohibited, no external resolver, entity expansion capped, only UTF-8/UTF-16 parts accepted. The workers set Pillow's decompression-bomb limit to 50 megapixels.
- **Offline recognition:** models load from `tools\models` with `HF_HUB_OFFLINE=1`. Pandoc runs with `--sandbox`. Edge renders a local `file://` page with no external resources, and background networking, component updates and sync are off.
- **Overwrite protection:** a non-empty output subfolder is skipped unless `--force`.
- **Gate failure naming:** if any Gate 3 check fails, the output is renamed `<stem>.GATE3_FAILED.docx`.
- **Atomic save:** the output is written to `<name>.tmp` first, then moved into place.
- A part that cannot be rewritten cleanly is not written at all; its items go back to NEEDS_REVIEW.

## Building

Use the local SDK. Do not rebuild `bin\` while a batch is running (the files are locked).

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = '<MathMorph folder>\tools\dotnet_home'
$env:NUGET_PACKAGES  = '<MathMorph folder>\tools\nuget'
$dotnet = '<MathMorph folder>\tools\dotnet8\dotnet.exe'
& $dotnet publish <MathMorph folder>\src\MathMorph.csproj -c Release -r win-x64 --self-contained true -o <MathMorph folder>\bin
& $dotnet publish <MathMorph folder>\gui\MathMorph.Gui.csproj -c Release -r win-x64 --self-contained true -o <MathMorph folder>\gui_bin
# validator (pulls DocumentFormat.OpenXml 3.5.1 from NuGet; single-file self-contained is set in the .csproj)
& $dotnet publish <MathMorph folder>\tools\OoxmlValidator\src\OoxmlValidator.csproj -c Release -o <MathMorph folder>\tools\OoxmlValidator
```

## Tests and checks

- Unit tests: `powershell -NoProfile -ExecutionPolicy Bypass -File <MathMorph folder>\tests\Unit\run_tests.ps1`.
  It builds into `tests\Unit\bin_out`, runs the tests and writes `tests\unit_result.json`. Exit 0 = all pass.
  **Gate 3 reads that file:** if it records failures, every converted document becomes GATE3_FAILED; if it is missing, the check is SKIPPED.
- Independent checks (`check_gate0.py`, `check_idempotence.ps1`, the validator): see `scripts\README_checks.md`.
- Corpus regeneration: see `tests\corpus\README.md`. `scripts\run_phase4.ps1` runs the full first pass, second pass and checks.

## Rebuilding the Python environment

```powershell
py -3.13 -m venv <MathMorph folder>\.venv
$py = '<MathMorph folder>\.venv\Scripts\python.exe'
& $py -m pip install pix2text pypandoc_binary scikit-image truststore
& $py -m pip install rapidocr==3.9.2          # pin: rapidocr 3.10 breaks the cnstd import (DECISIONS D4)
Copy-Item <MathMorph folder>\.venv\Lib\site-packages\pypandoc\files\pandoc.exe <MathMorph folder>\tools\pandoc\
& $py -I <MathMorph folder>\scripts\download_models.py   # one-time model download into tools\models
```

`download_models.py` prints `MODEL_READY` when done. After that the workers need no network.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Certificate errors during `pip install` or model download | A TLS-inspecting proxy. `download_models.py` already uses `truststore` (Windows certificate store). For pip, use a pip version that supports the system store (`--use-feature=truststore` on older pip). Do not disable verification |
| Every item NEEDS_REVIEW with "verification render failed: ... no headless browser" | Edge not found. Install Edge, or set `MATHMORPH_EDGE` to the full path of `msedge.exe`/`chrome.exe` before starting MathMorph |
| Render failures or hangs after a crash | Close stray `msedge.exe` processes that use `out\edge_profile`; delete that folder if needed (it is recreated). Do not run two batches at once: they share this profile |
| `G3.libreoffice_open` FAIL ("no PDF produced") | Close running LibreOffice instances. Delete `out\lo_profile` (recreated on the next run) |
| "recognizer unavailable" on every item | The pix2text worker did not start. Read `<output>\_work\logs\pix2text_worker.stderr.log`. Check the `.venv` and `tools\models` |
| "cannot find mathmorph root (set MATHMORPH_HOME)" | `MathMorph.exe` was moved out of the project tree. Set `MATHMORPH_HOME` |
| Exit code 2 with SKIPPED_EXISTS | Output already there. Use another output folder or `--force` |
| All documents GATE3_FAILED with `G3.unit_tests` FAIL | `tests\unit_result.json` records failed tests. Fix and re-run `run_tests.ps1` |
