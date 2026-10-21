using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// R46 R39 production Mobile Invoice document-recognition authority.
///
/// The updater provisions only the isolated Paddle invoice OCR runtime under
/// C:\AccountingAutomationHub\OCR\PaddleInvoice.  This service never searches Downloads,
/// never uses the Bank OCR runtime, and never reads the unrelated V15 main/GMFT environment.
/// It invokes Paddle PP-StructureV3 through the permanent Python runtime and normalizes the
/// result into AAH page/block/table JSON.  AAH C# remains the final party/GST/accounting authority.
/// </summary>
internal sealed class InvoicePaddleDocumentOcrRecognitionService
{
    internal const string PermanentRoot = @"C:\AccountingAutomationHub\OCR\PaddleInvoice";
    internal static readonly string PermanentPython = Path.Combine(PermanentRoot, "runtime", "Scripts", "python.exe");
    internal static readonly string PermanentSourceProbe = Path.Combine(PermanentRoot, "v15_paddle_child.py");
    private static readonly SemaphoreSlim GlobalPaddleInvoiceGate = new(1, 1);
    private static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(15);

    public async Task<BankOcrRecognitionBatch> RecognizeRawPagesAsync(
        IReadOnlyList<string> imagePaths,
        string runRoot,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var sources = imagePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var resultRoot = Path.Combine(runRoot, "PADDLE_INVOICE_R39");
        var logPath = Path.Combine(resultRoot, "invoice_paddle.log");
        var statusPath = Path.Combine(resultRoot, "invoice_paddle_status.txt");
        Directory.CreateDirectory(resultRoot);

        if (sources.Length == 0)
            return BankOcrRecognitionBatch.Failed("No captured invoice image is available for Paddle invoice OCR.", logPath, statusPath, resultRoot);

        var runtimeError = ValidatePermanentRuntime();
        if (runtimeError.Length > 0)
            return BankOcrRecognitionBatch.Failed(runtimeError, logPath, statusPath, resultRoot);

        var workerPath = Path.Combine(resultRoot, "AAH_INVOICE_OCR_R39.py");
        await File.WriteAllTextAsync(workerPath, WorkerScript, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        if (GlobalPaddleInvoiceGate.CurrentCount == 0)
            progress?.Invoke("AAH Invoice OCR • waiting for the local Paddle invoice engine");
        await GlobalPaddleInvoiceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteStatusAsync(statusPath, "RUNNING", "Paddle PP-StructureV3 invoice OCR started.", cancellationToken).ConfigureAwait(false);
            await AppendLogAsync(logPath, $"R39 R1 Paddle invoice OCR started for {sources.Length} image(s). Runtime={PermanentPython}", cancellationToken).ConfigureAwait(false);

            var pages = new List<BankOcrRecognitionPage>();
            var errors = new List<string>();
            for (var index = 0; index < sources.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[index];
                var pageJson = Path.Combine(resultRoot, $"page_{index + 1:000}.json");
                progress?.Invoke($"AAH Invoice OCR • page {index + 1}/{sources.Length}: PP-DocLayout/PP-Structure/table cells");
                try
                {
                    var exit = await RunWorkerAsync(workerPath, source, pageJson, index + 1, logPath, cancellationToken).ConfigureAwait(false);
                    if (exit != 0) throw new InvalidOperationException($"Paddle invoice worker exited with code {exit}.");
                    if (!File.Exists(pageJson)) throw new InvalidOperationException("Paddle invoice worker did not create page JSON.");
                    ValidatePageJson(pageJson);
                    pages.Add(new BankOcrRecognitionPage
                    {
                        Id = $"paddle-invoice-page-{index + 1:000}",
                        SourcePath = source,
                        SourceIndex = index,
                        SourcePage = index + 1,
                        JsonPath = pageJson
                    });
                }
                catch (Exception exception)
                {
                    var message = $"{Path.GetFileName(source)}: {exception.Message}";
                    errors.Add(message);
                    await AppendLogAsync(logPath, "ERROR " + message, cancellationToken).ConfigureAwait(false);
                }
            }

            if (pages.Count == 0)
            {
                var message = errors.FirstOrDefault() ?? "Paddle invoice OCR returned no usable page.";
                await WriteStatusAsync(statusPath, "ERROR", message, cancellationToken).ConfigureAwait(false);
                return BankOcrRecognitionBatch.Failed(message, logPath, statusPath, resultRoot);
            }

            await WriteStatusAsync(statusPath, "READY", $"Recognized {pages.Count} invoice page(s) with Paddle document structure.", cancellationToken).ConfigureAwait(false);
            progress?.Invoke($"AAH Invoice OCR • {pages.Count:N0} page(s) ready; applying AAH invoice semantics and GST/accounting checks");
            return new BankOcrRecognitionBatch
            {
                WasSuccessful = true,
                Pages = pages,
                SourceErrors = errors,
                LogPath = logPath,
                StatusPath = statusPath,
                ResultPath = resultRoot
            };
        }
        finally
        {
            GlobalPaddleInvoiceGate.Release();
        }
    }

