using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// R90: Independent ONNX Phone Link invoice OCR; isolated from Paddle and Bank OCR.
/// Windows background worker returns source-identified polygons and R89 human-review evidence.
/// This is NOT an accounting import/ledger/master authority.
/// </summary>
internal sealed class InvoiceOnnxDocumentOcrRecognitionService
{
    internal const string PermanentRoot = @"C:\AccountingAutomationHub\OCR\PhoneLinkInvoice";
    internal static readonly string PermanentPython = Path.Combine(PermanentRoot, "rapidocr_r88", "Scripts", "python.exe");
    private static readonly SemaphoreSlim GlobalOnnxInvoiceGate = new(1, 1);
    private static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(7);

    internal static string GetRuntimeReadinessIssue() => File.Exists(PermanentPython)
        ? string.Empty
        : $"Independent Phone Link RapidOCR/ONNX runtime is missing at {PermanentPython}. Install and verify R88 before retrying this scan.";

    public async Task<BankOcrRecognitionBatch> RecognizeRawPagesAsync(IReadOnlyList<string> imagePaths,
        string runRoot, Action<string>? progress, CancellationToken cancellationToken)
    {
        var sources = imagePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var resultRoot = Path.Combine(runRoot, "ONNX_INVOICE_R90");
        Directory.CreateDirectory(resultRoot);
        var log = Path.Combine(resultRoot, "invoice_onnx.log");
        var status = Path.Combine(resultRoot, "invoice_onnx_status.txt");
        if (sources.Length == 0) return BankOcrRecognitionBatch.Failed("Original Phone Link invoice images are missing.", log, status, resultRoot);
        var issue = GetRuntimeReadinessIssue();
        if (issue.Length > 0) return BankOcrRecognitionBatch.Failed(issue, log, status, resultRoot);

        // Generated INSIDE run folder at runtime; no external Python shipped by app updater.
        foreach (var entry in new[] {
            ("AAH_R88_WORKER.py", R88Worker),
            ("AAH_R88_LAYOUT.py", R88Layout),
            ("AAH_R89_VERIFY_R88_BLOCKS.py", R89Verifier),
            ("AAH_R90_MAIN.py", R90Controller) })
            await File.WriteAllTextAsync(Path.Combine(resultRoot, entry.Item1), entry.Item2, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        await GlobalOnnxInvoiceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(status, "RUNNING\nONNX invoice spatial OCR started.\n", cancellationToken).ConfigureAwait(false);
            var pages = new List<BankOcrRecognitionPage>();
            var errors = new List<string>();
            for (var i = 0; i < sources.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[i];
                var pageJson = Path.Combine(resultRoot, $"page_{i+1:000}.json");
                progress?.Invoke($"Phone Link ONNX OCR • image {i+1}/{sources.Length}, spatial polygons + R89 accounting review");
                try
                {
                    // AAH has one real exit to Purchase/Sales; this worker only prepares review evidence.
                    int exit = await RunAsync(Path.Combine(resultRoot, "AAH_R90_MAIN.py"), source, pageJson, i+1, log, cancellationToken).ConfigureAwait(false);
                    if (exit != 0) throw new InvalidOperationException($"Independent ONNX invoice worker exited with code {exit}.");
                    ValidatePage(pageJson, source);
                    pages.Add(new BankOcrRecognitionPage { Id=$"onnx-invoice-page-{i+1:000}", SourcePath=source, SourceIndex=i, SourcePage=i+1, JsonPath=pageJson });
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
                    await File.AppendAllTextAsync(log, "ERROR " + errors[^1] + Environment.NewLine, cancellationToken).ConfigureAwait(false);
                }
            }
            if (pages.Count==0)
            {
                var failure=errors.FirstOrDefault() ?? "ONNX returned no valid invoice page.";
                await File.WriteAllTextAsync(status, "ERROR\n"+failure+"\n", cancellationToken).ConfigureAwait(false);
                return BankOcrRecognitionBatch.Failed(failure, log, status, resultRoot);
            }
            await File.WriteAllTextAsync(status, "READY_FOR_REVIEW\nPolygon OCR completed; accounting discrepancies remain subject to review.\n", cancellationToken).ConfigureAwait(false);
            return new BankOcrRecognitionBatch { WasSuccessful=true, Pages=pages, SourceErrors=errors, LogPath=log, StatusPath=status, ResultPath=resultRoot };
        }
        finally { GlobalOnnxInvoiceGate.Release(); }
    }

