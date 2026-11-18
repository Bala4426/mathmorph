# MathMorph architecture

How a document goes from equation pictures to native Word equations. Rationale for individual choices
is in `DECISIONS.md` (referenced as Dnn). Operating instructions are in `README.md`.

## Components

| Component | File | Role |
|---|---|---|
| CLI | `src\Cli\Program.cs` | Arguments, file list, threshold loading, batch summary, exit codes |
| Pipeline | `src\Engine\Pipeline.cs` | Per-document flow, `BatchServices` (lazy workers + caches) |
| SafePackage | `src\Engine\SafePackage.cs` | Bounded zip reader; writer that copies untouched entries |
| SafeXml | `src\Engine\SafeXml.cs` | Hardened XML parse; decode/encode that preserves encoding and BOM |
| Inventory | `src\Engine\Inventory.cs` | Finds picture elements, assigns one category each, builds run locators |
| OmmlConverter | `src\Engine\OmmlConverter.cs` | LaTeX → OMML via Pandoc, OMML whitelist validation |
| Rewriter / XmlSplicer | `src\Engine\Rewriter.cs`, `XmlSplicer.cs` | Text-level replacement of exact element spans |
| Gates | `src\Engine\Gates.cs` | Gate 3 checks after save; package `compare` |
| Reports | `src\Engine\Reports.cs` | inventory.csv, report.csv, FINAL_REPORT.md, BATCH_SUMMARY.md, review page |
| Workers | `src\Recognition\*.cs`, `workers\*.py` | Long-lived Python processes: recognition and verification |
| Contracts | `src\Contracts\Models.cs` | Shared types: categories, statuses, limits, options |

The engine uses only built-in .NET libraries. The Open XML SDK is used only by the separate
`tools\OoxmlValidator` process (D29).

## Pipeline per document

`Pipeline.ProcessDocument` runs these steps for each input file.

1. **Pre-checks.** If the output subfolder exists and is non-empty and `--force` is not set: SKIPPED_EXISTS.
   The output path may not equal the input path. With `--force` the files in the subfolder are deleted.
2. **SafePackage.Open.** The whole zip is read into memory, read-only, under `SafetyLimits` (entry count,
   inflated bytes per entry and in total, compression ratio, file size, safe and unique entry names).
   A violation gives REJECTED_LIMITS and stops the document.
3. **Inventory.Scan.** Locates the main part from the package relationships, then scans it plus every
   header, footer, footnotes, endnotes and comments part it references. Each picture element gets one
   category and a reason (rules below). `inventory.csv` is written now.
4. **Extract.** For each CANDIDATE / OLE_PREVIEW item, the image bytes are written to
   `_work\<doc>\img\<sha256>.png|.jpg`. Items of other categories get their final status here
   (EXCLUDED, UNSUPPORTED_FORMAT, NO_IMAGE).
5. **Pix2TextWorker.** The image is recognised to LaTeX. Results are cached by SHA-256 for the whole
   batch, so each distinct image is recognised once.
6. **Content statistics exclusion.** The recognition worker also returns two pixel statistics. If more
   than 5 % of pixels are saturated (`colorful_fraction > 0.05`) or more than 30 % are mid-gray
   (`midtone_fraction > 0.30`), the item is re-categorised EXCLUDED as a logo/figure/photo.
   Recognition failure (no LaTeX) gives NEEDS_REVIEW.
7. **VerifyWorker (SSIM).** The LaTeX is rendered locally and scored against the original. Cached by
   (SHA-256, LaTeX). Render failure gives NEEDS_REVIEW.
8. **Threshold.** `score < MinScore` gives NEEDS_REVIEW ("score x below threshold y").
9. **NotReplaceable.** Structural check of the run that holds the picture. Returns a reason, which gives
   NEEDS_REVIEW, when: the picture is not in a run; the run holds other content; the run is inside
   `w:ins`, `w:del`, `w:moveFrom` or `w:moveTo`; or the run's parent is not one of `w:p`, `w:hyperlink`,
   `w:sdtContent`, `w:smartTag`, `w:customXml`, `w:fldSimple`, or an `mc:Choice` directly under a `w:p` (D18).
10. **OmmlConverter (Pandoc).** LaTeX → validated `m:oMath`. Failure gives NEEDS_REVIEW. Success marks
    the item CONVERTED and queues a replacement for its part.
11. **Rewriter / XmlSplicer.** Per changed part, all replacements are spliced into the original text in
    one pass, and the result must re-parse with the hardened parser. If anything fails, that part is not
    written and all its items go back to NEEDS_REVIEW.
12. **Save.** Skipped in dry run (status DRY_RUN). Otherwise `SafePackage.Save` writes every entry in the
    original order with the original timestamps; only the changed parts get new bytes. It writes to
    `.tmp` and then moves the file.
13. **Gates.** Gate 3 checks (below) unless `--no-gates`. Any FAIL renames the output to
    `<stem>.GATE3_FAILED.docx` and sets status GATE3_FAILED.
