Fix137 guarded Stock persistence regression suite.

Run on Windows with the .NET 10 SDK:
  dotnet run --project Tests\AAH.StockProjection.Tests\AAH.StockProjection.Tests.csproj --configuration Release --no-launch-profile

This executable links the actual production StockProjectionPersistence and contract
source files. It uses isolated in-memory SQLite tables; it never opens the user's
AAH database, connects to Tally, or modifies client data. The 19 scenarios cover
both Purchase and Sales, edited and unedited source rows, protected accounting fields,
exact source revision guards and both user-edit flag transitions,
no-op timestamps, client/FY isolation, deleted rows, malformed JSON, duplicate IDs,
source evidence, and restricted SQL table names. The normal Windows installer
executes this suite before application replacement. It does not replace the
existing Fix136 cache lifecycle suite or the complete WPF publish check.

Fix137 R6 fixture correction:
The fixture now inserts the same IsUserEdited value into the SQLite column and
RecordJson and captures that value in the expected revision. Previously the JSON
was true while the SQLite column and expected revision were false. That made the
original preservation assertion fail even when production wrote the correct data.
Both flag states are now tested, and the metadata-conflict test changes the flag
from its original value so it cannot accidentally test an unchanged revision.
Fixture consistency is checked before each scenario. No production code is modified.
