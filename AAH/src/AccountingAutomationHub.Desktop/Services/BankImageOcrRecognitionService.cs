using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Production Bank-image recognition bridge.
///
/// This service is the production raw-recognition bridge. It does not execute any external OCR
/// worker, verification, rebuild or GUI script.
/// It calls the already-installed external Python runtime + PaddleOCRVL package and the
/// already-installed llama.cpp/GGUF files directly. The result of this stage is raw OCR
/// page JSON only. All document grouping, ordering and financial verification belongs to
/// Accounting Automation Hub and runs after recognition.
/// </summary>
internal sealed class BankImageOcrRecognitionService
{
    private static readonly SemaphoreSlim GlobalRecognitionGate = new(1, 1);

    private const string RecognitionPython = """
import gc, json, os, sys, time, traceback
from pathlib import Path

manifest_path = Path(sys.argv[1])
raw_root = Path(sys.argv[2])
status_path = Path(sys.argv[3])
server_url = sys.argv[4]
cpu_threads = int(sys.argv[5])
model_profile = Path(sys.argv[6])
result_path = Path(sys.argv[7])

raw_root.mkdir(parents=True, exist_ok=True)
os.environ['USERPROFILE'] = str(model_profile)
os.environ['HOME'] = str(model_profile)
os.environ['XDG_CACHE_HOME'] = str(model_profile / '.cache')
os.environ.setdefault('PYTHONUTF8', '1')
# AAH production resource policy: OCR must not monopolize the workstation.
# Limit every common numerical backend before Paddle imports its native libraries.
os.environ['OMP_NUM_THREADS'] = str(cpu_threads)
os.environ['MKL_NUM_THREADS'] = str(cpu_threads)
os.environ['NUMEXPR_NUM_THREADS'] = str(cpu_threads)
os.environ['FLAGS_paddle_num_threads'] = str(cpu_threads)
os.environ['OPENBLAS_NUM_THREADS'] = '1'
os.environ['VECLIB_MAXIMUM_THREADS'] = str(cpu_threads)

def status(stage, message, **extra):
    payload = {
        'stage': stage,
        'message': message,
        'updated_at': time.strftime('%Y-%m-%d %H:%M:%S'),
        **extra,
    }
    tmp = status_path.with_suffix('.tmp')
    tmp.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(tmp, status_path)
    print(f'AAH-PROD-OCR|{stage}|{message}', flush=True)

def result_json(res):
    data = getattr(res, 'json', None)
    if callable(data):
        data = data()
    if isinstance(data, dict) and isinstance(data.get('res'), dict):
        data = data['res']
    return data if isinstance(data, dict) else None

def _summary_visible(data):
    blocks = data.get('parsing_res_list', [])
    text = '\n'.join(str(b.get('block_content', '')) for b in blocks if isinstance(b, dict)).upper()
    # Do not claim that a summary exists from inferred transaction totals.
    return 'STATEMENT SUMMARY' in text or all(
        label in text for label in ('OPENING BAL', 'DR COUNT', 'CR COUNT', 'CLOSING BAL'))

def _recheck_printed_summary(source, page_num, original, raw_root, pipeline):
    # First-pass page layout frequently omits a printed summary directly below
    # the last transaction table. Re-read an enlarged SOURCE-PDF crop with the
    # installed PaddleOCR-VL model, not a guessed/arithmetically built summary.
    if source.suffix.lower() != '.pdf' or _summary_visible(original):
        return original, False
    try:
        try:
            import fitz
        except ImportError:
            import pymupdf as fitz
        with fitz.open(str(source)) as pdf:
            if not (0 < page_num <= pdf.page_count):
                return original, False
            page = pdf[page_num - 1]
            shape = page.rect
            detector = original.get('layout_det_res', {})
            layout_h = max(1, int(original.get('height', 0) or 0))
            bottoms = [
                box.get('coordinate', [0, 0, 0, 0])[3] / layout_h
                for box in detector.get('boxes', [])
                if isinstance(box, dict) and box.get('label') == 'table'
                and len(box.get('coordinate', [])) == 4
            ]
            if bottoms:
                start = max(.12, min(.70, max(bottoms) - .13))
                regions = [(start, min(.95, start + .38)), (.42, .85)]
            else:
                regions = [(.42, .85), (.65, .98)]
            for attempt, (top, bottom) in enumerate(regions[:2], 1):
                region = fitz.Rect(shape.x0, shape.y0 + shape.height * top,
                                   shape.x1, shape.y0 + shape.height * bottom)
                if region.height < 40:
                    continue
                crop_file = raw_root / f'summary_source_{page_num:03d}_{attempt:02d}.png'
                page.get_pixmap(matrix=fitz.Matrix(2.4, 2.4), clip=region,
                                alpha=False).save(str(crop_file))
                for output in pipeline.predict_iter(
                    input=str(crop_file),
                    use_doc_orientation_classify=True,
                    use_doc_unwarping=False,
                    use_layout_detection=True):
                    parsed = result_json(output)
                    if parsed and _summary_visible(parsed):
                        candidate = parsed.get('parsing_res_list', [])
                        original.setdefault('parsing_res_list', []).extend(candidate)
                        # The verifier independently parses the source fields
                        # and requires every control to reconcile exactly.
                        return original, True
    except Exception as exc:
        status('SUMMARY_RECOVERY_SKIPPED',
               f'Source summary re-recognition unavailable: {type(exc).__name__}: {exc}')
    return original, False

try:
    sources = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    if not isinstance(sources, list) or not sources:
        raise RuntimeError('Recognition manifest contains no source files.')

    status('LOAD_PADDLE', f'Loading PaddleOCR-VL 1.6 production recognition for {len(sources)} source file(s).')
    from paddleocr import PaddleOCRVL

    pipeline = PaddleOCRVL(
        pipeline_version='v1.6',
        device='cpu',
        use_doc_orientation_classify=True,
        use_doc_unwarping=True,
        use_layout_detection=True,
        format_block_content=True,
        use_queues=False,
        enable_mkldnn=False,
        cpu_threads=max(1, cpu_threads),
        vl_rec_backend='llama-cpp-server',
        vl_rec_server_url=server_url,
    )

    records = []
    source_errors = []
    total = len(sources)

    for source_index, source_text in enumerate(sources, 1):
        source = Path(source_text)
        if not source.exists():
            source_errors.append({'source': str(source), 'error': 'Source file no longer exists.'})
            continue

        status(
            'SOURCE_OCR',
            f'Recognizing source {source_index} of {total}: {source.name}',
            source=str(source),
            source_index=source_index,
            source_total=total,
        )

        try:
            generator = pipeline.predict_iter(
                input=str(source),
                use_doc_orientation_classify=True,
                use_doc_unwarping=True,
                use_layout_detection=True,
            )
            page_count = 0
            for page_index, res in enumerate(generator, 1):
                page_count += 1
                page_dir = raw_root / f'{source_index:03d}'
                page_dir.mkdir(parents=True, exist_ok=True)
                json_path = page_dir / f'page_{page_index:03d}.json'

                data = result_json(res)
                if data is not None:
                    json_path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
                else:
                    saver = getattr(res, 'save_to_json', None)
                    if not callable(saver):
                        raise RuntimeError('PaddleOCR result does not expose JSON output.')
                    saver(save_path=str(json_path))
                    if not json_path.exists():
                        raise RuntimeError('PaddleOCR did not save raw page JSON.')

                records.append({
                    'id': f'S{source_index:03d}P{page_index:03d}',
                    'source': str(source),
                    'source_index': source_index,
                    'source_page': page_index,
                    'json_path': str(json_path),
                })
                status(
                    'PAGE_OCR_COMPLETE',
                    f'Recognized source {source_index} of {total}, page {page_index}.',
                    source=str(source),
                    source_index=source_index,
                    source_total=total,
                    source_page=page_index,
                    raw_json=str(json_path),
                )
                # Release per-page result/layout objects immediately. The model stays
                # loaded for this batch, but page tensors/images must not accumulate.
                try:
                    del data
                    del res
                except Exception:
                    pass
                gc.collect()

            try:
                del generator
            except Exception:
                pass
            gc.collect()
            if page_count and source.suffix.lower() == '.pdf':
                # Amend the original last-page result, never create a phantom
                # physical page or an independent accounting document.
                last_record = records[-1]
                last_page_file = Path(last_record['json_path'])
                last_page = json.loads(last_page_file.read_text(encoding='utf-8'))
                if isinstance(last_page.get('res'), dict):
                    original_page = last_page['res']
                else:
                    original_page = last_page
                if not _summary_visible(original_page):
                    status('SUMMARY_REGION_RECHECK',
                           'Printed summary not in OCR output; checking the original last-page region.',
                           source=str(source), page=page_count)
                    recovered_page, recovered = _recheck_printed_summary(
                        source, page_count, original_page, raw_root, pipeline)
                    if recovered:
                        last_page_file.write_text(
                            json.dumps(last_page, ensure_ascii=False, indent=2),
                            encoding='utf-8')
                        status('SUMMARY_REGION_RECOVERED',
                               'Additional source-backed summary blocks saved for verifier.',
                               source=str(source), page=page_count)
                    else:
                        status('SUMMARY_REGION_UNREADABLE',
                               'Printed summary not recovered; normal AAH review gate remains active.',
                               source=str(source), page=page_count)
            if page_count == 0:
                source_errors.append({'source': str(source), 'error': 'PaddleOCR-VL returned no page result.'})
        except Exception as exc:
            source_errors.append({
                'source': str(source),
                'error': f'{type(exc).__name__}: {exc}',
                'traceback': traceback.format_exc(),
            })

    try:
        del pipeline
    except Exception:
        pass
    gc.collect()

    final = {
        'stage': 'RECOGNITION_SUCCESS' if records else 'RECOGNITION_FAILED',
        'records': records,
        'source_errors': source_errors,
        'recognized_pages': len(records),
        'source_count': len(sources),
    }
    result_path.write_text(json.dumps(final, ensure_ascii=False, indent=2), encoding='utf-8')

    if not records:
        status('RECOGNITION_FAILED', 'PaddleOCR-VL returned no usable raw page JSON.', source_errors=len(source_errors))
        sys.exit(4)

    status(
        'RECOGNITION_SUCCESS',
        f'Raw recognition completed: {len(records)} page(s). AAH document verification is next.',
        recognized_pages=len(records),
        source_errors=len(source_errors),
        result=str(result_path),
    )
    sys.exit(0)

except Exception as exc:
    tb = traceback.format_exc()
    try:
        result_path.write_text(json.dumps({
            'stage': 'RECOGNITION_FAILED',
            'records': [],
            'source_errors': [{'source': '', 'error': f'{type(exc).__name__}: {exc}', 'traceback': tb}],
        }, ensure_ascii=False, indent=2), encoding='utf-8')
        status('RECOGNITION_FAILED', f'{type(exc).__name__}: {exc}')
    except Exception:
        pass
    print(tb, file=sys.stderr, flush=True)
    sys.exit(5)
""";

