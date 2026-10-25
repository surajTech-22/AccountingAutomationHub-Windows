// Allows a separately published, read-only Windows bank-OCR acceptance probe
// to call the exact production recognizer and verifier without a second OCR engine.
// The probe is NOT an accounting importer and does not modify AAH data.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AAH.BankOcrProbe")]
