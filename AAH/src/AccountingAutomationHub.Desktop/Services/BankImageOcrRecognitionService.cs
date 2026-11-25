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
    import re
    blocks = data.get('parsing_res_list', [])
    raw_text = '\n'.join(str(b.get('block_content', '')) for b in blocks if isinstance(b, dict))
    plain_text = re.sub(r'<[^>]+>', ' ', raw_text)
    upper_text = re.sub(r'\s+', ' ', plain_text).upper()
    # Do not claim that a summary exists from inferred transaction totals or a
    # header-only row with missing numeric controls. Verify both the six printed
    # control headings and actual printed numbers/decimals in the summary block.
    matches = list(re.finditer(
        r'(?:STATEMENT\s+SUMMARY|\b(?:OPENING|OPE|OP\.?)\s+BAL(?:ANCE)?\b)',
        upper_text))
    if not matches:
        return False
    tail = upper_text[matches[-1].start():]
    has_labels = (
        any(k in tail for k in ('OPENING BAL', 'OPE BAL', 'OP BAL', 'OP. BAL')) and
        ('DR COUNT' in tail or 'DEBIT COUNT' in tail) and
        ('CR COUNT' in tail or 'CREDIT COUNT' in tail) and
        ('DEBITS' in tail or 'TOTAL DEBIT' in tail) and
        ('CREDITS' in tail or 'TOTAL CREDIT' in tail) and
        any(k in tail for k in ('CLOSING BAL', 'CLO BAL', 'CL BAL', 'CL. BAL'))
    )
    if not has_labels:
        return False
    nums = re.findall(r'\d+(?:,\d+)*(?:\.\d{1,2})?', tail)
    decs = re.findall(r'\d+(?:,\d+)*\.\d{1,2}', tail)
    return len(nums) >= 4 and len(decs) >= 2

