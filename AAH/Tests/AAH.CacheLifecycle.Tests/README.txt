Fix136 cache lifecycle tests
Run RUN_CACHE_TESTS.bat on Windows with .NET 10 SDK installed.
The project links the actual cache service source and compiles a standalone console test.
No AAH database, installed application, Tally connection, or user data is accessed.
The tests create isolated temporary files and delete their own test directory.
A nonzero exit blocks the Fix136 updater preflight.
The normal Windows WPF publish still runs afterwards before replacing the installed app.