14. **Reports.** `report.csv` and `FINAL_REPORT.md` are always written (in a `finally` block). After all
    documents: `BATCH_SUMMARY.md`, `review\index.html`, `_work\batch_results.json`.

Any unexpected exception sets the document status to FAILED; the batch continues with the next file.

## Worker protocol

Both workers are Python scripts started from `.venv\Scripts\python.exe -I` (isolated mode) and driven by
`JsonLineWorker`.

| Aspect | Behaviour |
|---|---|
| Framing | One request line on stdin, exactly one JSON line on stdout. Requests may not contain newlines |
| Startup | The worker's first stdout line must be `{"ready": true, ...}` (pix2text: version and model load time; verifier: browser and Pandoc path) |
| Concurrency | One request in flight per worker (lock around write + read) |
| stdout hygiene | In Python, `sys.stdout` is redirected to stderr, so library prints cannot corrupt the protocol. Responses use ASCII-escaped JSON |
| stderr | Drained asynchronously into `_work\logs\<worker>.stderr.log`, so the pipe never fills |
| Timeouts | Start: 5 min (pix2text), 2 min (verifier). Request: 3 min each |
| Failure | On timeout or exit the process is killed; that item gets an error; the worker restarts on the next request |
| Start failure | Recorded once; every later item gets "recognizer unavailable" / "verifier unavailable" |
| Lazy start | A worker starts on the first item that needs it, so documents with no candidates never load the model |
| Shutdown | Empty line, close stdin, wait 15 s, then kill |
| Environment | `HF_HUB_OFFLINE=1`, `TRANSFORMERS_OFFLINE=1`, telemetry off, caches and model root under `tools\models`, `MATHMORPH_PANDOC`, `MATHMORPH_EDGE_PROFILE=out\edge_profile` |

**pix2text_worker.py.** Request: an absolute image path. Response:
`{path, ok, latex, error, seconds, width, height, colorful_fraction, midtone_fraction}`.
Model: `LatexOCR(model_name="mfr-1.5", model_backend="onnx", device="cpu")` (D7). Transparent images are
flattened onto white.

**verify_worker.py.** Request: `{"orig", "latex", "out"}`. Response: `{ok, ssim, render_path, error, seconds}`.
LaTeX → MathML with local Pandoc → a local HTML page (font Cambria Math) → headless Edge screenshot.
If the ink touches the page edge, the font size is halved and the shot retried (up to 3 tries). Score:
both images grayscale, cropped to ink (gray < 200, 2 px margin), resampled to 32 px height with the
original's aspect ratio, Gaussian blur σ = 1.5, SSIM (scikit-image, Gaussian weights, 7-px window).
Display and inline renderings are both scored and the higher one is kept (D28). The kept render is saved
at the original's pixel size for the review page. (The module docstring still describes an older
variant; the constants `NORM_H` and `SIGMA` in the code are authoritative.)

## Detection rules

A **picture element** is (D9): a `w:drawing` with its own `a:blip`; a `w:pict` with its own
`v:imagedata`; or a `w:object` (OLE). A drawing inside a `w:object` is that object's preview and is not
counted separately. "Own" excludes nested `w:txbxContent`, so a picture in a text box is counted once.

Categories, applied in this fixed order; the first match wins (D10):

| # | Condition | Category / reason |
|---|---|---|
| 1 | Inside `m:oMath` | NATIVE |
| 2 | Part is header, footer, footnote, endnote or comments | EXCLUDED `container:<name>` |
| 3 | Inside `mc:Fallback` | EXCLUDED (left unchanged) |
| 4 | `wp:anchor`, or VML with `position:absolute` | EXCLUDED anchored/floating |
| 5 | No embedded image (missing part or external link) | EXCLUDED `no_image: ...` → status NO_IMAGE |
| 6 | EMF or WMF (by magic bytes) | UNSUPPORTED_FORMAT (D12) |
| 7 | Height > 1 inch (914400 EMU) | EXCLUDED |
| 8 | Format not PNG or JPEG | UNSUPPORTED_FORMAT |
| 9 | Name/alt text matches logo, figure, chart, photo, diagram, graph, screenshot, banner, fig. | EXCLUDED |
| 10 | `w:object` | OLE_PREVIEW |
| 11 | Otherwise | CANDIDATE |

After recognition, step 6 of the pipeline can move a CANDIDATE or OLE_PREVIEW to EXCLUDED by pixel
statistics. Format is detected from magic bytes, not from the file extension. Height comes from
`wp:extent cy`, from the VML `style` height, or from `w:dyaOrig` for OLE objects.

## Splicing (why not re-serialise)

The changed part is **not** rebuilt from the XML tree. Re-serialising with LINQ to XML would reorder
namespace declarations, change quoting, whitespace and empty-element forms everywhere in the part. That
makes review diffs noisy and risks breaking content Word is sensitive to. Instead (D15):

- The part is parsed with line info. For each picture, `Inventory` records a `RunLocator`: line, column
  and qualified name of the run that directly owns the picture.
- `XmlSplicer.SpanOf` maps line/column to a character offset, checks that the qualified name really
  starts there, and finds the matching end tag with a small scanner that handles comments, CDATA,
  processing instructions, quoted attributes and self-closing tags.
