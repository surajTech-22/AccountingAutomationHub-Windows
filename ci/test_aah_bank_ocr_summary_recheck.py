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
targets = {"result_json", "_summary_visible", "_recheck_printed_summary", "_analyze_table_alignment"}
funcs = [node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name in targets]
assert len(funcs) == len(targets), "Source crop helper missing."
env = {"Path": Path, "status": lambda *a, **k: None}
exec(compile(ast.Module(body=funcs, type_ignores=[]), "<production-bank-ocr>", "exec"), env)
recover = env["_recheck_printed_summary"]
analyze_table = env["_analyze_table_alignment"]

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
        # Co-op Ope Bal / Clo Bal header-only row must NOT look visible, while
        # header + numeric controls (with blank Ope Bal) MUST pass visibility.
        coop_header_only = {
            "parsing_res_list": [{
                "block_label": "table",
                "block_content": "| Ope Bal | Dr count | Cr count | Debits | Credits | Clo Bal |",
            }],
        }
        assert not env["_summary_visible"](coop_header_only), "Header-only co-op summary falsely satisfied visibility."
        coop_complete = {
            "parsing_res_list": [{
                "block_label": "text",
                "block_content": "Ope Bal Dr count Cr count Debits Credits Clo Bal\n4 6 12,34,567.00 12,45,678.00 11,611.00Cr",
            }],
        }
        assert env["_summary_visible"](coop_complete), "Complete co-op summary was not recognized as visible."
        # Direct-VLM fallback (use_layout_detection=False) recovers borderless
        # summaries omitted by layout detection on Attempts 1 & 2.
        class FallbackPipeline:
            def __init__(self, value):
                self.value = value
                self.calls = []
            def predict_iter(self, **kwargs):
                self.calls.append((kwargs.get("use_layout_detection"), kwargs.get("prompt_label")))
                if kwargs.get("use_layout_detection") is False and kwargs.get("prompt_label") == "table":
                    return iter([Response(self.value)])
                return iter([Response({"parsing_res_list": []})])
        fb_pipe = FallbackPipeline(coop_complete)
        data = blank()
        recovered, ok = recover(Path("synthetic.pdf"), 1, data, out, fb_pipe)
        assert ok and recovered is data and (False, "table") in fb_pipe.calls
        assert env["_summary_visible"](data), "Direct-VLM fallback did not persist recovered co-op summary."
        assert (out / "summary_source_001_03.png").exists(), "Direct-VLM tight summary crop absent."
        # Table alignment analyzer: clean BF + transactions => defect_score == 0;
        # BF merged onto Tx #1 + drifted dated fragments => needs_recheck == True.
        clean_html = (
            "<table><tr><th>Post.Dt</th><th>Val.Dt</th><th>Details</th><th>Debit</th><th>Credit</th><th>Balance</th></tr>"
            "<tr><td></td><td></td><td></td><td>BROUGHT</td><td>FORWARD:</td><td>500.00Cr</td></tr>"
            "<tr><td>01/05/25</td><td>01/05/25</td><td>CASH DEP</td><td></td><td>1,000.00</td><td>1,500.00Cr</td></tr>"
            "<tr><td>02/05/25</td><td>02/05/25</td><td>SMS CHARGE</td><td>20.00</td><td></td><td>1,480.00Cr</td></tr></table>"
        )
        clean_stats = analyze_table(clean_html)
        assert clean_stats is not None and clean_stats["defect_score"] == 0 and not clean_stats["needs_recheck"]
        drifted_html = (
            "<table><tr><th>Post.Dt</th><th>Val.Dt</th><th>Details</th><th>Debit</th><th>Credit</th><th>Balance</th></tr>"
            "<tr><td>01/05/25</td><td>01/05/25</td><td>CASH DEP</td><td>BROUGHT</td><td>FORWARD: 1,000.00</td><td>500.00Cr</td></tr>"
            "<tr><td>02/05/25</td><td>02/05/25</td><td>SMS CHARGE</td><td></td><td></td><td></td></tr>"
            "<tr><td></td><td></td><td>CONT LINE</td><td>20.00</td><td></td><td>1,480.00Cr</td></tr></table>"
        )
        drifted_stats = analyze_table(drifted_html)
        assert drifted_stats is not None and drifted_stats["needs_recheck"] and drifted_stats["defect_score"] > clean_stats["defect_score"]
finally:
    if old_fitz is None:
        sys.modules.pop("fitz", None)
    else:
        sys.modules["fitz"] = old_fitz

print("PASS: Bank OCR summary crop re-recognition preserves source evidence and blocks missing controls.")