def _recheck_printed_summary(source, page_num, original, raw_root, pipeline, page_image=None):
    # First-pass page layout frequently omits a printed summary directly below
    # the last transaction table. Re-read an enlarged SOURCE-PDF crop with the
    # installed PaddleOCR-VL model, not a guessed/arithmetically built summary.
    if (source.suffix.lower() != '.pdf' and page_image is None) or _summary_visible(original):
        return original, False
    try:
        close_src = lambda: None
        if page_image is not None:
            from PIL import Image
            if isinstance(page_image, (str, Path)):
                rect_im = Image.open(str(page_image))
                page = True
                def save_region(top, bottom, path):
                    rw, rh = rect_im.size
                    y0 = max(0, min(rh - 1, int(round(rh * top))))
                    y1 = max(y0 + 1, min(rh, int(round(rh * bottom))))
                    rect_im.crop((0, y0, rw, y1)).save(str(path))
                def close_src():
                    rect_im.close()
            elif hasattr(page_image, 'shape'):
                page = True
                def save_region(top, bottom, path):
                    rh, rw = page_image.shape[:2]
                    y0 = max(0, min(rh - 1, int(round(rh * top))))
                    y1 = max(y0 + 1, min(rh, int(round(rh * bottom))))
                    crop = page_image[y0:y1, :]
                    if crop.ndim == 3 and crop.shape[2] >= 3:
                        crop_rgb = crop[:, :, :3][:, :, ::-1]
                    else:
                        crop_rgb = crop
                    Image.fromarray(crop_rgb).save(str(path))
            else:
                page = None
        if page_image is None or page is None:
            try:
                import fitz
            except ImportError:
                try:
                    import pymupdf as fitz
                except ImportError:
                    fitz = None
            if fitz is not None:
                pdf = fitz.open(str(source))
                page_count = pdf.page_count
                page = pdf[page_num - 1] if 0 < page_num <= page_count else None
                def save_region(top, bottom, path):
                    shape = page.rect
                    clip = fitz.Rect(shape.x0, shape.y0 + shape.height * top,
                                     shape.x1, shape.y0 + shape.height * bottom)
                    page.get_pixmap(matrix=fitz.Matrix(2.4, 2.4), clip=clip,
                                    alpha=False).save(str(path))
                close_src = pdf.close
            else:
                # PaddleOCR deployments frequently bundle PDFium instead of
                # PyMuPDF. Use that existing local renderer, never a cloud API.
                import pypdfium2 as pdfium
                pdf = pdfium.PdfDocument(str(source))
                page_count = len(pdf)
                page = pdf[page_num - 1] if 0 < page_num <= page_count else None
                def save_region(top, bottom, path):
                    width, height = page.get_size()
                    bitmap = page.render(scale=2.4, crop=(
                        0, height * (1.0 - bottom), 0, height * top))
                    bitmap.to_pil().save(str(path))
                close_src = pdf.close
        try:
            if page is None:
                return original, False
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
                tb = max(bottoms)
                tight_top = max(0.0, min(0.88, tb - 0.015))
                tight_bot = min(0.98, max(tight_top + 0.12, tb + 0.12))
            else:
                regions = [(.42, .85), (.65, .98)]
                tight_top, tight_bot = 0.55, 0.95
            for attempt, (top, bottom) in enumerate(regions[:2], 1):
                crop_file = raw_root / f'summary_source_{page_num:03d}_{attempt:02d}.png'
                save_region(top, bottom, crop_file)
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
            # Fallback when PP-DocLayoutV3 drops unboxed borderless footer summaries:
            # run direct VLM on a tight below-table crop with layout detection off.
            tight_crop = raw_root / f'summary_source_{page_num:03d}_03.png'
            save_region(tight_top, tight_bot, tight_crop)
            for prompt in ('table', 'ocr'):
                try:
                    outputs = pipeline.predict_iter(
                        input=str(tight_crop),
                        use_doc_orientation_classify=False,
                        use_doc_unwarping=False,
                        use_layout_detection=False,
                        prompt_label=prompt)
                except TypeError:
                    outputs = pipeline.predict_iter(
                        input=str(tight_crop),
                        use_doc_orientation_classify=False,
                        use_doc_unwarping=False,
                        use_layout_detection=False)
                for output in outputs:
                    parsed = result_json(output)
                    if parsed and _summary_visible(parsed):
                        candidate = parsed.get('parsing_res_list', [])
                        original.setdefault('parsing_res_list', []).extend(candidate)
                        return original, True
        finally:
            close_src()
    except Exception as exc:
        status('SUMMARY_RECOVERY_SKIPPED',
               f'Source summary re-recognition unavailable: {type(exc).__name__}: {exc}')
    return original, False