    private static async Task<int> RunAsync(string worker, string image, string output, int page, string log, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo { FileName=PermanentPython, WorkingDirectory=Path.GetDirectoryName(worker)!,
            UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        psi.ArgumentList.Add(worker); psi.ArgumentList.Add("--image"); psi.ArgumentList.Add(image);
        psi.ArgumentList.Add("--output"); psi.ArgumentList.Add(output); psi.ArgumentList.Add("--page"); psi.ArgumentList.Add(page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = new Process { StartInfo=psi };
        if (!process.Start()) throw new InvalidOperationException("Cannot start isolated ONNX invoice worker.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PageTimeout);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree:true); } catch { }
            throw;
        }
        await File.AppendAllTextAsync(log, $"PAGE={page} EXIT={process.ExitCode}\nSTDOUT:\n{await stdout.ConfigureAwait(false)}\nSTDERR:\n{await stderr.ConfigureAwait(false)}\n", cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void ValidatePage(string page, string source)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(page, Encoding.UTF8));
        var root=document.RootElement;
        if (root.GetProperty("engine").GetString()!="AAH-RAPIDOCR-ONNX-R90") throw new InvalidOperationException("Untrusted invoice OCR engine output.");
        if (root.GetProperty("parsing_res_list").GetArrayLength()<3) throw new InvalidOperationException("No spatial OCR polygons.");
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        if (!string.Equals(sourceHash,root.GetProperty("source_sha256").GetString(),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OCR output source photograph SHA-256 mismatch.");
        if (root.GetProperty("accounting_preview").GetProperty("schema").GetString()!="AAH-R89-ACCOUNTING-BLOCK-PREVIEW-V1" ||
            root.GetProperty("accounting_preview").GetProperty("import_allowed").GetBoolean())
            throw new InvalidOperationException("R89 review authority contract not satisfied.");
    }

    private const string R88Worker = """"
#!/usr/bin/env python3
"""Read-only RapidOCR engine invocation; one image orientation per bounded child process."""
import argparse, hashlib, json, os, sys, time, traceback
from pathlib import Path

def log(*args):
    print(time.strftime('%H:%M:%S'), *args, flush=True)

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--image',required=True); p.add_argument('--degrees', type=int, required=True,choices=[0,90,180,270]);p.add_argument('--output', required=True)
    a=p.parse_args()
    import cv2, numpy as np
    from rapidocr import RapidOCR
    raw=Path(a.image).read_bytes()
    orig=cv2.imdecode(np.frombuffer(raw,dtype=np.uint8),cv2.IMREAD_COLOR)
    if orig is None:raise RuntimeError('Cannot decode selected image')
    hh,ww=orig.shape[:2]
    if a.degrees==90: orig=cv2.rotate(orig,cv2.ROTATE_90_COUNTERCLOCKWISE)
    elif a.degrees==180: orig=cv2.rotate(orig,cv2.ROTATE_180)
    elif a.degrees==270: orig=cv2.rotate(orig,cv2.ROTATE_90_CLOCKWISE)
    largest=max(orig.shape[:2]); scale=min(1.0, 1600.0/largest)
    img=cv2.resize(orig,None,fx=scale,fy=scale,interpolation=cv2.INTER_AREA) if scale<1 else orig
    log('IMAGE',ww,hh,'rotation',a.degrees,'result_dims',img.shape[1],img.shape[0],'scale',round(scale,5))
    log('IMPORTS_OK','ONNX_RAPIDOCR_ENGINE_START')
    start=time.monotonic()
    engine=RapidOCR()
    log('ENGINE_READY',round(time.monotonic()-start,2),'sec')
    out=engine(img,return_word_box=True)
    texts=list(getattr(out,'txts',[]) or [])
    boxraw=getattr(out,'boxes',None)
    score_raw=getattr(out,'scores',None)
    boxes=boxraw.tolist() if hasattr(boxraw,'tolist') else list(boxraw or [])
    scores=score_raw.tolist() if hasattr(score_raw,'tolist') else list(score_raw or [])
    items=[]
    for i,text in enumerate(texts):
        poly=boxes[i] if i<len(boxes) else None
        if not isinstance(poly,(list,tuple)) or len(poly)<4:continue
        coords=[[float(x),float(y)] for x,y in poly[:4]]
        conf=float(scores[i]) if i<len(scores) else 0.
        minx=min(c[0] for c in coords);maxx=max(c[0] for c in coords)
        miny=min(c[1] for c in coords);maxy=max(c[1] for c in coords)
        items.append({'text':str(text),'confidence':round(conf,5),'polygon_xy':coords,'bbox_xyxy':[round(minx,2),round(miny,2),round(maxx,2),round(maxy,2)]})
    log('OCR_FINISHED',len(items),'bounded_polygons',round(time.monotonic()-start,2),'sec')
    score=sum(max(.0,min(1.,x['confidence']))*(1. if sum(c.isalnum() for c in x['text'])>=3 else .2) for x in items)
    st=' '.join(x['text'].upper() for x in items)
    for token in ['INVOICE','GSTIN','HSN','TAXABLE','SGST','CGST','TOTAL']:
        if token in st:score+=2.0
    obj={'engine':'RapidOCR-ONNX','state':'completed','rotation_ccw_degrees':a.degrees,
         'original_height_width':[hh,ww],'upright_height_width':list(img.shape[:2]),
         'image_sha256':hashlib.sha256(raw).hexdigest().upper(),'raw_ocr_line_count':len(items),
         'orientation_selection_score':round(score,4),'elapsed_seconds':round(time.monotonic()-start,2),'lines':items,
         'human_verified':False, 'accounting_authority':False}
    Path(a.output).write_text(json.dumps(obj,indent=2,ensure_ascii=False),encoding='utf-8')
    log('RESULT_SAVED',a.output)

if __name__=='__main__':
    try:main()
    except BaseException as exc:
        log('WORKER_ERROR',type(exc).__name__,str(exc));traceback.print_exc();sys.exit(2)
"""";
    private const string R88Layout = """"
#!/usr/bin/env python3
"""Geometry-oriented grouping of text POLYGONS; no implied field verification."""
import argparse,hashlib,json,re
from pathlib import Path

def assign(rows, w, h):
    """Assign semantic blocks from document anchors, not a fixed two-column header."""
    ordered = sorted(rows, key=lambda r: (r['bbox_xyxy'][1], r['bbox_xyxy'][0]))
    def center(r):
        bb = r['bbox_xyxy']; return ((bb[0] + bb[2]) / 2, (bb[1] + bb[3]) / 2)
    def t(r): return ' '.join(r['text'].upper().split())
    def is_goods_header(r):
        text = t(r)
        return bool(re.match(r'^(?:S\.?\s*L\.?|S\.?\s*N\.?|SR\.?|PRODUCT|DESCRIPTIONS?\s+(?:OF\s+)?GOODS|HSN\.?|HSN/SA|QTY|QUANTITY|MRP|RATE|DISC\s*%|GST\s*%|AMOUNT|FREE)$', text))
    candidates = [r for r in ordered if is_goods_header(r)]
    # Header candidates need local peers in a common horizontal band, rejecting stray footer labels.
    bands = [(r, sum(abs(center(r)[1]-center(other)[1]) <= max(26, h*.025) for other in candidates)) for r in candidates]
    cluster = [r for r, n in bands if n >= 3]
    goods_y = min([r['bbox_xyxy'][1] for r in cluster], default=h*.28)
    subtotal = [r['bbox_xyxy'][1] for r in ordered if re.search(r'\b(?:SUB\s*TOTAL|CLASS|TAXABLE|GRAND\s+TOTAL|SGST\s+PAYABLE)\b', t(r)) and r['bbox_xyxy'][1] > goods_y + max(40,h*.055)]
    footer_y = min(subtotal, default=h*.75)
    footer_y = max(goods_y+max(45,h*.06), footer_y)
    footer_label = [r['bbox_xyxy'][1] for r in ordered if re.search(r'^(?:TERMS\s*&|E\.?\s*&\s*O\.?|RECEIVER|FOR\s+\w+)', t(r)) and r['bbox_xyxy'][1]>footer_y+30]
    footer_end=min(footer_label,default=min(h*.94, footer_y+max(135,h*.15)))
    # Distinguish the buyer block even when it is directly BELOW the supplier on the LEFT.
    buyer_label = next((r for r in ordered if re.fullmatch(r'BUYER|BILL\s+TO|BILLED\s+TO',t(r)) and center(r)[1]<goods_y),None)
    buyer_top = buyer_label['bbox_xyxy'][1] if buyer_label else None
    buyer_m_s = next((r for r in ordered if re.match(r'^M\s*/\s*S\s*:',t(r)) and center(r)[1]<goods_y), None)
    meta = [r for r in ordered if re.search(r'^(?:INVOICE\s*NO\.?|DATE\b|TAX\s+INVOICE)',t(r)) and center(r)[1]<goods_y]
    meta_y=min((r['bbox_xyxy'][1] for r in meta),default=goods_y)
    slab_hdr = [r for r in ordered if t(r) in ('CLASS','TAXABLE','TOTAL GST') and footer_y-20<=center(r)[1]<footer_end]
    slab_right=max((r['bbox_xyxy'][2] for r in slab_hdr),default=0)
    slab_cut = min(w*.69,slab_right+max(60,w*.075)) if len(slab_hdr)>=2 else -1
    groups={k:[] for k in ('supplier_header','buyer_header','invoice_identifiers','goods_table','gst_slab','invoice_totals','footer_other')}
    for row in ordered:
        x,y=center(row); content=t(row)
        if y<goods_y:
            if buyer_label and buyer_top is not None and y>=buyer_top-5 and x<=w*.54:
                zone='buyer_header'
            elif buyer_m_s and x>=buyer_m_s['bbox_xyxy'][0]-max(35,w*.035) and y<meta_y:
                zone='buyer_header'
            elif y>=meta_y and x>w*.43:
                zone='invoice_identifiers'
            else:
                zone='supplier_header'
        elif y<footer_y:
            zone='goods_table'
        elif y<footer_end:
            zone='gst_slab' if slab_cut>0 and x<slab_cut else 'invoice_totals'
        else:
            zone='footer_other'
        groups[zone].append(row)
    return groups,{'goods_header_y':round(goods_y,1),'financial_breakdown_y':round(footer_y,1),
                   'footer_y':round(footer_end,1),'buyer_anchor_y':round(buyer_top,1) if buyer_top is not None else None,
                   'slab_divider_x':round(slab_cut,1) if slab_cut>0 else None,
                   'rule':'R91 heading-anchored semantic regions and spatial table header evidence; needs review'}

def prepare(raw, outprefix):
    import cv2,numpy as np
    input_file=Path(raw['input_image']); bytes_image=input_file.read_bytes()
    if hashlib.sha256(bytes_image).hexdigest().upper()!=raw['image_sha256']:
        raise RuntimeError('Source image hash changed after recognition; refusing to annotate')
    a=cv2.imdecode(np.frombuffer(bytes_image,dtype=np.uint8),cv2.IMREAD_COLOR)
    deg=raw['rotation_ccw_degrees'];
    if deg==90:a=cv2.rotate(a,cv2.ROTATE_90_COUNTERCLOCKWISE)
    elif deg==180:a=cv2.rotate(a,cv2.ROTATE_180)
    elif deg==270:a=cv2.rotate(a,cv2.ROTATE_90_CLOCKWISE)
    oh,ow=raw['upright_height_width'];a=cv2.resize(a,(ow,oh),interpolation=cv2.INTER_AREA)
    groups,bounds=assign(raw['lines'],ow,oh)
    # A bounded deterministic structural line mask. Ruled-cell intersections are evidence,
    # not a guarantee that every apparent character/row is segmented correctly.
    grey=cv2.cvtColor(a,cv2.COLOR_BGR2GRAY)
    b=cv2.adaptiveThreshold(grey,255,cv2.ADAPTIVE_THRESH_GAUSSIAN_C,cv2.THRESH_BINARY_INV,31,14)
    hk=cv2.getStructuringElement(cv2.MORPH_RECT,(max(18,ow//30),1))
    vk=cv2.getStructuringElement(cv2.MORPH_RECT,(1,max(18,oh//32)))
    hmask=cv2.morphologyEx(b,cv2.MORPH_OPEN,hk)
    vmask=cv2.morphologyEx(b,cv2.MORPH_OPEN,vk)
    evidence=cv2.bitwise_or(hmask,vmask)
    cnts,_=cv2.findContours(evidence,cv2.RETR_LIST,cv2.CHAIN_APPROX_SIMPLE)
    ruling=[]
    for cnt in cnts:
        x,y,w,h=cv2.boundingRect(cnt)
        if (w>ow*.15 and h<oh*.06) or (h>oh*.14 and w<ow*.06):
            ruling.append([int(x),int(y),int(x+w),int(y+h)])
    ruling=sorted(ruling,key=lambda t:(t[1],t[0]))[:700]
    palette={'supplier_header':(0,150,0),'buyer_header':(200,100,0),'invoice_identifiers':(175,0,150),
             'goods_table':(0,0,210),'gst_slab':(180,0,130),'invoice_totals':(0,140,200),'footer_other':(100,100,100)}
    for name,rows in groups.items():
        color=palette[name]
        for r in rows:
            pts=np.array(r['polygon_xy'],dtype=np.int32).reshape((-1,1,2))
            cv2.polylines(a,[pts],True,color,2)
    # no text annotations written over invoice; bounding polygons only.
    overlay=Path(str(outprefix)+'_BLOCK_POLYGONS.png')
    if not cv2.imwrite(str(overlay),a):raise RuntimeError('Cannot write overlay')
    obj={'schema':'AAH_R88_BLOCK_EVIDENCE_V1','status':'NEEDS_HUMAN_REVIEW','orientation_ccw_degrees':deg,
         'source_sha256':raw['image_sha256'],'source_image':str(input_file),
         'upright_height_width':[oh,ow],'ocr_engine':'RapidOCR-ONNX','ocr_line_count':len(raw['lines']),
         'coordinate_space':'upright-rescaled-image-pixels','orientation_score':raw['orientation_selection_score'],
         'detected_ruling_candidates':ruling,'zone_breakpoints':bounds,'zones':groups,
         'rules':['Raw polygons and text are preserved unchanged','No supplier/buyer/GSTIN is inferred from text alone',
                  'Rows not verified against invoice totals','Not valid for AAH Purchase/Sales import or Tally export without verification']}
    path=Path(str(outprefix)+'_BLOCK_EVIDENCE.json');path.write_text(json.dumps(obj,indent=2,ensure_ascii=False),encoding='utf-8')
    return path,overlay,obj

if __name__=='__main__':
    ap=argparse.ArgumentParser();ap.add_argument('json');ap.add_argument('outprefix');args=ap.parse_args()
    obj=json.loads(Path(args.json).read_text(encoding='utf-8'))
    a,b,out=prepare(obj,args.outprefix)
    print(a,b,{k:len(v) for k,v in out['zones'].items()})
"""";
    private const string R89Verifier = """"
#!/usr/bin/env python3
"""Read-only evidence-linked accounting invoice mapping of R88 spatial OCR JSON.

No OCR engine needed; no account/Tally mutations; never asserts import authority.
A tentative table parser: supports dynamic heading-centres and a strict row gate;
unsupported layouts are reported as unresolved rather than guessed.
"""
from __future__ import annotations
import argparse
import datetime as dt
import hashlib
import json
import re
from collections import Counter
from decimal import Decimal, ROUND_HALF_UP, InvalidOperation
from pathlib import Path

SCHEMA = "AAH-R89-ACCOUNTING-BLOCK-PREVIEW-V1"
CHARSET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"
D2 = Decimal('0.01')

def dec(s):
    if s is None: return None
    try:
        s = str(s).replace(',','').replace('₹','').strip()
        if not re.fullmatch(r'-?\d+(?:\.\d+)?', s): return None
        return Decimal(s)
    except InvalidOperation: return None

def money(v):
    if v is None: return None
    return str(v.quantize(D2, rounding=ROUND_HALF_UP))

def gstin_check(s):
    s = str(s).upper().strip()
    if not re.fullmatch(r'[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][A-Z0-9]Z[A-Z0-9]', s): return False
    total = 0
    for idx, ch in enumerate(s[:14]):
        n=CHARSET.index(ch)*(1+(idx%2))
        total += n//36+n%36
    return CHARSET[(36-total%36)%36] == s[14]

def coord(item):
    b = item['bbox_xyxy']; return ((b[0]+b[2])/2,(b[1]+b[3])/2)

def ref(item):
    return {'id':item['_id'], 'text':item['text'], 'confidence':item['confidence'],
            'bbox_xyxy':item['bbox_xyxy'], 'polygon_xy':item['polygon_xy']}

def field(value, items):
    return {'value':value, 'source_boxes':[ref(x) for x in items]}

def unique(entries):
    return list(dict.fromkeys(x['_id'] for x in entries))

def read_input(p):
    raw = p.read_bytes(); data = json.loads(raw.decode('utf-8-sig'))
    if data.get('schema')!='AAH_R88_BLOCK_EVIDENCE_V1' or data.get('ocr_engine')!='RapidOCR-ONNX':
        raise ValueError('Expected complete AAH R88 block evidence JSON, not an unrelated OCR/preview file')
    h,w = data['upright_height_width']
    if not (100<=h<=12000 and 100<=w<=12000):raise ValueError('Bad image dimensions')
    if not re.fullmatch('[0-9A-F]{64}',data['source_sha256']):raise ValueError('Invalid source SHA-256')
    all_items=[]
    for z,items in data['zones'].items():
        for i, obj in enumerate(items):
            # copy while retaining raw OCR evidence untouched
            r=dict(obj);r['_id']=f'{z}:{i}';r['_zone']=z
            bb=r.get('bbox_xyxy');poly=r.get('polygon_xy')
            if not (isinstance(bb,list) and len(bb)==4 and 0<=bb[0]<=bb[2]<=w and 0<=bb[1]<=bb[3]<=h):
                raise ValueError('Invalid OCR box '+r['_id'])
            if not (isinstance(poly,list) and len(poly)==4):raise ValueError('Invalid OCR polygon '+r['_id'])
            all_items.append(r)
    if len(all_items)!=data['ocr_line_count']:raise ValueError('R88 OCR region count mismatch: preserve every polygon')
    if len({x['_id'] for x in all_items})!=len(all_items):raise ValueError('Duplicate line ID')
    return data,all_items,hashlib.sha256(raw).hexdigest().upper()

def one(rows,pattern):
    matches=[r for r in rows if re.search(pattern,r['text'],re.I)]
    return matches[0] if len(matches)==1 else None

def gst_party(rows):
    gs=[]
    for r in rows:
        m=re.search(r'(?:GSTIN|GSTN)(?:\s+NO\.?)?\s*[:.\-]*\s*([A-Z0-9]{15})',r['text'].upper())
        if m:gs.append((m.group(1),r))
    ident=field(gs[0][0],[gs[0][1]]) if len(gs)==1 else field(None,[])
    # Choose the earliest plausible heading in the party's own zone; do not assume
    # a particular firm name, suffix, GSTIN, or client. Never use bank/footer text.
    sorted_rows=sorted(rows,key=lambda r:(coord(r)[1],coord(r)[0]))
    names=[]
    for r in sorted_rows:
        t=r['text'].strip();u=t.upper()
        if any(token in u for token in ('GSTIN','GSTN','PHONE','PH.NO','STATE:', 'STATE NAME','TAX INVOICE','TUMKUR-','KARNATAKA','APMC YARD','ADDRESS:','E-WAY','INVOICE NO','MODE/TERMS','DESTINATION','DELIVERY','TRANSPORT','FOR RECIPIENT')):continue
        if re.fullmatch(r'BUYER|BILL\s+TO|S\.?L\.?|RECEIVER|DATE|DATED',u):continue
        if re.search(r'\d{3,}',u):continue
        if len(re.findall(r'[A-Za-z]{2,}',t))<2:continue
        if not re.match(r'^(M\s*/\s*S\s*:?\s*)?[A-Za-z]',t,re.I):continue
        names.append(r)
    if not names:
        selected=None
    else:
        selected=names[0]
    name=field(re.sub(r'^\s*M\s*/\s*S\s*:\s*','',selected['text'],flags=re.I).strip(),[selected]) if selected else field(None,[])
    return {'name':name,'gstin':ident,'gstin_checksum_valid':gstin_check(ident['value']) if ident['value'] else None,
            'address_evidence':[ref(r) for r in rows if r is not selected and r not in [p[1] for p in gs]]}

def determine_columns(goods, flags):
    # Heading centres come from the actual page, not an absolute y<400 threshold.
    labels={
        'serial':r'^(?:S\.?\s*L\.?|S\.?\s*N\.?|SR\.?)$',
        'description':r'^(?:PRODUCT|DESCRIPTIONS?\s+(?:OF\s+)?GOODS)$',
        'hsn':r'^HSN\.?|^HSN/SA', 'quantity':r'^(?:QTY|QUANTITY)$', 'free':r'^FREE$',
        'mrp':r'^MRP$', 'rate':r'^RATE$', 'discount':r'^DISC\s*%$',
        'gst_rate':r'^GST\s*%$', 'line_amount':r'^AMOUNT$'}
    candidates={}
    for key,pat in labels.items():
        matches=[r for r in goods if re.search(pat,r['text'].strip(),re.I)]
        candidates[key]=matches
    anchor=list(candidates['description'])
    if len(anchor)!=1:
        flags.append('GOODS_DESCRIPTION_HEADER_MISSING_OR_AMBIGUOUS')
        return {},{},
    baseline=coord(anchor[0])[1]
    hdr={}
    for key,entries in candidates.items():
        near=[r for r in entries if abs(coord(r)[1]-baseline)<=max(28,baseline*.04)]
        if len(near)==1:hdr[key]=near[0]
        elif len(near)>1:flags.append('DUPLICATE_GOODS_HEADER_'+key.upper())
        elif key in ('line_amount','quantity','hsn','rate','gst_rate'):
            flags.append('MISSING_HEADER_'+key.upper())
    if 'line_amount' not in hdr:return {},hdr
    ordered=sorted(hdr,key=lambda k:coord(hdr[k])[0]);xs=[coord(hdr[k])[0] for k in ordered]
    if len(xs)!=len(set(xs)):
        flags.append('COLLIDING_HEADER_CENTRES');return {},hdr
    boundaries=[float('-inf')]+[(xs[i]+xs[i+1])/2 for i in range(len(xs)-1)]+[float('inf')]
    return {k:(boundaries[i],boundaries[i+1]) for i,k in enumerate(ordered)},hdr

def classify_goods(goods, flags):
    cols, headers=determine_columns(goods,flags)
    if not cols:return [],[],headers,cols
    bottom=max(r['bbox_xyxy'][3] for r in headers.values())
    cell_items={}
    for r in goods:
        if r in headers.values() or r['bbox_xyxy'][1] < bottom-6:continue
        x,y=coord(r)
        k=next((k for k,(lo,hi) in cols.items() if lo<=x<hi),None)
        if k:cell_items[r['_id']]=(k,r)
    descs=[r for k,r in cell_items.values() if k=='description' and re.search(r'[A-Za-z]{3}',r['text'])
           and not re.search(r'(?i)SCAN TO|QR|DIGITAL PURCHASE|MARG ERP|PERSONALLY TRUSTED|TERMS',r['text'])]
    amounts=[r for k,r in cell_items.values() if k=='line_amount' and dec(r['text']) is not None]
    rows=[]; used=set()
    for anchor in sorted(descs,key=lambda r:coord(r)[1]):
        y=coord(anchor)[1]
        # A real item needs a numeric value in the printed amount column on the same physical row.
        am=[r for r in amounts if abs(coord(r)[1]-y)<=max(24,(r['bbox_xyxy'][3]-r['bbox_xyxy'][1])*1.15) and r['_id'] not in used]
        if len(am)!=1:continue
        chosen={'description':anchor,'line_amount':am[0]}
        for key in cols:
            if key in chosen:continue
            nearby=[r for k,r in cell_items.values() if k==key and abs(coord(r)[1]-y)<=max(24,(r['bbox_xyxy'][3]-r['bbox_xyxy'][1])*1.15) and r['_id'] not in used]
            if len(nearby)==1:chosen[key]=nearby[0]
            elif len(nearby)>1 and key not in ('serial',):flags.append('AMBIGUOUS_GOODS_CELL_'+key.upper())
        if 'hsn' not in chosen:flags.append('GOODS_HSN_UNVERIFIED')
        if 'quantity' not in chosen or dec(chosen['quantity']['text']) is None:
            flags.append('GOODS_QUANTITY_UNVERIFIED')
        if 'gst_rate' not in chosen or not re.fullmatch(r'\s*\d+(?:\.\d+)?\s*%?\s*',chosen['gst_rate']['text']):
            flags.append('GOODS_GST_RATE_UNVERIFIED')
        row={k:field(r['text'],[r]) for k,r in chosen.items()}
        rows.append(row);used.update(r['_id'] for r in chosen.values())
    ignored=[r for r in goods if r not in headers.values() and r['_id'] not in used]
    return rows,ignored,headers,cols

def find_nearest_numeric(entries, lab, x_min=None, max_delta=23):
    candidates=[];lab_y=coord(lab)[1]
    for e in entries:
        if e is lab or dec(e['text']) is None:continue
        cx,cy=coord(e)
        if x_min is not None and cx<x_min:continue
        if abs(cy-lab_y)<=max_delta:candidates.append((abs(cy-lab_y),-cx,e))
    candidates.sort(key=lambda x:(x[0],x[1]))
    return candidates[0][2] if candidates else None

def slab(rows,flags):
    headers={}
    for key,regex in {'class':r'^CLASS$', 'taxable':r'^TAXABLE$', 'sgst':r'^SGST$', 'cgst':r'^CGST$', 'total_gst':r'^TOTAL GST$'}.items():
        h=[r for r in rows if re.search(regex,r['text'].strip(),re.I)]
        if len(h)==1:headers[key]=h[0]
        else:flags.append('SLAB_MISSING_OR_DUPLICATE_HEADER_'+key.upper())
    if len(headers)!=5:return [],{}
    cols=sorted(headers,key=lambda k:coord(headers[k])[0]);xs=[coord(headers[x])[0] for x in cols]
    cuts=[float('-inf')]+[(xs[i]+xs[i+1])/2 for i in range(len(xs)-1)]+[float('inf')]
    colsp={k:(cuts[i],cuts[i+1]) for i,k in enumerate(cols)}
    rates=[];summary={}
    labels=[r for r in rows if re.fullmatch(r'GST\s*\d+(?:\.\d+)?',r['text'].strip(),re.I)]
    tl=[r for r in rows if r['text'].strip().upper()=='TOTAL' and coord(r)[1]>coord(headers['class'])[1]]
    for label in labels+tl:
        item={'gst_rate':field(re.sub('GST','',label['text'],flags=re.I).strip(),[label])} if label in labels else {}
        this_y=coord(label)[1]
        for key in ['taxable','sgst','cgst','total_gst']:
            left,right=colsp[key]
            near=[r for r in rows if dec(r['text']) is not None and left<=coord(r)[0]<right and abs(coord(r)[1]-this_y)<=23]
            near.sort(key=lambda r:(abs(coord(r)[1]-this_y),abs(coord(r)[0]-xs[cols.index(key)])))
            if near:
                item[key]=field(near[0]['text'],[near[0]])
            else:
                item[key]=field(None,[]);flags.append('MISSING_SLAB_VALUE_'+key.upper())
        if label in labels:rates.append(item)
        else:summary=item
    if len(tl)!=1:flags.append('AMBIGUOUS_SLAB_TOTAL_ROW')
    return rates,summary

def totals(rows,flags):
    patterns={'subtotal':r'^SUB\s*TOTAL$', 'discount':r'^DISC(?:OUNT)?$',
       'sgst':r'^SGST(?:\s+PAYA?BLE)?$',
       'cgst':r'^CGST(?:\s+PAYA?BLE)?$',
       'round_off':r'^ROUND\s*OFF$', 'grand_total':r'^GRAND\s+TOTAL$'}
    result={}
    consumed_values=set()
    # One printed numeric polygon cannot serve two different accounting total labels.
    for key,regex in patterns.items():
        label=one(rows,regex)
        if label is None:
            if key in ('discount','round_off'):
                # Absence is represented as null, not silently written as a source amount.
                result[key]=field(None,[])
            else:
                flags.append('MISSING_TOTAL_LABEL_'+key.upper());result[key]=field(None,[])
            continue
        # Match only numeric evidence to the RIGHT on the SAME printed row.
        candidates=[r for r in rows if r is not label and id(r) not in consumed_values and dec(r['text']) is not None
            and r['bbox_xyxy'][0]>label['bbox_xyxy'][2]+4
            and abs(coord(r)[1]-coord(label)[1])<=max(31,(label['bbox_xyxy'][3]-label['bbox_xyxy'][1])*1.1)]
        candidates.sort(key=lambda r:(abs(coord(r)[1]-coord(label)[1]),r['bbox_xyxy'][0]))
        if not candidates:
            flags.append('MISSING_TOTAL_VALUE_'+key.upper())
            result[key]=field(None,[])
        elif len(candidates)>1 and abs(coord(candidates[0])[1]-coord(label)[1]) == abs(coord(candidates[1])[1]-coord(label)[1]):
            flags.append('AMBIGUOUS_TOTAL_VALUE_'+key.upper());result[key]=field(None,[])
        else:
            result[key]=field(candidates[0]['text'],[label,candidates[0]])
            consumed_values.add(id(candidates[0]))
    return result

def extract(data,rows,sha):
    flags=[]; warn=[]
    zs=data['zones'];sup=gst_party([r for r in rows if r['_zone']=='supplier_header'])
    buy=gst_party([r for r in rows if r['_zone']=='buyer_header'])
    head=[r for r in rows if r['_zone']=='invoice_identifiers']
    def labelled_value(label_regex, value_pattern, is_date=False):
        lab=one(head,label_regex)
        if lab is None:return field(None,[])
        label_tail=re.sub(label_regex,'',lab['text'].strip(),count=1,flags=re.I).lstrip(' :.-')
        same=re.fullmatch(value_pattern,label_tail,re.I) if label_tail else None
        if same:
            val=same.group(1)
            refs=[lab]
        else:
            # Printed forms frequently split INVOICE NO / DATE labels from the values below.
            lb=lab['bbox_xyxy'];cx,cy=coord(lab)
            candidates=[]
            for r in head:
                if r is lab:continue
                mm=re.fullmatch(value_pattern,r['text'].strip(),re.I)
                if mm is None:continue
                bb=r['bbox_xyxy'];dx=abs(coord(r)[0]-cx)
                dy=bb[1]-lb[3]
                if -7<=dy<=max(68,(lb[3]-lb[1])*2.6) and dx<max(90,lb[2]-lb[0]):
                    candidates.append((abs(dy)+dx*.1,r,mm.group(1)))
            if len(candidates)!=1:return field(None,[])
            _,r,val=candidates[0];refs=[lab,r]
        if is_date:
            try:val=dt.datetime.strptime(val.replace('-','/'),'%d/%m/%Y').date().isoformat()
            except ValueError:return field(None,[])
        return field(val,refs)
    invoiceNumber=labelled_value(r'^INVOICE\s*NO\.?',r'(?:INVOICE\s*NO\.?\s*:?\s*)?([A-Z0-9/-]+)')
    # The date label must be the primary DATE, not a later dispatch DATED field.
    dateField=labelled_value(r'^DATE\b',r'(?:DATE\s*:?\s*)?(\d{1,2}[/-]\d{1,2}[/-]\d{4})',True)
    if not invoiceNumber['value']:flags.append('INVOICE_NUMBER_UNRESOLVED')
    if not dateField['value']:flags.append('INVOICE_DATE_UNRESOLVED')
    if sup['gstin_checksum_valid'] is not True:flags.append('SUPPLIER_GSTIN_UNVERIFIED')
    if buy['gstin_checksum_valid'] is not True:flags.append('BUYER_GSTIN_UNVERIFIED')
    if sup['gstin']['value']==buy['gstin']['value']:flags.append('SUPPLIER_BUYER_GSTIN_SAME')
    items,other,headers,cols=classify_goods([r for r in rows if r['_zone']=='goods_table'],flags)
    if not items:flags.append('NO_VERIFIED_ITEM_ROWS')
    rates,totalSlab=slab([r for r in rows if r['_zone']=='gst_slab'],flags)
    sums=totals([r for r in rows if r['_zone']=='invoice_totals'],flags)
    # All comparisons in Decimal, not floats. Never silently "repair" a source amount.
    productAmounts=[dec(r.get('line_amount',{}).get('value')) for r in items]
    subtotal=dec(sums['subtotal']['value'])
    grand=dec(sums['grand_total']['value'])
    cgst=dec(sums['cgst']['value']);sgst=dec(sums['sgst']['value'])
    discount=dec(sums['discount']['value']);round_off=dec(sums['round_off']['value'])
    if discount is None and not sums['discount']['source_boxes']:discount=Decimal('0')
    if round_off is None and not sums['round_off']['source_boxes']:round_off=Decimal('0')
    total=totalSlab
    # If the invoice has NO printed GST slab matrix, make one explicitly PROVISIONAL
    # review row only when exactly one source item rate and the printed tax totals
    # support that rate. Never report it as a printed slab or permit posting.
    if not rates and len(items)==1 and None not in (subtotal, cgst, sgst):
        ocr_rate=items[0].get('gst_rate',{}).get('value')
        normalized=None
        if isinstance(ocr_rate,str):
            candidate=re.fullmatch(r'\s*(\d{1,2})(?:\.)?%\s*',ocr_rate)
            if candidate:normalized=candidate.group(1)
            elif re.fullmatch(r'\d{1,2}(?:\.\d+)?',ocr_rate):normalized=ocr_rate
        parsed=dec(normalized)
        if parsed is not None and Decimal('0')<parsed<=Decimal('28') and \
             (subtotal*parsed/Decimal('200')).quantize(D2,rounding=ROUND_HALF_UP)==cgst==sgst:
            rates=[{
                'gst_rate':field(normalized,[r for r in rows if r['_id']==items[0]['gst_rate']['source_boxes'][0]['id']]),
                'taxable':sums['subtotal'],'sgst':sums['sgst'],'cgst':sums['cgst'],
                'total_gst':field(None,[]),
                'evidence_status':'DERIVED_CANDIDATE_NOT_A_PRINTED_SLAB',
                'original_ocr_rate':ocr_rate}]
            flags.append('DERIVED_GST_SLAB_REQUIRES_EXPLICIT_MANUAL_VERIFICATION')
            if str(ocr_rate).strip()!=str(normalized):flags.append('GST_RATE_OCR_NORMALIZATION_REQUIRES_REVIEW')
            # A full table was not printed; retaining that evidentiary distinction.
            flags=[f for f in flags if not f.startswith('SLAB_MISSING_OR_DUPLICATE_HEADER_')]
            flags.append('NO_PRINTED_GST_SLAB_TABLE')
    tax_base=dec(total.get('taxable',{}).get('value'))
    line_sum=sum(productAmounts,Decimal(0)) if productAmounts and all(x is not None for x in productAmounts) else None
    checks={}
    def check(key,actual,expected,flag):
        delta = (actual-expected) if actual is not None and expected is not None else None
        checks[key]={'actual':money(actual),'expected':money(expected),'difference':money(delta),'pass':delta==0 if delta is not None else None}
        if delta is None:flags.append('MISSING_EVIDENCE_'+key.upper())
        elif delta!=0:flags.append(flag)
    check('sum_item_lines_equals_subtotal',line_sum,subtotal,'ITEM_SUM_VS_SUBTOTAL_DISCREPANCY')
    check('gst_slab_taxable_equals_subtotal',tax_base,subtotal,'SLAB_TAXABLE_VS_SUBTOTAL_DISCREPANCY')
    check('sgst_slab_matches_invoice',dec(total.get('sgst',{}).get('value')),sgst,'SGST_SLAB_INVOICE_DISCREPANCY')
    check('cgst_slab_matches_invoice',dec(total.get('cgst',{}).get('value')),cgst,'CGST_SLAB_INVOICE_DISCREPANCY')
    check('grand_total_equals_subtotal_plus_taxes_minus_discount',grand,
          subtotal+cgst+sgst+round_off-discount if None not in (subtotal,cgst,sgst,round_off,discount) else None,'GRAND_TOTAL_MISMATCH')
    check('slab_total_gst_equals_cgst_sgst',dec(total.get('total_gst',{}).get('value')),
          cgst+sgst if None not in (cgst,sgst) else None,'GST_TOTAL_MISMATCH')
    for i,item in enumerate(items):
        qty=dec(item.get('quantity',{}).get('value'));rate=dec(item.get('rate',{}).get('value'))
        row_total=dec(item.get('line_amount',{}).get('value'))
        if None not in (qty,rate,row_total):
            # Displayed rate is already rounded. Flag but don't infer the undisplayed true rate.
            implied=qty*rate
            if abs(implied-row_total)>=D2:
                warn.append(f'ITEM_{i+1}_DISPLAY_RATE_MULTIPLICATION_DIFFERS_BY_{money(implied-row_total)}: displayed rate may be rounded; do not alter invoice')
    # Tax percentage arithmetic per row is rounded component-wise (2.5% + 2.5%), not 5% of total rounded once.
    for slabline in rates:
        rate=dec(slabline.get('gst_rate',{}).get('value'))
        taxable=dec(slabline.get('taxable',{}).get('value'))
        c=dec(slabline.get('cgst',{}).get('value'));s=dec(slabline.get('sgst',{}).get('value'))
        if rate is None or taxable is None or c is None or s is None:
            flags.append('INCOMPLETE_GST_RATE_SLICE');continue
        expected = (taxable*rate/Decimal(200)).quantize(D2,rounding=ROUND_HALF_UP)
        if c!=expected or s!=expected:flags.append('GST_RATE_COMPONENT_MISMATCH')
    text_ignored=[ref(r) for r in other]
    product_used={x['id'] for row in items for fld in row.values() for x in fld['source_boxes']}
    head_used={x['_id'] for x in headers.values()}
    if len(product_used & head_used): raise AssertionError('Header reused as product')
    if len(items)>0 and len(text_ignored)>0:warn.append('NON_ITEM_TEXT_IN_GOODS_AREA_EXCLUDED')
    result={'schema':SCHEMA, 'status':'REVIEW_REQUIRED', 'import_allowed':False,
            'source':{'ocr_engine':data['ocr_engine'],'orientation_ccw_degrees':data['orientation_ccw_degrees'],
                      'source_image_sha256':data['source_sha256'],'r88_evidence_file_sha256':sha,
                      'total_ocr_lines_preserved':len(rows)},
            'supplier':sup,'buyer':buy,'invoice':{'number':invoiceNumber,'date':dateField},
            'goods':{'headings':{k:ref(r) for k,r in headers.items()},'rows':items,
                     'non_item_text_in_goods_area':text_ignored,'row_count':len(items)},
            'gst':{'slabs':rates,'summary':totalSlab},'totals':sums,'checks':checks,
            'blocking_flags':sorted(set(flags)), 'advisories':sorted(set(warn)),
            'authority':{'review':'Human must compare each field with original photograph',
                'posting':'Not approved for AAH Purchase/Sales or Tally posting',
                'master_lookup':'No ledger/stock identity guessed',
                'gst':'Original component-wise CGST/SGST preserved',
                'rounding':'No automatic amount rounding or balancing'}}
    return result

def report(out):
    lines=[
        '# R89 — Accounting block verification (not import-ready)',
        '',f"OCR source SHA-256: `{out['source']['source_image_sha256']}`",
        f"Recognized polygons: {out['source']['total_ocr_lines_preserved']}",
        f"Import allowed: **No**",'','## Detected fields', '',
        f"Supplier: {out['supplier']['name']['value']} — GSTIN {out['supplier']['gstin']['value']} (checksum {out['supplier']['gstin_checksum_valid']})",
        f"Buyer: {out['buyer']['name']['value']} — GSTIN {out['buyer']['gstin']['value']} (checksum {out['buyer']['gstin_checksum_valid']})",
        f"Invoice number: {out['invoice']['number']['value']}", f"Invoice date: {out['invoice']['date']['value']}", '',
        '## Item rows (only geometry-aligned item/quantity/amount groups)','',
        '| Product text (as recognized) | HSN | Qty | Free | MRP | Rate | GST % | Line amount |',
        '|---|---|---:|---:|---:|---:|---:|---:|',
    ]
    for row in out['goods']['rows']:
        v=lambda k:str(row.get(k,{}).get('value',''))
        lines.append('| '+' | '.join(v(k) for k in ('description','hsn','quantity','free','mrp','rate','gst_rate','line_amount'))+' |')
    lines+=['',f"Other detected text inside goods area (QR/promotional): {len(out['goods']['non_item_text_in_goods_area'])} lines excluded from item rows",'', '## Accounting cross-checks','',
            '| Check | Source | Expected | Difference | Pass |','|---|---:|---:|---:|:---:|']
    for k,c in out['checks'].items():
        lines.append(f"| {k} | {c['actual']} | {c['expected']} | {c['difference']} | {c['pass']} |")
    lines+=['','## Blocking issues']
    for f in out['blocking_flags']:lines.append('- '+f)
    lines+=['','## Advisories']
    for f in out['advisories']:lines.append('- '+f)
    lines+=['','**All extracted fields retain original text box coordinates in the JSON.** This is an independent preview, not a Purchase/Sales import.']
    return '\n'.join(lines)+'\n'

def main():
    a=argparse.ArgumentParser(description=__doc__)
    a.add_argument('evidence',type=Path,help='R88 BLOCK_EVIDENCE.json')
    a.add_argument('--output-prefix',type=Path,default=None)
    args=a.parse_args()
    data,rows,sha=read_input(args.evidence)
    out=extract(data,rows,sha)
    prefix=args.output_prefix or args.evidence.with_name(args.evidence.stem.replace('_BLOCK_EVIDENCE','')+'_R89')
    prefix.parent.mkdir(parents=True,exist_ok=True)
    jf=Path(str(prefix)+'_ACCOUNTING_PREVIEW.json');tf=Path(str(prefix)+'_ACCOUNTING_AUDIT.md')
    jf.write_text(json.dumps(out,indent=2,ensure_ascii=False,default=str),encoding='utf-8')
    tf.write_text(report(out),encoding='utf-8')
    print('PREVIEW',jf,'AUDIT',tf,'ITEMS',out['goods']['row_count'], 'BLOCKING',len(out['blocking_flags']),'IMPORT_ALLOWED',out['import_allowed'])

if __name__=='__main__':
    main()
"""";
    private const string R90Controller = """"
#!/usr/bin/env python3
"""R90: bounded OCR polygons + R89 review; never writes accounting data."""
from __future__ import annotations
import argparse,hashlib,json,subprocess,sys,traceback
from pathlib import Path
import AAH_R88_LAYOUT as layout
import AAH_R89_VERIFY_R88_BLOCKS as verifier

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument('--image',required=True);ap.add_argument('--output',required=True);ap.add_argument('--page',type=int,default=1)
    args=ap.parse_args()
    src=Path(args.image).resolve(strict=True)
    assert src.is_file() and src.stat().st_size>0
    sha=hashlib.sha256(src.read_bytes()).hexdigest().upper()
    target=Path(args.output)
    target.parent.mkdir(parents=True,exist_ok=True)
    options=[]
    for deg in (0,90,180,270):
        temp=target.with_name(target.stem+f'_rotation_{deg}.json')
        try:
            run=subprocess.run([sys.executable,str(Path(__file__).with_name('AAH_R88_WORKER.py')),'--image',str(src),'--degrees',str(deg),'--output',str(temp)],capture_output=True,text=True,timeout=75,check=False)
            print('ROTATION',deg,'EXIT',run.returncode, 'LOG', (run.stdout+run.stderr)[-1500:],flush=True)
            if run.returncode:continue
            obj=json.loads(temp.read_text(encoding='utf-8'))
            if obj.get('engine')!='RapidOCR-ONNX' or obj.get('state')!='completed' or obj.get('image_sha256')!=sha:continue
            if obj.get('raw_ocr_line_count',0)<3 or len(obj.get('lines',[]))!=obj['raw_ocr_line_count']:continue
            options.append((float(obj['orientation_selection_score']),deg,obj))
        except Exception as ex:print('ROTATION_ERROR',deg,str(ex),flush=True)
    if not options:raise RuntimeError('All ONNX orientations failed or returned fewer than 3 bounded text regions')
    options.sort(key=lambda z:(z[0],-z[1]),reverse=True)
    score,deg,raw=options[0]
    raw['input_image']=str(src)
    evidence_path,overlay,evidence=layout.prepare(raw,target.with_suffix(''))
    data,items,evidence_sha=verifier.read_input(evidence_path)
    result=verifier.extract(data,items,evidence_sha)
    if result.get('schema')!='AAH-R89-ACCOUNTING-BLOCK-PREVIEW-V1' or result.get('import_allowed') is not False:
        raise RuntimeError('R89 verification authority contract mismatch')
    h,w=raw['upright_height_width']
    blocks=[]; words=[]
    for r in raw['lines']:
        b=[int(round(x)) for x in r['bbox_xyxy']]
        if not (0<=b[0]<b[2]<=w and 0<=b[1]<b[3]<=h):continue
        words.append({'text':r['text'],'bbox':b,'score':r['confidence'],'polygon_xy':r['polygon_xy']})
        blocks.append({'block_label':'text','block_content':r['text'],'block_bbox':b,'polygon_xy':r['polygon_xy'],'block_order':len(blocks)+1})
    if len(blocks)<3 or len(blocks)!=len(raw['lines']):raise RuntimeError('R90 rejected incomplete geometric OCR block evidence')
    normalized={'engine':'AAH-RAPIDOCR-ONNX-R90','page':args.page,'width':w,'height':h,
                'source_sha256':sha,'rotation_ccw_degrees':deg,
                'document_structure_authority':'RapidOCR polygon evidence + R88 XY regions + R89 review (no Paddle, no import)',
                'word_res_list':words,'parsing_res_list':blocks,'accounting_preview':result,
                'block_evidence_path':str(evidence_path),'block_overlay_path':str(overlay),
                'accounting_block_status':'REVIEW_REQUIRED'}
    target.write_text(json.dumps(normalized,ensure_ascii=False,indent=2),encoding='utf-8')
    print('R90_SPATIAL_REVIEW_READY','orientation',deg,'polygons',len(blocks),'rows',result['goods']['row_count'],'issues',len(result['blocking_flags']),flush=True)
    return 0

if __name__=='__main__':
    try:sys.exit(main())
    except Exception:
        traceback.print_exc();sys.exit(2)
"""";
}
