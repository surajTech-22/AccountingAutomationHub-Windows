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
        Console.WriteLine("PASS existing-unowned mutex, live-owner exclusion and abandoned-owner restart.");
    }
}