- `XmlSplicer.Apply` replaces those `[start, end)` spans (non-overlapping, one per run). All other
  characters stay the same.
- The new text is encoded with the part's original encoding and BOM (`SafeXml.Encode`) and must re-parse.
- The inserted `m:oMath` / `m:oMathPara` declares `xmlns:m` itself, so the root element and its
  `mc:Ignorable` list are never touched (D16).
- Display vs inline (D20): `m:oMathPara` when the paragraph has no other visible content and the run's
  parent is the `w:p`; otherwise an inline `m:oMath` in the run's place.

Result: the output package differs from the input only in the changed parts, and inside those parts only
in the replaced runs. Gate 3 checks this byte identity.

## mc:AlternateContent handling

- A picture in **`mc:Fallback`** is always EXCLUDED and left unchanged. It mirrors the Choice copy.
  It does not count as "other content" when deciding display vs inline.
- A picture whose run sits directly in a **paragraph-level `mc:Choice`** (run → `mc:Choice` → `mc:AlternateContent` → `w:p`)
  is replaced like any other run. The Fallback copy keeps its picture.
- A **run-level** `mc:AlternateContent` (run → `mc:AlternateContent` → Choice holding only the picture)
  cannot keep math inside a run. The whole run is replaced by a paragraph-level `mc:AlternateContent` (D17):
  - the original `mc:AlternateContent` and `mc:Choice` start tags (including `Requires`) are reused;
    the Choice holds the equation;
  - the Fallback holds a copy of the original run: the original run start tag and `w:rPr`, with the
    original Fallback content copied verbatim in place of the AlternateContent.
- `RunLocator.ChoiceRequires` records the `Requires` value for reporting.

## OMML conversion

MML2OMML.XSL (shipped with Microsoft Office) is **not** used. `Program.EnvironmentNote` only reports
whether it exists. The converter always uses Pandoc (D14):

- Input `$<latex>$` (newlines flattened, max 4000 characters) through
  `pandoc -f markdown-smart -t docx --sandbox`, 60 s timeout.
- The temporary .docx is opened with `SafePackage`. It must contain exactly one `m:oMath`, in a single
  paragraph with no `w:r`. A leftover text run means Pandoc could not parse part of the TeX.
- Validation: root `m:oMath`; only math-namespace elements from a whitelist; no foreign attributes;
  at least one non-empty `m:t` or `m:chr`; the serialised form must re-parse.

## Gates

Gate 3 runs after each save (`Gates.RunGate3`). Each check records PASS, FAIL or SKIPPED with evidence
in FINAL_REPORT.md and BATCH_SUMMARY.md.

| Check | PASS when | SKIPPED when |
|---|---|---|
| `G3.unit_tests` | `tests\unit_result.json` has `failed == 0` and `passed > 0` | file missing |
| `G3.byte_identity` | Every zip entry is byte-identical except the expected changed parts; each expected part did change; same entry set and order | — |
| `G3.reinventory` | Re-scanning the output: pictures = before − converted, and new `m:oMath` count = converted | — |
| `G3.libreoffice_open` | `soffice --headless --convert-to pdf` produces a non-empty PDF (profile `out\lo_profile`, 300 s timeout) | LibreOffice not installed |
| `G3.openxml_validator` | The output has no validator errors that the input did not already have (error = part, id, description) | validator exe missing |

Gate 0 (every picture has exactly one category and matches the corpus truth) and idempotence (running
the converter on its own output changes nothing) are checked outside the engine by
`scripts\check_gate0.py` and `scripts\check_idempotence.ps1` (see `scripts\README_checks.md`).

## Extension points

| To change | Where | Notes |
|---|---|---|
| Renderer | `workers\verify_worker.py` | Keep the JSON-line protocol and the response fields. The browser is picked from `MATHMORPH_EDGE`, then Edge (x86/x64), then Chrome |
| Score | `score()` in `verify_worker.py` | Re-derive the threshold afterwards (README "Threshold") |
| Recognizer | `workers\pix2text_worker.py` | Any process that takes an image path and returns `ok`/`latex`/`error`. Return `colorful_fraction` and `midtone_fraction`; if they are missing (-1), the content exclusion never fires |
| LaTeX → OMML | `OmmlConverter` | Pandoc only. Using MML2OMML.XSL would need a new implementation (LaTeX → MathML → XSLT) behind the same `Convert` signature. Keep `Validate` |
| Detection rules | `Inventory.Classify`, `Inventory.LogoWords`, `OneInchEmu`; `Pipeline.ColorfulLimit` / `MidtoneLimit` | Re-run `check_gate0.py` against the corpus after any change |
| Replaceable parents | `Pipeline.InlineMathParents`, `NotReplaceable` | Any new parent must still be a valid place for `m:oMath` |
| Safety limits | `SafetyLimits` in `Models.cs` | No command-line switch; change the defaults and rebuild |
| Paths | `EnginePaths` | Root from `MATHMORPH_HOME` or the exe's ancestors |
