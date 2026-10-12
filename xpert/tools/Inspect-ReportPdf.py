"""Inspect/render newly generated synthetic report PDFs; never edit source documents."""
import hashlib
import json
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw
from pypdf import PdfReader

root = Path(sys.argv[1]).resolve()
poppler = Path(sys.argv[2]).resolve()
if "build" not in root.parts or not (root / "real-pdf-exports.json").is_file():
    raise SystemExit("Use a completed isolated report QA folder.")
entries = json.loads((root / "real-pdf-exports.json").read_text(encoding="utf-8"))
render = root / "pdf-render"
render.mkdir(exist_ok=False)
records = []
for entry in entries:
    pdf = Path(entry["Pdf"]).resolve()
    if root not in pdf.parents:
        raise RuntimeError("Unexpected non-QA PDF path")
    before = hashlib.sha256(pdf.read_bytes()).hexdigest().upper()
    reader = PdfReader(str(pdf))
    texts = [page.extract_text() or "" for page in reader.pages]
    flattened = "".join("".join(text.split()) for text in texts)
    if not texts or "EPFT" in flattened or "QAStart" not in flattened or "QAEnd" not in flattened or "414.8" not in flattened:
        raise RuntimeError(f"Missing metadata or non-neutral rendered header: {entry['Language']}")
    if "".join(entry["Title"].split()) not in flattened:
        raise RuntimeError(f"PDF title differs from actual selected-language Word title: {entry['Language']}")
    if any("{{report." in text or "{{label." in text for text in texts):
        raise RuntimeError("Unresolved PDF field")
    prefix = render / entry["Language"]
    run = subprocess.run([str(poppler / "pdftoppm.exe"), "-r", "72", "-png", str(pdf), str(prefix)], capture_output=True, timeout=120)
    if run.returncode:
        raise RuntimeError(run.stderr.decode("utf-8", errors="replace"))
    images = sorted(render.glob(entry["Language"] + "-*.png"))
    if len(images) != len(texts):
        raise RuntimeError("Rendered page count mismatch")
    cell_w, cell_h = 397, 582
    sheet = Image.new("RGB", (cell_w * 3, cell_h * ((len(images) + 2) // 3)), "#dfe4e8")
    draw = ImageDraw.Draw(sheet)
    for index, path in enumerate(images):
        with Image.open(path) as page:
            page.thumbnail((cell_w - 8, cell_h - 32))
            x, y = (index % 3) * cell_w + 4, (index // 3) * cell_h + 25
            sheet.paste(page, (x, y))
        draw.text(((index % 3) * cell_w + 8, (index // 3) * cell_h + 6), f"{entry['Language']}  page {index + 1}", fill="black")
    contact = render / (entry["Language"] + "-contact.png")
    sheet.save(contact)
    if hashlib.sha256(pdf.read_bytes()).hexdigest().upper() != before:
        raise RuntimeError("Read-only inspection changed PDF")
    records.append({"Language": entry["Language"], "Pages": len(texts), "PdfSHA256": before,
                    "TextChecksPassed": True, "RenderedAllPages": True, "ContactSheet": str(contact),
                    "ManualVisualReview": "pending", "ExtractedText": texts})
(root / "verification-pdf-render.json").write_text(json.dumps({"Schema": "HVX.Report.PdfReadOnly.1", "SyntheticOnly": True,
    "PdfUnchanged": True, "VisualReviewIsSeparate": True, "Documents": records}, ensure_ascii=False, indent=2), encoding="utf-8")
print("PDF_TEXT_AND_RENDER_PASS=" + str(len(records)) + ";pages=" + str(sum(r["Pages"] for r in records)))