    public async Task<BankOcrRecognitionBatch> RecognizeAsync(
        IReadOnlyList<string> sourcePaths,
        BankAdvancedOcrRuntimeInfo runtime,
        string runRoot,
        Action<string>? progress,
        int timeoutMinutes,
        CancellationToken cancellationToken)
    {
        if (GlobalRecognitionGate.CurrentCount == 0)
            progress?.Invoke("Bank OCR • another OCR job is active; waiting without starting a second engine");

        await GlobalRecognitionGate.WaitAsync(cancellationToken);
        try
        {
            return await RecognizeCoreAsync(
                sourcePaths,
                runtime,
                runRoot,
                progress,
                timeoutMinutes,
                cancellationToken);
        }
        finally
        {
            GlobalRecognitionGate.Release();
        }
    }

    private async Task<BankOcrRecognitionBatch> RecognizeCoreAsync(
        IReadOnlyList<string> sourcePaths,
        BankAdvancedOcrRuntimeInfo runtime,
        string runRoot,
        Action<string>? progress,
        int timeoutMinutes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(runRoot);
        var rawRoot = Path.Combine(runRoot, "RawRecognition");
        Directory.CreateDirectory(rawRoot);

        var manifestPath = Path.Combine(runRoot, "recognition_sources.json");
        var statusPath = Path.Combine(runRoot, "recognition_status.json");
        var resultPath = Path.Combine(runRoot, "recognition_result.json");
        var logPath = Path.Combine(runRoot, "recognition.log");
        var llamaStdout = Path.Combine(runRoot, "llama.stdout.log");
        var llamaStderr = Path.Combine(runRoot, "llama.stderr.log");

        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(sourcePaths),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        // Leave most logical processors free for Tally, Excel, browser and the AAH UI.
        // The model is slower than the old near-100% policy, but the desktop stays usable.
        var threads = Math.Clamp(Environment.ProcessorCount / 8, 1, 2);
        var port = GetFreePort();
        var serverUrl = $"http://127.0.0.1:{port}/v1";

        using var overallTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Clamp(timeoutMinutes, 5, 720)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, overallTimeout.Token);

