using System.IO.Compression;
using Aah.UpdateHost;

namespace Aah.UpdateHost.Tests;

internal static class EntryPoint
{
    private static void Assert(bool ok, string explanation)
    {
        if (!ok) throw new InvalidOperationException(explanation);
    }

    private static void Test(string root)
    {
        Assert(Program.PublisherIdentityCompatible(null, null),
            "Two unsigned checksummed GitHub releases were rejected.");
        Assert(Program.PublisherIdentityCompatible("AAA", "aaa"),
            "Identical trusted signed publishers should match.");
        Assert(!Program.PublisherIdentityCompatible("AAA", "BBB") &&
               !Program.PublisherIdentityCompatible(null, "AAA") &&
               !Program.PublisherIdentityCompatible("AAA", null),
            "Mismatch or partial signing was permitted.");
        Console.WriteLine("PASS: signed publishers must match; DeskSpace-style unsigned channel is consistent.");

        var install = Path.Combine(root, "installed");
        var publish = Path.Combine(root, "publish");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(publish);
        var target = Path.Combine(install, "AccountingAutomationHub.exe");
        File.WriteAllText(target, "ORIGINAL EXE");
        File.WriteAllText(Path.Combine(install, "plugin.dll"), "ORIGINAL DLL");
        var records = Path.Combine(install, "private-accounts.sqlite");
        File.WriteAllText(records, "KEEP BOOKS");
        File.WriteAllText(Path.Combine(publish, "AccountingAutomationHub.exe"), "NEW EXE");
        File.WriteAllText(Path.Combine(publish, "plugin.dll"), "NEW DLL");
        File.WriteAllText(Path.Combine(publish, "new-support.dll"), "NEW FILE");
        var tx = Program.StageTransaction(publish, install);
        Program.ApplyTransaction(tx, install);
        Assert(File.ReadAllText(target) == "NEW EXE", "Application was not upgraded.");
        Assert(File.ReadAllText(Path.Combine(install, "new-support.dll")) == "NEW FILE", "New files missing.");
        Assert(File.ReadAllText(records) == "KEEP BOOKS", "Financial data changed.");
        Program.RollbackTransaction(tx, install);
        Assert(File.ReadAllText(target) == "ORIGINAL EXE", "Rollback did not restore prior EXE.");
        Assert(File.ReadAllText(Path.Combine(install, "plugin.dll")) == "ORIGINAL DLL", "Rollback did not restore prior DLL.");
        Assert(!File.Exists(Path.Combine(install, "new-support.dll")), "Rollback left newly created DLL.");
        Assert(File.ReadAllText(records) == "KEEP BOOKS", "Rollback removed books.");
        Console.WriteLine("PASS: application replacement and transactional rollback preserve accounting data.");

        tx = Program.StageTransaction(publish, install);
        Program.ApplyTransaction(tx, install);
        Program.RecoverPending(install);
        Assert(File.ReadAllText(target) == "ORIGINAL EXE", "Interrupted transaction was not recovered.");
        Assert(File.ReadAllText(records) == "KEEP BOOKS", "Interrupted update changed financial data.");
        Console.WriteLine("PASS: interrupted update journal recovers original binaries.");

        var preJournal = Path.Combine(install, ".aah-github-swap-orphaned");
        Directory.CreateDirectory(preJournal);
        File.WriteAllText(Path.Combine(preJournal,"staging.tmp"), "DISPOSABLE");
        Program.RecoverPending(install);
        Assert(!Directory.Exists(preJournal), "Pre-journal staging directory not reclaimed.");
        Console.WriteLine("PASS: orphaned staging is cleaned without altering installed files.");

        var badFile = Path.Combine(publish,"accounts.db");
        File.WriteAllText(badFile,"PRIVATE");
        var wasBlocked = false;
        try { Program.StageTransaction(publish, install); }
        catch (InvalidDataException) { wasBlocked = true; }
        Assert(wasBlocked, "Accounting data was allowed into release payload.");
        File.Delete(badFile);
        Console.WriteLine("PASS: accounting database excluded from application update.");

        var zip = Path.Combine(root,"bad.zip");
        using (var f = File.Open(zip,FileMode.Create))
        using (var archive = new ZipArchive(f,ZipArchiveMode.Create))
        {
            var evil = archive.CreateEntry("../outside.txt");
            using var stream = new StreamWriter(evil.Open());
            stream.Write("ESCAPE");
        }
        wasBlocked = false;
        try { Program.ExtractStrict(zip,Path.Combine(root,"extract")); }
        catch (InvalidDataException) { wasBlocked=true; }
        Assert(wasBlocked,"ZIP traversal was not prevented.");
        Assert(!File.Exists(Path.Combine(root,"outside.txt")),"ZIP wrote outside extraction directory.");
        Console.WriteLine("PASS: ZIP traversal rejected before application replacement.");

        var maliciousJournal = Program.StageTransaction(publish, install);
        Program.ApplyTransaction(maliciousJournal,install);
        File.WriteAllText(target, "EXTERNALLY MODIFIED");
        wasBlocked=false;
        try { Program.RollbackTransaction(maliciousJournal,install); }
        catch (InvalidDataException) { wasBlocked=true; }
        Assert(wasBlocked,"Rollback overwrote an externally changed executable.");
        Console.WriteLine("PASS: rollback refuses unexpected externally modified application bytes.");
    }

    internal static int Main()
    {
        var temp=Path.Combine(Path.GetTempPath(),"AAH-UpdateHost-Test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Test(temp);
            Console.WriteLine("PASS: 7/7 AAH UpdateHost transactional safety scenarios.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(temp,true); } catch { }
        }
    }
}
