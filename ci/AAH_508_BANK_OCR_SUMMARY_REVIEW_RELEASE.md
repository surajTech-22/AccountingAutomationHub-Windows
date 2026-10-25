# AAH v4.0.0.508 — Bank OCR review and printed-summary compatibility

## Confirmed source-level defects from the user's Action Center screenshot

1. The review message `Grouped pages: 1` counted distinct **source files**, not physical scanned-PDF pages. It was misleading for one two-page PDF.
2. Review messages used literal `\\n` separators, making the action details unreadable and preventing the Action Center from finding `Source:` in old persisted actions.
3. The Bank verifier's printed statement-summary extractor accepted only `table`-labeled OCR blocks. PaddleOCR can preserve a real printed six-control summary as Markdown in a `text` block, or as explicitly labeled printed text.

## Changes in v4.0.0.508

- Report real recognized document PDF page count, separate source-file count, transaction row count, issue count and verification audit path.
- Decode known legacy review-message delimiters without indiscriminately rewriting Windows path backslashes, preserving existing Action Center Open Source action.
- Read source-visible printed summaries when OCR provides explicit six column labels and six monetary/count values; no hardcoded bank figures, correction guesses, or arithmetic-created evidence.
- If an OCR block identifies a printed statement summary but the values remain illegible, force `REVIEW_REQUIRED` even if a brought-forward value exists.
- Retain v4.0.0.507 scanned-vs-digital Bank PDF classification and Tally/accounting import gates.

## Verification and safety

- 16 synthetic executable Bank OCR verifier scenarios (previously 12), including text-Markdown summary, labeled plain-text summary, one-paisa mismatch, damaged financial OCR; financial mismatch and damaged values must be blocked.
- Windows compile, signed-release protocol and full-components regression must pass on **the exact source commit**. No CI step touches the user's installation.
- Real PDF OCR output from the user's machine is **not available through GitHub**. The screenshot says `REVIEW_REQUIRED`, and the source PDF in the screenshot lacks the `(2)` suffix of the Library test copy. Therefore do NOT claim end-to-end OCR accuracy or import success for that file.
- The private local `AAH_DOCUMENT_VERIFICATION.json` and `recognition.log` need a source comparison before correctness certification. Do not upload these to public GitHub issue #2 or source control.
- Automatic release is allowed only after all exact SHA build gates, through existing v4.0.0.508 checksum-verified GitHub release channel. Installation readback is required to confirm successful update.

This release improves review correctness and conservatively recovers explicit printed summary evidence. It does **not** train an OCR model or certify all printed text.