    // R79: Non-mutating readiness probe reused by Phone Link manual retry.
    // A missing runtime must never make Android ML Kit an accounting authority.
    internal static string GetRuntimeReadinessIssue() => InvoiceOnnxDocumentOcrRecognitionService.GetRuntimeReadinessIssue();

    private static string ValidatePermanentRuntime()
    {
        if (!File.Exists(PermanentPython))
            return $"Permanent Paddle Invoice OCR runtime is missing at {PermanentPython}. Re-run the R39 Windows/Desktop updater so it can copy only the isolated OCR runtime to C:\\AccountingAutomationHub\\OCR\\PaddleInvoice.";

        var modelRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".paddlex", "official_models");
        var required = new[]
        {
            "PP-DocLayoutV3",
            "PP-LCNet_x1_0_table_cls",
            "PP-OCRv5_server_det",
            "PP-OCRv5_server_rec",
            "RT-DETR-L_wired_table_cell_det",
            "RT-DETR-L_wireless_table_cell_det",
            "SLANet_plus"
        };
        var missing = required.Where(name => !Directory.Exists(Path.Combine(modelRoot, name))).ToArray();
        if (missing.Length > 0)
            return "Paddle Invoice OCR model cache is incomplete under " + modelRoot + ". Missing: " + string.Join(", ", missing) + ".";
        return string.Empty;
    }

    private static async Task<int> RunWorkerAsync(
        string workerPath,
        string sourcePath,
        string outputPath,
        int page,
        string logPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PermanentPython,
                WorkingDirectory = PermanentRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = string.Join(" ", new[]
                {
                    Quote(workerPath), "--image", Quote(sourcePath), "--output", Quote(outputPath), "--page", page.ToString(System.Globalization.CultureInfo.InvariantCulture)
                })
            }
        };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PageTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await AppendLogAsync(logPath, $"PAGE {page} EXIT={process.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}", cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void ValidatePageJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var root = document.RootElement;
        if (!root.TryGetProperty("parsing_res_list", out var blocks) || blocks.ValueKind != JsonValueKind.Array || blocks.GetArrayLength() == 0)
            throw new InvalidOperationException("Paddle produced no spatial document blocks.");
        if (!root.TryGetProperty("engine", out var engine) || !engine.ToString().Contains("PADDLE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Paddle page JSON engine identity is missing.");
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static async Task AppendLogAsync(string path, string text, CancellationToken cancellationToken)
    {
        await File.AppendAllTextAsync(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteStatusAsync(string path, string state, string text, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, $"{state}{Environment.NewLine}{text}{Environment.NewLine}", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    // This worker is emitted into the AAH run folder at execution time; no external Python file
    // is packaged or installed by the application.  It normalizes PP-StructureV3's native spatial
    // result into AAH's stable page contract while retaining table HTML/cells and 2-D word boxes.
    private const string WorkerScript = """
from __future__ import annotations
import argparse, json, os, re, traceback
from pathlib import Path
from PIL import Image


def plain(obj, depth=0):
    if depth > 12:
        return str(obj)
    if obj is None or isinstance(obj, (str, int, float, bool)):
        return obj
    if isinstance(obj, dict):
        return {str(k): plain(v, depth+1) for k,v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [plain(v, depth+1) for v in obj]
    for attr in ("json", "res", "result", "data"):
        try:
            value = getattr(obj, attr)
            if callable(value): value = value()
            if value is not None: return plain(value, depth+1)
        except Exception: pass
    for method in ("to_dict", "dict", "model_dump"):
        try:
            fn = getattr(obj, method)
            if callable(fn): return plain(fn(), depth+1)
        except Exception: pass
    try: return plain(vars(obj), depth+1)
    except Exception: return str(obj)


def walk(obj):
    if isinstance(obj, dict):
        yield obj
        for v in obj.values():
            yield from walk(v)
    elif isinstance(obj, list):
        for v in obj: yield from walk(v)


def bbox4(value):
    try:
        if isinstance(value, dict):
            for keys in (("x0","y0","x1","y1"),("left","top","right","bottom"),("xmin","ymin","xmax","ymax")):
                if all(k in value for k in keys): return bbox4([value[k] for k in keys])
        if isinstance(value, (list,tuple)) and len(value)>=4:
            if all(isinstance(p,(list,tuple)) and len(p)>=2 for p in value[:4]):
                xs=[float(p[0]) for p in value]; ys=[float(p[1]) for p in value]
                return [int(round(min(xs))),int(round(min(ys))),int(round(max(xs))),int(round(max(ys)))]
            vals=[float(x) for x in value[:4]]
            return [int(round(min(vals[0],vals[2]))),int(round(min(vals[1],vals[3]))),int(round(max(vals[0],vals[2]))),int(round(max(vals[1],vals[3])))]
    except Exception: return None
    return None


def label(d):
    for k in ("block_label","label","type","category_name","class_name","layout_type"):
        if d.get(k) not in (None,""): return str(d.get(k))
    return "text"


def content(d):
    for k in ("block_content","pred_html","table_html","html","html_res","text","content"):
        v=d.get(k)
        if isinstance(v,str) and v.strip(): return v.strip()
    texts=d.get("rec_texts")
    if isinstance(texts,list) and texts: return " ".join(str(x) for x in texts if str(x).strip())
    return ""


def direct_parsing_lists(obj):
    found=[]
    for d in walk(obj):
        v=d.get("parsing_res_list")
        if isinstance(v,list) and v: found.extend(v)
    return found


def word_boxes(obj):
    out=[]; seen=set()
    for d in walk(obj):
        texts=d.get("rec_texts")
        polys=d.get("rec_polys") or d.get("dt_polys")
        scores=d.get("rec_scores")
        if not isinstance(texts,list) or not isinstance(polys,list) or len(texts)!=len(polys): continue
        for i,(t,p) in enumerate(zip(texts,polys)):
            t=str(t).strip()
            b=bbox4(p)
            if not t or not b: continue
            key=(t,*b)
            if key in seen: continue
            seen.add(key)
            score=0.0
            try:
                if isinstance(scores,list) and i < len(scores): score=float(scores[i])
            except Exception: pass
            out.append({"text":t,"bbox":b,"score":score})
    out.sort(key=lambda x:(x["bbox"][1],x["bbox"][0]))
    for i,x in enumerate(out,1): x["order"]=i
    return out


def normalized_blocks(obj, width, height):
    blocks=[]; seen=set()
    direct=direct_parsing_lists(obj)
    source=direct if direct else list(walk(obj))
    for d in source:
        if not isinstance(d,dict): continue
        b=None
        for k in ("block_bbox","bbox","box","coordinate","coordinates","region","poly","polygon"):
            b=bbox4(d.get(k))
            if b: break
        c=content(d)
        l=label(d)
        if not b or not c: continue
        # Ignore giant wrapper objects that duplicate the whole page unless they are a table.
        area=max(1,(b[2]-b[0])*(b[3]-b[1]))
        if area > width*height*.97 and "table" not in l.lower(): continue
        key=(l.lower(),c[:400],*b)
        if key in seen: continue
        seen.add(key)
        blocks.append({"block_label":l,"block_content":c,"block_bbox":b})

    # Ensure table-native HTML is retained even if parsing_res_list did not expose it.
    for d in walk(obj):
        c=content(d)
        if "<table" not in c.lower() and "<tr" not in c.lower(): continue
        b=None
        for k in ("block_bbox","bbox","box","coordinate","coordinates","region","poly","polygon"):
            b=bbox4(d.get(k))
            if b: break
        if not b: continue
        key=("table",c[:400],*b)
        if key in seen: continue
        seen.add(key)
        blocks.append({"block_label":"table","block_content":c,"block_bbox":b})

    blocks.sort(key=lambda x:(x["block_bbox"][1],x["block_bbox"][0]))
    for i,b in enumerate(blocks,1): b["block_order"]=i
    return blocks


def synthesize_word_blocks(words):
    # Fail-safe spatial fallback: retain each OCR token as an independent 2-D block.
    # AAH then groups by geometry/semantics; this is intentionally NOT flattened into a line.
    return [{"block_label":"text","block_content":w["text"],"block_bbox":w["bbox"],"block_order":i+1} for i,w in enumerate(words)]


def main():
    ap=argparse.ArgumentParser()
    ap.add_argument("--image",required=True)
    ap.add_argument("--output",required=True)
    ap.add_argument("--page",type=int,default=1)
    args=ap.parse_args()
    image=Image.open(args.image).convert("RGB")
    width,height=image.size
    try:
        from paddleocr import PPStructureV3
        model_root=Path.home()/".paddlex"/"official_models"
        def model(name):
            path=model_root/name
            if not path.is_dir(): raise RuntimeError(f"Required local Paddle model missing: {path}")
            return str(path)
        # R39 R1: bind the exact already-proven local model cache.  Do not let PP-StructureV3
        # silently select/download a different table model family during accounting recognition.
        pipeline=PPStructureV3(
            device="cpu",
            layout_detection_model_name="PP-DocLayoutV3",
            layout_detection_model_dir=model("PP-DocLayoutV3"),
            table_classification_model_name="PP-LCNet_x1_0_table_cls",
            table_classification_model_dir=model("PP-LCNet_x1_0_table_cls"),
            wired_table_structure_recognition_model_name="SLANet_plus",
            wired_table_structure_recognition_model_dir=model("SLANet_plus"),
            wireless_table_structure_recognition_model_name="SLANet_plus",
            wireless_table_structure_recognition_model_dir=model("SLANet_plus"),
            wired_table_cells_detection_model_name="RT-DETR-L_wired_table_cell_det",
            wired_table_cells_detection_model_dir=model("RT-DETR-L_wired_table_cell_det"),
            wireless_table_cells_detection_model_name="RT-DETR-L_wireless_table_cell_det",
            wireless_table_cells_detection_model_dir=model("RT-DETR-L_wireless_table_cell_det"),
            text_detection_model_name="PP-OCRv5_server_det",
            text_detection_model_dir=model("PP-OCRv5_server_det"),
            text_recognition_model_name="PP-OCRv5_server_rec",
            text_recognition_model_dir=model("PP-OCRv5_server_rec"),
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_textline_orientation=False,
            use_table_recognition=True,
            use_seal_recognition=False,
            use_formula_recognition=False,
            use_chart_recognition=False,
            use_region_detection=False
        )
        prediction=list(pipeline.predict(args.image))
        native=plain(prediction)
        words=word_boxes(native)
        blocks=normalized_blocks(native,width,height)
        if not blocks and words: blocks=synthesize_word_blocks(words)
        if not blocks: raise RuntimeError("PP-StructureV3 returned no spatial blocks")
        payload={
            "width":width,"height":height,"page":args.page,
            "engine":"AAH-PADDLE-PPSTRUCTUREV3-R39-R1-LOCALMODELS",
            "document_structure_authority":"PP-StructureV3/PP-DocLayout/table-cell geometry; AAH accounting semantics",
            "word_res_list":words,
            "parsing_res_list":blocks
        }
        Path(args.output).write_text(json.dumps(payload,ensure_ascii=False,indent=2),encoding="utf-8")
        print(f"AAH_PADDLE_READY blocks={len(blocks)} words={len(words)}")
        return 0
    except Exception:
        Path(args.output+".error.txt").write_text(traceback.format_exc(),encoding="utf-8")
        traceback.print_exc()
        return 2

if __name__=="__main__":
    raise SystemExit(main())
""";
}