        using var processJob = BankOcrJobObject.TryCreateKillOnClose();
        Process? server = null;
        Process? python = null;
        Task<string>? serverOutTask = null;
        Task<string>? serverErrTask = null;

        try
        {
            progress?.Invoke("Bank OCR • starting local PaddleOCR-VL 1.6 recognition engine");
            var serverInfo = new ProcessStartInfo
            {
                FileName = runtime.LlamaServer,
                WorkingDirectory = Path.GetDirectoryName(runtime.LlamaServer) ?? runtime.RootFolder,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            serverInfo.ArgumentList.Add("-m");
            serverInfo.ArgumentList.Add(runtime.MainModel);
            serverInfo.ArgumentList.Add("--mmproj");
            serverInfo.ArgumentList.Add(runtime.VisionProjector);
            serverInfo.ArgumentList.Add("--host");
            serverInfo.ArgumentList.Add("127.0.0.1");
            serverInfo.ArgumentList.Add("--port");
            serverInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
            serverInfo.ArgumentList.Add("--temp");
            serverInfo.ArgumentList.Add("0");
            serverInfo.ArgumentList.Add("--threads");
            serverInfo.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));

            server = new Process { StartInfo = serverInfo, EnableRaisingEvents = true };
            if (!server.Start())
                return BankOcrRecognitionBatch.Failed("llama.cpp could not be started.", logPath, statusPath, resultPath);
            TrySetBelowNormal(server);
            var serverJobOwned = processJob?.TryAssign(server) ?? false;