def _analyze_table_alignment(html):
    import html as html_mod
    import re
    if not html or '<tr' not in html.lower():
        return None
    row_htmls = re.findall(r'<tr\b[^>]*>(.*?)</tr>', html, flags=re.S | re.I)
    rows = []
    for rhtml in row_htmls:
        cells = []
        for attrs, body in re.findall(r'<(?:td|th)\b([^>]*)>(.*?)</(?:td|th)>', rhtml, flags=re.S | re.I):
            txt = re.sub(r'<br\s*/?>', ' ', body, flags=re.I)
            txt = re.sub(r'<[^>]+>', ' ', txt)
            txt = html_mod.unescape(txt)
            txt = re.sub(r'\s+', ' ', txt).strip()
            cs_m = re.search(r'\bcolspan\s*=\s*["\']?(\d+)', attrs, flags=re.I)
            colspan = max(1, int(cs_m.group(1))) if cs_m else 1
            cells.append(txt)
            for _ in range(colspan - 1):
                cells.append('')
        if cells:
            rows.append(cells)
    if len(rows) < 3:
        return None

    date_hdr_re = re.compile(r'\b(?:POST\.?\s*DT|POSTING\s+DATE|TXN\.?\s*DT|TRAN\.?\s*DT|TRANS(?:ACTION)?\s+DATE|DATE)\b')
    val_hdr_re = re.compile(r'\b(?:VAL\.?\s*DT|VALUE\s+DATE)\b')
    bal_hdr_re = re.compile(r'\b(?:CLOSING\s+BAL(?:ANCE)?|BALANCE|BAL\.?)\b')
    amt_hdr_re = re.compile(r'\b(?:DEBIT|DR|WITHDRAWAL|CREDIT|CR|DEPOSIT|AMOUNT)\b')

    header_idx = None
    date_col = None
    bal_col = None
    dr_col = None
    cr_col = None
    amt_cols = []
    for idx in range(min(5, len(rows))):
        up = [c.upper() for c in rows[idx]]
        d_col = next((i for i, h in enumerate(up) if date_hdr_re.search(h) and not val_hdr_re.search(h)), None)
        b_col = next((i for i, h in enumerate(up) if bal_hdr_re.search(h)), None)
        a_cols = [i for i, h in enumerate(up) if amt_hdr_re.search(h) and not bal_hdr_re.search(h)]
        if d_col is not None and b_col is not None and a_cols:
            header_idx = idx
            date_col = d_col
            bal_col = b_col
            amt_cols = a_cols
            dr_col = next((i for i in a_cols if any(k in up[i] for k in ('DEBIT', 'WITHDRAWAL', 'DR'))), None)
            cr_col = next((i for i in a_cols if any(k in up[i] for k in ('CREDIT', 'DEPOSIT', 'CR'))), None)
            break
    if header_idx is None:
        return None

    date_re = re.compile(r'\b(?:\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4}|\d{1,2}[-/ ][A-Za-z]{3}[-/ ]\d{2,4})\b')
    money_re = re.compile(r'(-?(?:\d{1,3}(?:,\d{2,3})+|\d+)\.\d{2})\s*(CR|DR)?\b', flags=re.I)

    def parse_money(cell):
        if not cell:
            return None
        m = money_re.search(cell)
        if not m:
            return None
        val = float(m.group(1).replace(',', ''))
        suf = (m.group(2) or '').upper()
        if suf == 'DR':
            val = -abs(val)
        elif suf == 'CR':
            val = abs(val)
        return val

    financial_rows = 0
    total_money_rows = 0
    dated_without_amount = 0
    amount_without_date = 0
    bf_merged_with_date = 0
    multi_date_in_cell = 0
    balance_breaks = 0
    prev_bal = None

    for row in rows[header_idx + 1:]:
        joined_up = ' '.join(row).upper()
        if 'STATEMENT SUMMARY' in joined_up or (
            re.search(r'\b(?:OPENING|OPE|OP\.?)\s+BAL', joined_up) and
            re.search(r'\b(?:CLOSING|CLO|CL\.?)\s+BAL', joined_up)
        ):
            break

        d_cell = row[date_col] if date_col < len(row) else ''
        dates = date_re.findall(d_cell)
        has_date = len(dates) > 0
        if len(dates) >= 2:
            multi_date_in_cell += 1

        dr_val = abs(parse_money(row[dr_col])) if (dr_col is not None and dr_col < len(row) and parse_money(row[dr_col]) is not None) else 0.0
        cr_val = abs(parse_money(row[cr_col])) if (cr_col is not None and cr_col < len(row) and parse_money(row[cr_col]) is not None) else 0.0
        has_move = any((c < len(row) and parse_money(row[c]) is not None) for c in amt_cols)
        bal_val = parse_money(row[bal_col]) if bal_col < len(row) else None
        has_bal = bal_val is not None
        has_money = has_move or has_bal
        if has_money:
            total_money_rows += 1

        is_bf_or_total = any(k in joined_up for k in (
            'BROUGHT', 'FORWARD', 'B/F', 'OPENING BAL', 'CLOSING BAL', 'CARRIED', 'C/F', 'TOTAL'
        ))
        if is_bf_or_total:
            if has_date and has_money:
                bf_merged_with_date += 1
            elif has_bal and prev_bal is None:
                prev_bal = bal_val
            continue

        if has_date and not has_money:
            dated_without_amount += 1
            continue

        if has_money:
            financial_rows += 1
            if not has_date:
                amount_without_date += 1
            if prev_bal is not None and bal_val is not None and (dr_col is not None or cr_col is not None):
                expected = round(prev_bal - dr_val + cr_val, 2)
                if abs(expected - bal_val) > 0.02:
                    balance_breaks += 1
            if bal_val is not None:
                prev_bal = bal_val

    defect_score = (
        dated_without_amount * 2 +
        amount_without_date * 2 +
        bf_merged_with_date * 3 +
        multi_date_in_cell * 3 +
        balance_breaks * 2
    )
    needs_recheck = (
        (dated_without_amount + amount_without_date + bf_merged_with_date + multi_date_in_cell) >= 2
        or (bf_merged_with_date >= 1 and balance_breaks >= 1)
        or (balance_breaks >= 2 and (dated_without_amount + amount_without_date + bf_merged_with_date) >= 1)
    )
    return {
        'financial_rows': financial_rows,
        'total_money_rows': total_money_rows,
        'dated_without_amount': dated_without_amount,
        'amount_without_date': amount_without_date,
        'bf_merged_with_date': bf_merged_with_date,
        'multi_date_in_cell': multi_date_in_cell,
        'balance_breaks': balance_breaks,
        'defect_score': defect_score,
        'needs_recheck': needs_recheck,
    }

