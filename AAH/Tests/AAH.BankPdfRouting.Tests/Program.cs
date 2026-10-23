using System.Text;
using AccountingAutomationHub.Desktop.Services;

// Independently exercise the production classifier using actual generated PDF
// objects, not file extensions or mocked pages. Contains NO customer data.
var root = Path.Combine(Path.GetTempPath(), "AAH-Pdf-Route-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var scenarios = new (string Name, string[] Pages, BankPdfDocumentKind Expected)[]
{
    ("native text PDF", new[] { "text" }, BankPdfDocumentKind.Digital),
    ("raster-only scanned PDF", new[] { "image" }, BankPdfDocumentKind.Scanned),
    ("raster scan with hidden OCR text layer", new[] { "overlay" }, BankPdfDocumentKind.Scanned),
    ("mixed text and scanned pages", new[] { "text", "image" }, BankPdfDocumentKind.Mixed),
    ("all digital multi-page", new[] { "text", "text" }, BankPdfDocumentKind.Digital),
    ("all scanned multi-page", new[] { "image", "image" }, BankPdfDocumentKind.Scanned)
};
try
{
    int passed = 0;
    foreach (var scene in scenarios)
    {
        var path = Path.Combine(root, passed + ".pdf");
        MakePdf(path, scene.Pages);
        var actual = BankPdfDocumentClassifier.Classify(path);
        if (actual != scene.Expected)
            throw new Exception(scene.Name + ": expected " + scene.Expected + ", got " + actual);
        Console.WriteLine("PASS: " + scene.Name);
        passed++;
    }
    var invalid = Path.Combine(root, "invalid.pdf");
    File.WriteAllText(invalid, "This is not a PDF");
    if (BankPdfDocumentClassifier.Classify(invalid) != BankPdfDocumentKind.Unknown)
        throw new Exception("Unreadable PDF was not sent to safe fallback.");
    Console.WriteLine("PASS: unreadable PDF routes to safe fallback.");
    Console.WriteLine($"PASS: {passed + 1}/{scenarios.Length + 1} Bank PDF routing scenarios.");
}
finally { try { Directory.Delete(root, true); } catch { } }

static void MakePdf(string path, IReadOnlyList<string> types)
{
    // Build a minimal standards-conformant PDF with a placement for a single
    // raster image that fills the page. The image is 1px in the fixture;
    // what matters for detection is its placement rectangle.
    const string contentText = "THIS IS A SEARCHABLE BANK STATEMENT CONTAINING ENOUGH ALPHANUMERIC WORDS FOR THE TEXT ROUTING THRESHOLD TO BE MET";
    var pages = new List<int>();
    var objects = new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "", // /Pages patched below
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length 3 >>\nstream\n00>\nendstream"
    };
    foreach (var type in types)
    {
        bool isImage = type == "image" || type == "overlay";
        bool hasText = type == "text" || type == "overlay";
        int pageNumber = objects.Count + 1;
        int contentsNumber = pageNumber + 1;
        pages.Add(pageNumber);
        var res = "<< /Font << /F1 3 0 R >> /XObject << /Im1 4 0 R >> >>";
        objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources {res} /Contents {contentsNumber} 0 R >>");
        var content = (isImage ? "q 595 0 0 842 0 0 cm /Im1 Do Q\n" : "")
                    + (hasText ? $"BT /F1 12 Tf 30 750 Td ({contentText}) Tj ET\n" : "");
        int length = Encoding.ASCII.GetByteCount(content);
        objects.Add($"<< /Length {length} >>\nstream\n{content}endstream");
    }
    objects[1] = $"<< /Type /Pages /Count {pages.Count} /Kids [ {string.Join(" ", pages.Select(p => p + " 0 R"))} ] >>";
    var pdf = new StringBuilder("%PDF-1.4\n");
    var offsets = new List<int> { 0 };
    for (int i = 0; i < objects.Count; i++)
    {
        offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
        pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
    }
    int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
    pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
    for (int i = 1; i <= objects.Count; i++)
        pdf.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
    pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1)
       .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
    File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
}