            serverOutTask = server.StandardOutput.ReadToEndAsync(linked.Token);
            serverErrTask = server.StandardError.ReadToEndAsync(linked.Token);
            await WaitForPortAsync(port, server, linked.Token);

            progress?.Invoke("Bank OCR • llama.cpp ready; loading PaddleOCR-VL layout/recognition models");

            var modelProfile = Path.Combine(runtime.RootFolder, "_model_profile");
            // Same behavior as the standalone Bank OCR worker: the cache/profile folder
            // is working state, not an installation prerequisite.
            Directory.CreateDirectory(modelProfile);
            var pythonInfo = new ProcessStartInfo
            {
                FileName = runtime.PythonExecutable,
                WorkingDirectory = runtime.RootFolder,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            pythonInfo.ArgumentList.Add("-c");
            pythonInfo.ArgumentList.Add(RecognitionPython);
            pythonInfo.ArgumentList.Add(manifestPath);
            pythonInfo.ArgumentList.Add(rawRoot);
            pythonInfo.ArgumentList.Add(statusPath);
            pythonInfo.ArgumentList.Add(serverUrl);
            pythonInfo.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));
            pythonInfo.ArgumentList.Add(modelProfile);
            pythonInfo.ArgumentList.Add(resultPath);

            python = new Process { StartInfo = pythonInfo, EnableRaisingEvents = true };
            if (!python.Start())
                return BankOcrRecognitionBatch.Failed("The configured Python runtime could not be started.", logPath, statusPath, resultPath);
            TrySetBelowNormal(python);
            var pythonJobOwned = processJob?.TryAssign(python) ?? false;
            progress?.Invoke(
                $"Bank OCR • engine running • Python PID {python.Id} • llama PID {server.Id} • {threads} CPU thread(s)" +
                (serverJobOwned && pythonJobOwned ? " • Windows job-owned" : " • process-tree fallback"));

            var ownershipPath = Path.Combine(runRoot, "process_ownership.json");
            await File.WriteAllTextAsync(
                ownershipPath,
                JsonSerializer.Serialize(new
                {
                    aah_pid = Environment.ProcessId,
                    python_pid = python.Id,
                    llama_pid = server.Id,
                    python_path = runtime.PythonExecutable,
                    llama_path = runtime.LlamaServer,
                    cpu_threads = threads,
                    windows_job_owned = serverJobOwned && pythonJobOwned,
                    started_utc = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            var stdoutTask = python.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = python.StandardError.ReadToEndAsync(linked.Token);
            var waitTask = python.WaitForExitAsync(linked.Token);
            var lastStatus = string.Empty;

            while (!waitTask.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700), linked.Token);
                var message = ReadStatusMessage(statusPath);
                if (message.Length > 0 && !message.Equals(lastStatus, StringComparison.Ordinal))
                {
                    lastStatus = message;
                    progress?.Invoke(message);
                }
            }
            await waitTask;

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await File.WriteAllTextAsync(
                logPath,
                $"PRODUCTION RECOGNITION STDOUT{Environment.NewLine}{stdout}{Environment.NewLine}{Environment.NewLine}PRODUCTION RECOGNITION STDERR{Environment.NewLine}{stderr}",
                CancellationToken.None);

            if (python.ExitCode != 0 || !File.Exists(resultPath))
            {
                var useful = ReadStatusMessage(statusPath);
                if (useful.Length == 0) useful = LastUsefulLine(stderr);
                if (useful.Length == 0) useful = $"PaddleOCR-VL recognition stopped with exit code {python.ExitCode}.";
                return BankOcrRecognitionBatch.Failed(useful, logPath, statusPath, resultPath);
            }

            var batch = ReadRecognitionResult(resultPath, logPath, statusPath);
            if (batch.Pages.Count == 0)
                return BankOcrRecognitionBatch.Failed("PaddleOCR-VL recognition produced no raw page JSON.", logPath, statusPath, resultPath);

            progress?.Invoke($"Bank OCR • raw recognition complete ({batch.Pages.Count:N0} page(s)); AAH verification starting");
            return batch;
        }
        catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(python);
            TryKill(server);
            return BankOcrRecognitionBatch.Failed($"Bank OCR recognition exceeded {timeoutMinutes:N0} minute(s).", logPath, statusPath, resultPath);
        }
        finally
        {
            // Process.Dispose() does NOT terminate a running child. Kill both process trees
            // explicitly so a cancelled/failed OCR cannot remain at high CPU after AAH stops it.
            TryKill(python);
            TryKill(server);
            progress?.Invoke("Bank OCR • recognition engine stopped • owned Python/llama processes released");
            if (serverOutTask is not null)
            {
                try { await File.WriteAllTextAsync(llamaStdout, await serverOutTask, CancellationToken.None); } catch { }
            }
            if (serverErrTask is not null)
            {
                try { await File.WriteAllTextAsync(llamaStderr, await serverErrTask, CancellationToken.None); } catch { }
            }
            python?.Dispose();
            server?.Dispose();
        }
    }

    private static void TrySetBelowNormal(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { }
    }

    private static BankOcrRecognitionBatch ReadRecognitionResult(string path, string logPath, string statusPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var root = document.RootElement;
        var pages = new List<BankOcrRecognitionPage>();
        var errors = new List<string>();

        if (root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in records.EnumerateArray())
            {
                pages.Add(new BankOcrRecognitionPage
                {
                    Id = Text(item, "id"),
                    SourcePath = Text(item, "source"),
                    SourceIndex = Number(item, "source_index"),
                    SourcePage = Number(item, "source_page"),
                    JsonPath = Text(item, "json_path")
                });
            }
        }

        if (root.TryGetProperty("source_errors", out var sourceErrors) && sourceErrors.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sourceErrors.EnumerateArray())
            {
                var source = Text(item, "source");
                var error = Text(item, "error");
                errors.Add(source.Length > 0 ? $"{Path.GetFileName(source)}: {error}" : error);
            }
        }

        return new BankOcrRecognitionBatch
        {
            WasSuccessful = pages.Count > 0,
            Pages = pages,
            SourceErrors = errors,
            LogPath = logPath,
            StatusPath = statusPath,
            ResultPath = path
        };
    }

    private static async Task WaitForPortAsync(int port, Process server, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromMinutes(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (server.HasExited)
                throw new InvalidOperationException($"llama.cpp exited during startup with code {server.ExitCode}.");
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port, cancellationToken);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(400, cancellationToken);
            }
        }
        throw new TimeoutException("Timed out waiting for the local PaddleOCR-VL llama.cpp server.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string ReadStatusMessage(string statusPath)
    {
        try
        {
            if (!File.Exists(statusPath)) return string.Empty;
            using var document = JsonDocument.Parse(File.ReadAllText(statusPath, Encoding.UTF8));
            return Text(document.RootElement, "message");
        }
        catch { return string.Empty; }
    }

    private static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static int Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
    }

    private static string LastUsefulLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => line.Length > 0) ?? string.Empty;

    private static void TryKill(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}

internal sealed class BankOcrRecognitionBatch
{
    public bool WasSuccessful { get; set; }
    public IReadOnlyList<BankOcrRecognitionPage> Pages { get; set; } = [];
    public IReadOnlyList<string> SourceErrors { get; set; } = [];
    public string LogPath { get; set; } = string.Empty;
    public string StatusPath { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public string FailureMessage { get; set; } = string.Empty;

    public static BankOcrRecognitionBatch Failed(string message, string logPath, string statusPath, string resultPath) =>
        new()
        {
            FailureMessage = message,
            LogPath = logPath,
            StatusPath = statusPath,
            ResultPath = resultPath,
            WasSuccessful = false
        };
}

internal sealed class BankOcrRecognitionPage
{
    public string Id { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public int SourceIndex { get; set; }
    public int SourcePage { get; set; }
    public string JsonPath { get; set; } = string.Empty;
}