def _build_row_guided_table_crop(preproc_img, bbox, out_path):
    try:
        import math
        import cv2
        import numpy as np
        if preproc_img is None or not hasattr(preproc_img, 'shape') or len(bbox) != 4:
            return None
        H, W = preproc_img.shape[:2]
        pad = 12
        x0 = max(0, int(round(bbox[0])) - pad)
        y0 = max(0, int(round(bbox[1])) - pad)
        x1 = min(W, int(round(bbox[2])) + pad)
        y1 = min(H, int(round(bbox[3])) + pad)
        if x1 - x0 < 200 or y1 - y0 < 120:
            return None
        crop = preproc_img[y0:y1, x0:x1].copy()

        gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY) if crop.ndim == 3 else crop.copy()
        ch, cw = gray.shape
        bw = cv2.adaptiveThreshold(gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY_INV, 31, 15)
        contours, _ = cv2.findContours(bw, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        big = sorted(
            [c for c in contours if cv2.boundingRect(c)[2] > cw * 0.70 and cv2.boundingRect(c)[3] > ch * 0.60],
            key=cv2.contourArea, reverse=True)
        if big:
            c = big[0]
            _, _, bww, bwh = cv2.boundingRect(c)
            if cv2.contourArea(c) / float(max(1, bww * bwh)) >= 0.78:
                pts = c.reshape(-1, 2).astype(np.float32)
                s = pts.sum(axis=1)
                d = pts[:, 0] - pts[:, 1]
                tl, br, tr, bl = pts[np.argmin(s)], pts[np.argmax(s)], pts[np.argmax(d)], pts[np.argmin(d)]
                top_ang = abs(math.degrees(math.atan2(float(tr[1] - tl[1]), float(tr[0] - tl[0]))))
                bot_ang = abs(math.degrees(math.atan2(float(br[1] - bl[1]), float(br[0] - bl[0]))))
                drift = max(abs(float(tr[1] - tl[1])), abs(float(br[1] - bl[1])), abs(float(bl[0] - tl[0])), abs(float(br[0] - tr[0])))
                if drift >= 6.0 and top_ang <= 10.0 and bot_ang <= 10.0:
                    tw_f = max(float(np.linalg.norm(tr - tl)), float(np.linalg.norm(br - bl)))
                    th_f = max(float(np.linalg.norm(bl - tl)), float(np.linalg.norm(br - tr)))
                    m = 6.0
                    src_q = np.array([tl, tr, br, bl], dtype=np.float32)
                    dst_q = np.array([[m, m], [m + tw_f, m], [m + tw_f, m + th_f], [m, m + th_f]], dtype=np.float32)
                    Hmat = cv2.getPerspectiveTransform(src_q, dst_q)
                    crop = cv2.warpPerspective(
                        crop, Hmat, (int(round(tw_f + 2 * m)), int(round(th_f + 2 * m))),
                        flags=cv2.INTER_CUBIC, borderValue=(255, 255, 255))

        th, tw = crop.shape[:2]
        gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY) if crop.ndim == 3 else crop.copy()
        tbw = cv2.adaptiveThreshold(gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY_INV, 31, 15)

        vlines = cv2.HoughLinesP(tbw, 1, np.pi / 1800, threshold=max(40, int(th * 0.22)), minLineLength=max(40, int(th * 0.35)), maxLineGap=25)
        v_mask = np.zeros_like(tbw)
        v_xs = []
        if vlines is not None:
            for lx1, ly1, lx2, ly2 in vlines[:, 0]:
                if abs(lx2 - lx1) < 28 and abs(ly2 - ly1) > th * 0.35:
                    cv2.line(v_mask, (lx1, ly1), (lx2, ly2), 255, 13)
                    v_xs.append((lx1 + lx2) / 2.0)
        v_xs.sort()
        v_clusters = []
        for x in v_xs:
            if not v_clusters or x - v_clusters[-1][-1] > 20:
                v_clusters.append([x])
            else:
                v_clusters[-1].append(x)
        v_cols = [int(np.mean(g)) for g in v_clusters]

        h_proj = (cv2.dilate(tbw, np.ones((3, 1), np.uint8)) > 0).sum(axis=1)
        h_ys = [y for y in range(th) if h_proj[y] > tw * 0.55]
        h_clusters = []
        for y in h_ys:
            if not h_clusters or y - h_clusters[-1][-1] > 8:
                h_clusters.append([y])
            else:
                h_clusters[-1].append(y)
        h_rules = [int(np.mean(g)) for g in h_clusters]
        h_mask = np.zeros_like(tbw)
        for hy in h_rules:
            cv2.line(h_mask, (0, hy), (tw - 1, hy), 255, 9)

        header_rules = [y for y in h_rules if 15 < y < int(th * 0.18)]
        body_y0 = (header_rules[-1] + 5) if header_rules else int(th * 0.065)
        footer_rules = [y for y in h_rules if y > int(th * 0.85)]
        body_y1 = (footer_rules[0] - 4) if footer_rules else th - 5
        if body_y1 <= body_y0 + 30:
            return None

        internal_h = [y for y in h_rules if body_y0 + 15 < y < body_y1 - 15]
        if len(internal_h) >= 5:
            return None

        clean = cv2.subtract(tbw, cv2.bitwise_or(v_mask, h_mask))
        outer_left = max([0] + [x for x in v_cols if x <= 25])
        outer_right = min([tw - 1] + [x for x in v_cols if x >= tw - 25])
        inner_v = [x for x in v_cols if 25 < x < tw - 25]
        left_x0 = outer_left + 6
        left_x1 = (inner_v[0] - 6) if inner_v and inner_v[0] >= 45 else int(tw * 0.10)
        right_x0 = (inner_v[-1] + 6) if inner_v and (outer_right - inner_v[-1]) >= 55 else int(tw * 0.85)
        right_x1 = outer_right - 6
        if left_x1 <= left_x0 + 10 or right_x1 <= right_x0 + 10:
            return None

        def find_bands(xa, xb):
            sub = clean[body_y0:body_y1, xa:xb]
            proj = (sub > 0).sum(axis=1)
            min_h = max(6, int(th * 0.006))
            bands = []
            in_b = False
            start_i = 0
            for i, val in enumerate(proj):
                if val >= 4 and not in_b:
                    start_i = i
                    in_b = True
                elif val < 4 and in_b:
                    if i - start_i >= min_h:
                        bands.append((body_y0 + start_i, body_y0 + i - 1))
                    in_b = False
            if in_b and len(proj) - start_i >= min_h:
                bands.append((body_y0 + start_i, body_y0 + len(proj) - 1))
            return bands

        lb = find_bands(left_x0, left_x1)
        rb = find_bands(right_x0, right_x1)
        if len(rb) < 3 or len(lb) < 2:
            return None

        line_h = float(np.median([b[1] - b[0] + 1 for b in (lb + rb)])) if (lb + rb) else 15.0
        tol = max(14.0, line_h * 1.1)
        rows = []
        used_l = set()
        for ry0, ry1 in rb:
            rc = (ry0 + ry1) / 2.0
            best_li = None
            best_d = tol
            for li, (ly0, ly1) in enumerate(lb):
                if li in used_l:
                    continue
                d = abs((ly0 + ly1) / 2.0 - rc)
                if d < best_d:
                    best_d = d
                    best_li = li
            if best_li is not None:
                used_l.add(best_li)
                rows.append((lb[best_li], (ry0, ry1)))
            else:
                rows.append((None, (ry0, ry1)))
        for li, band in enumerate(lb):
            if li not in used_l:
                rows.append((band, None))
        rows.sort(key=lambda item: (item[1][0] if item[1] is not None else item[0][0]))

        clean_pad = cv2.dilate(clean, np.ones((3, 1), np.uint8))
        mid_x = (outer_left + outer_right) // 2

        def poly_ink(yl, ym, yr):
            n_pts = max(12, tw // 8)
            xs1 = np.linspace(outer_left + 4, mid_x, n_pts).astype(int)
            ys1 = np.clip(np.round(np.linspace(yl, ym, len(xs1))).astype(int), 0, th - 1)
            xs2 = np.linspace(mid_x, outer_right - 4, n_pts).astype(int)
            ys2 = np.clip(np.round(np.linspace(ym, yr, len(xs2))).astype(int), 0, th - 1)
            hits_exact = float((clean[ys1, xs1] > 0).mean() + (clean[ys2, xs2] > 0).mean()) * 0.5
            hits_pad = float((clean_pad[ys1, xs1] > 0).mean() + (clean_pad[ys2, xs2] > 0).mean()) * 0.5
            return hits_exact, hits_pad

        drawn = 0
        for i in range(1, len(rows)):
            pl, pr = rows[i - 1]
            cl, cr = rows[i]
            yr = (cr[0] - 4) if cr is not None else (cl[0] - 4)
            if cr is not None and pr is not None and (cr[0] - pr[1]) <= line_h:
                yr = (pr[1] + cr[0]) // 2
            yl = (cl[0] - 4) if cl is not None else yr
            if cl is not None and pl is not None and (cl[0] - pl[1]) <= line_h:
                yl = (pl[1] + cl[0]) // 2
            ym0 = (yl + yr) // 2
            best = (9.0, 9.0, 99, yl, ym0, yr)
            for dl in (-2, -1, 0, 1, 2):
                for dr in (-2, -1, 0, 1, 2):
                    yl2 = max(body_y0, min(body_y1, yl + dl))
                    yr2 = max(body_y0, min(body_y1, yr + dr))
                    for dm in (-4, -2, -1, 0, 1, 2, 4):
                        ym2 = max(body_y0, min(body_y1, (yl2 + yr2) // 2 + dm))
                        he, hp = poly_ink(yl2, ym2, yr2)
                        cand = (he, hp, abs(dl) + abs(dr) + abs(dm), yl2, ym2, yr2)
                        if cand < best:
                            best = cand
            if best[0] < 0.025:
                drawn += 1
                cv2.line(crop, (outer_left + 2, best[3]), (mid_x, best[4]), (60, 60, 60), 1)
                cv2.line(crop, (mid_x, best[4]), (outer_right - 2, best[5]), (60, 60, 60), 1)

        if drawn < 3:
            return None
        from PIL import Image
        crop_rgb = crop[:, :, :3][:, :, ::-1] if crop.ndim == 3 and crop.shape[2] >= 3 else crop
        Image.fromarray(crop_rgb).save(str(out_path))
        return out_path
    except Exception:
        return None

def _recheck_misaligned_borderless_tables(data, preproc_img, raw_root, source_index, page_index, pipeline):
    if not isinstance(data, dict) or preproc_img is None:
        return
    blocks = data.get('parsing_res_list', [])
    if not isinstance(blocks, list):
        return
    for block_idx, block in enumerate(blocks):
        if not isinstance(block, dict) or block.get('block_label') != 'table':
            continue
        bbox = block.get('block_bbox')
        if not isinstance(bbox, (list, tuple)) or len(bbox) != 4:
            continue
        orig_html = str(block.get('block_content', ''))
        orig_stats = _analyze_table_alignment(orig_html)
        if not orig_stats or not orig_stats.get('needs_recheck'):
            continue
        guided_path = raw_root / f'table_guided_s{source_index:03d}_p{page_index:03d}_b{block_idx:02d}.png'
        built = _build_row_guided_table_crop(preproc_img, bbox, guided_path)
        if built is None:
            continue
        try:
            outputs = pipeline.predict_iter(
                input=str(built),
                use_doc_orientation_classify=False,
                use_doc_unwarping=False,
                use_layout_detection=False,
                prompt_label='table')
        except TypeError:
            outputs = pipeline.predict_iter(
                input=str(built),
                use_doc_orientation_classify=False,
                use_doc_unwarping=False,
                use_layout_detection=False)
        cand_html = None
        for out in outputs:
            parsed = result_json(out)
            if not parsed:
                continue
            cand_blocks = parsed.get('parsing_res_list', [])
            cand_texts = [
                str(b.get('block_content', ''))
                for b in cand_blocks
                if isinstance(b, dict) and '<tr' in str(b.get('block_content', '')).lower()
            ]
            if cand_texts:
                cand_html = cand_texts[0]
                break
        if not cand_html:
            status('TABLE_ROW_GUIDED_KEPT_ORIGINAL',
                   f'Kept original table on page {page_index} (no candidate HTML extracted).')
            continue
        cand_stats = _analyze_table_alignment(cand_html)
        min_rows = max(3, int(orig_stats['total_money_rows'] * 0.85))
        if (cand_stats is not None and
                cand_stats['total_money_rows'] >= min_rows and
                cand_stats['defect_score'] < orig_stats['defect_score'] and
                cand_stats['balance_breaks'] <= orig_stats['balance_breaks']):
            block['block_content'] = cand_html
            status('TABLE_ROW_GUIDED_RECOVERY',
                   f'Replaced drifted borderless table on page {page_index} '
                   f'(defect_score {orig_stats["defect_score"]} -> {cand_stats["defect_score"]}).')
        else:
            status('TABLE_ROW_GUIDED_KEPT_ORIGINAL',
                   f'Kept original table on page {page_index} '
                   f'(orig defect_score={orig_stats["defect_score"]}, '
                   f'cand={(cand_stats or {}).get("defect_score")}).')

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
            page_count = 0
            last_preproc_image = None
            generator = pipeline.predict_iter(
                input=str(source),
                use_doc_orientation_classify=True,
                use_doc_unwarping=True,
                use_layout_detection=True,
            )

            for page_index, res in enumerate(generator, 1):
                page_count += 1
                page_dir = raw_root / f'{source_index:03d}'
                page_dir.mkdir(parents=True, exist_ok=True)
                json_path = page_dir / f'page_{page_index:03d}.json'

                preproc_img = None
                try:
                    dp = res['doc_preprocessor_res'] if hasattr(res, '__getitem__') else None
                    if isinstance(dp, dict):
                        out_arr = dp.get('output_img')
                        if out_arr is not None and hasattr(out_arr, 'shape'):
                            preproc_img = out_arr.copy()
                except Exception:
                    preproc_img = None
                last_preproc_image = preproc_img

                data = result_json(res)
                if data is not None:
                    _recheck_misaligned_borderless_tables(
                        data, preproc_img, raw_root, source_index, page_index, pipeline)
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
                    del preproc_img
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
            if page_count and (source.suffix.lower() == '.pdf' or last_preproc_image is not None):
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
                        source, page_count, original_page, raw_root, pipeline,
                        page_image=last_preproc_image)
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
            last_preproc_image = None
            gc.collect()
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
            var scriptPath = Path.Combine(runRoot, "recognition_bridge.py");
            await File.WriteAllTextAsync(scriptPath, RecognitionPython, new UTF8Encoding(false), cancellationToken);
            var pythonInfo = new ProcessStartInfo
            {
                FileName = runtime.PythonExecutable,
                WorkingDirectory = runtime.RootFolder,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            pythonInfo.ArgumentList.Add(scriptPath);
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
