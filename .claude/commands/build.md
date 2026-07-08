Restore and build the OrientPyx solution.

```bash
dotnet restore
dotnet build OrientPyx.sln
```

The build must succeed with **0 warnings and 0 errors**. If you added a new project,
make sure it is in `OrientPyx.sln` and that its project references respect the
dependency direction in `CLAUDE.md` (BusinessLogic must not reference Avalonia/EF Core;
EF Core and SQLite live only in DataAccess).

If you touched localization, also run `python build/check-localization.py` — it must
report the uk-UA and en-US resources in sync (0 differing keys). The Stop hook runs this
automatically, but checking early saves a round-trip.
