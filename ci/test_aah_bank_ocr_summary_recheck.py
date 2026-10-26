"""Offline regression for source-backed Bank OCR summary-region recovery.

Uses a synthetic PDF renderer and synthetic Paddle responses. Does not load a
model, access an account, or read a real bank statement.
"""
import ast
import sys
import tempfile
import types
from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "AAH/src/AccountingAutomationHub.Desktop/Services/BankImageOcrRecognitionService.cs").read_text(encoding="utf-8-sig")
start = 'private const string RecognitionPython = """'
assert source.count(start) == 1
python_source = source.split(start, 1)[1].split('\n""";', 1)[0]
tree = ast.parse(python_source)
targets = {"result_json", "_summary_visible", "_recheck_printed_summary"}
funcs = [node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name in targets]
assert len(funcs) == len(targets), "Source crop helper missing."
env = {"Path": Path, "status": lambda *a, **k: None}
exec(compile(ast.Module(body=funcs, type_ignores=[]), "<production-bank-ocr>", "exec"), env)
recover = env["_recheck_printed_summary"]

class Rect:
    def __init__(self, x0, y0, x1, y1):
        self.x0, self.y0, self.x1, self.y1 = x0, y0, x1, y1
        self.height = y1 - y0

class Pix:
    def save(self, path):
        Path(path).write_bytes(b"synthetic-crop")

class Page:
    rect = Rect(0, 0, 595, 842)
    def get_pixmap(self, **kwargs):
        clip = kwargs["clip"]
        assert clip.y0 < 510, "Summary crop must include the top of printed summary."
        assert clip.y1 > 550, "Summary crop must include the printed values."
        return Pix()

class Doc:
    page_count = 1
    def __enter__(self):
        return self
    def __exit__(self, *_):
        pass
    def __getitem__(self, index):
        assert index == 0
        return Page()
    def close(self):
        pass

fitz = types.ModuleType("fitz")
fitz.open = lambda source: Doc()
fitz.Rect = Rect
fitz.Matrix = lambda x, y: (x, y)
old_fitz = sys.modules.get("fitz")
sys.modules["fitz"] = fitz

summary = {
    "parsing_res_list": [{
        "block_label": "table",
        "block_content": (
            "STATEMENT SUMMARY\n"
            "| Opening Balance | Dr Count | Cr Count | Debits | Credits | Closing Bal |\n"
            "| 0.00 | 1 | 2 | 25.00 | 100.01 | 75.01 |"
        ),
    }],
}
class Response:
    def __init__(self, value):
        self.json = value

class Pipeline:
    def __init__(self, value):
        self.value = value
        self.calls = 0
    def predict_iter(self, **kwargs):
        self.calls += 1
        assert kwargs["input"].endswith(".png")
        return iter([Response(self.value)])

def blank():
    return {
        "height": 1684,
        "layout_det_res": {"boxes": [
            {"label": "table", "coordinate": [0, 380, 1190, 966]}]},
        "parsing_res_list": [{"block_label": "table", "block_content": "| Date | Debit | Balance |"}],
    }

try:
    with tempfile.TemporaryDirectory() as directory:
        out = Path(directory)
        data = blank()
        recovered, ok = recover(Path("synthetic.pdf"), 1, data, out, Pipeline(summary))
        assert ok and recovered is data and len(data["parsing_res_list"]) == 2
        assert env["_summary_visible"](data), "Full printed controls were not retained."
        assert list(out.glob("summary_source_001_01.png")), "Source crop evidence absent."
        # No independent summary evidence -> the importer stays review-required.
        data = blank()
        not_found, ok = recover(Path("synthetic.pdf"), 1, data, out, Pipeline({"parsing_res_list": []}))
        assert not ok and len(not_found["parsing_res_list"]) == 1
        # Ordinary image/PDF with source summary never gets a redundant crop.
        already = summary.copy()
        same, ok = recover(Path("synthetic.pdf"), 1, already, out, Pipeline({}))
        assert not ok and same is already
        nonpdf, ok = recover(Path("synthetic.png"), 1, blank(), out, Pipeline(summary))
        assert not ok
finally:
    if old_fitz is None:
        sys.modules.pop("fitz", None)
    else:
        sys.modules["fitz"] = old_fitz

print("PASS: Bank OCR summary crop re-recognition preserves source evidence and blocks missing controls.")
