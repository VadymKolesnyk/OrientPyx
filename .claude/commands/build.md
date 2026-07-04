Restore and build the OrientPyx solution.

```bash
dotnet restore
dotnet build OrientPyx.sln
```

The build must succeed with **0 warnings and 0 errors**. If you added a new project,
make sure it is in `OrientPyx.sln` and that its project references respect the
dependency direction in `CLAUDE.md` (BusinessLogic must not reference Avalonia/EF Core;
EF Core and SQLite live only in DataAccess).
