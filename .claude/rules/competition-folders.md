---
paths:
  - "**/*EventCatalog*"
  - "**/*EventSelection*"
  - "**/*EventArchive*"
  - "**/*EventFolder*"
---

# Competition folders: rename and delete

- The identifier can be changed later on the Information page (`IEventCatalogService.RenameIdentifierAsync`):
  it renames the folder *and* rewrites `CompetitionInfo.Identifier` (the scanner reads it from the DB, so both
  must move together). The event DB is released first (`IEventStore.ReleaseAsync` — WAL checkpoint +
  `SqliteConnection.ClearAllPools()`), otherwise Windows refuses to move a folder holding an open file.
- A competition is deleted from the picker (bin column / Delete key → `EventSelectionViewModel.DeleteCommand`): one
  `ConfirmDialog` confirms and offers the archive («Зберегти архів і видалити» runs `IEventArchiveFlow.ExportAsync`
  first; a cancelled/failed save deletes nothing). `IEventCatalogService.DeleteEventAsync` moves the log out, releases
  the DB, moves `events/<id>` aside to `.deleted-<guid>` (all-or-nothing → `EventFolderInUseException`) then deletes
  it, and clears a local `LastSessionRow` naming it; only a folder directly under the events path. Host-only
  (`Wire.HostOnlyMethods`: the host refuses it over RPC, the station proxy fails it, the picker hides the bin).
