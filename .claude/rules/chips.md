---
paths:
  - "**/*Chip*"
  - "**/*SiCard*"
---

## Chip capacity (SportIdent card model)

`Toolkit.SiCard.Identify(number)` maps a chip number onto its SportIdent card model and punch capacity by the
SPORTident number ranges (Card 5 = 30, Card 6 = 64 (192 station-dependent), Card 8 = 30, Card 9 = 50, pCard 20,
tCard 25, Card 10/11/SIAC = 128, fCard 20); an unknown number is `null` and never judged. `Models/ChipCapacity.
NeededPunches(groupDayRow, strategy)` = max(course control count, longest named course / scatter variant) and is
carried on `GroupOption.ControlCount`. Surfaces: «Чіпи» shows read-only «Тип картки» / «Відміток» columns; the
day grid and the roster amber-tint a chip cell whose card holds fewer punches than the group's course + `SiCard.Reserve` (2 spare)
(`ChipTooSmall` / `ChipCapacityTooltip` on both row VMs and `RosterDayCellViewModel`); «Сканування чіпів»
holds such a chip back (`ChipScanCandidate.NeededPunches`) with «Пропустити» / «Присвоїти все одно», like a
rental-note mismatch. **All of it is gated on the app-level «Тип відмітки» = SportIdent** (another system's numbers
say nothing about the card): `ParticipantsViewModel.UsesSportIdentCardsAsync` feeds `ControlCount` = 0 otherwise,
`ChipsViewModel.ShowCardColumns` / `ChipScanViewModel.ShowCardColumn` hide the columns; a Settings change applies on
the next page load.
