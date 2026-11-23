---
paths:
  - "**/*CustomField*"
  - "**/*ParticipantField*"
  - "**/*Import*"
  - "**/Controls/Roster/**"
---

## Additional participant fields

A competition can define its own participant fields (Р-13…Р-19 in `docs/plugins-plan.md`): `CustomFieldDef`
(key PK, label, kind Text|Number|Bool|Choice, choice options + synonyms as JSON, source `user`/`plugin:<id>`) and
`ParticipantFieldValue(ParticipantId, Key, Value)` in `event.db`. Values are participant-level, deleted with the
participant (`DeleteParticipantAsync`, delete-all, import clear-first) and with the field. The domain model is
`CustomField` (BusinessLogic/Models) — `Normalize` is the single place a raw value becomes the stored one (choice
synonym → value via `TextKey`, number comma/dot), `IsInvalid` drives the red cell. Service API on
`ICompetitionEditorService`: `Get/Add/Update/DeleteCustomFieldAsync`, `SetParticipantFieldAsync`,
`EnsurePluginFieldsAsync` (a plugin's `AddParticipantField` fields are created when its discipline is first used).
Both participant tables add one `field.<key>` column per field (`Controls/Roster/CustomFieldColumns.cs`, binding the
row's `Fields[key]` cell VM); export and the statement pick them up. CSV/Excel import maps onto `ImportTarget`
(string-keyed: built-ins by their old enum names, `field.<key>`, `relay.teamNumber|teamName|leg`), remembers the last mapping per
header set (`ImportMappingRow` in `event.db`, app default in `AppSettingsRow.ImportMappingsJson`) and can create a new
field from a file column («+ Нове додаткове поле…»).
