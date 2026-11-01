using AccountingAutomationHub.Desktop.Services;
internal static class SingleInstanceTests
{
    private static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
    public static void Run()
    {
        var name="Local\\AAH-instance-test-"+Guid.NewGuid().ToString("N");
        using (var retained = new Mutex(false,name))
        using (var candidate = new Mutex(true,name,out var created))
        {
            Check(!created,"Test did not retain an existing unowned mutex.");
            Check(SingleInstanceOwnership.TryAcquire(candidate,created),"An existing unowned mutex blocked startup.");
            candidate.ReleaseMutex();
        }
        using (var owner = new Mutex(true,name,out var created))
        {
            Check(created,"New instance mutex creation failed.");
            bool? secondAcquired=null;
            var contender=new Thread(()=>
            {
                using var candidate=new Mutex(true,name,out var secondCreated);
                secondAcquired=SingleInstanceOwnership.TryAcquire(candidate,secondCreated);
                if(secondAcquired==true)candidate.ReleaseMutex();
            });
            contender.Start();Check(contender.Join(5000),"Contender did not finish.");
            Check(secondAcquired==false,"An active owner allowed duplicate startup.");
            owner.ReleaseMutex();
        }
        Mutex? abandoned=null;
        var previousOwner=new Thread(()=> { abandoned=new Mutex(true,name); });
        previousOwner.Start();Check(previousOwner.Join(5000),"Previous owner did not finish.");
        try
        {
            using var candidate=new Mutex(true,name,out var created);
            Check(!created,"Abandoned test object was not retained.");
            Check(SingleInstanceOwnership.TryAcquire(candidate,created),"Abandoned owner blocked restart.");
            candidate.ReleaseMutex();
        }
        finally { abandoned?.Dispose(); }
        Check(AppExitPolicy.ShouldCleanupRuntime(isOwner: true), "Primary owner lost its cleanup obligation.");
        Check(!AppExitPolicy.ShouldCleanupRuntime(isOwner: false),
            "Secondary instance must not checkpoint the live owner's SQLite database.");
        Check(AppExitPolicy.CanExitCompletely(false, false, false), "Idle tray exit was blocked.");
        Check(!AppExitPolicy.CanExitCompletely(true, false, false), "Busy Tally was interrupted.");
        Check(!AppExitPolicy.CanExitCompletely(false, true, false), "Active auto import was interrupted.");
        Check(!AppExitPolicy.CanExitCompletely(false, false, true), "Distributed import was interrupted.");

        // UI/source contract: the window must offer a direct Exit route even if
        // Windows Explorer loses the tray icon. Closing X still hides to tray.
        var source = new DirectoryInfo(Environment.CurrentDirectory);
        while (source is not null &&
               !File.Exists(Path.Combine(source.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "App.xaml.cs")))
            source = source.Parent;
        Check(source is not null, "Could not find desktop source for exit integration regression.");
        var app = File.ReadAllText(Path.Combine(source!.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "App.xaml.cs"));
        var window = File.ReadAllText(Path.Combine(source.FullName, "AAH", "src", "AccountingAutomationHub.Desktop", "MainWindow.xaml"));
        Check(app.Contains("exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(RequestExitCompletely));", StringComparison.Ordinal) &&
              app.Contains("Environment.Exit(0);", StringComparison.Ordinal) &&
              app.Contains("AppExitPolicy.ShouldCleanupRuntime(_ownsSingleInstanceMutex)", StringComparison.Ordinal) &&
              window.Contains("Click=\"ExitCompletely_Click\"", StringComparison.Ordinal) &&
              window.Contains("Click=\"Close_Click\"", StringComparison.Ordinal),
              "Missing independent tray/window Exit, secondary termination, or owner-only cleanup.");

        Console.WriteLine("PASS: primary-only cleanup, no ghost secondary, independent tray/window Exit and safe busy gate.");
        Console.WriteLine("PASS existing-unowned mutex, live-owner exclusion and abandoned-owner restart.");
    }
}
