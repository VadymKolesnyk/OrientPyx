---
paths:
  - "**/*Relay*"
  - "**/*Scatter*"
  - "**/*Mixed*"
  - "**/*CourseVariant*"
  - "**/*LegCourse*"
  - "**/*TeamCourse*"
  - "**/*NumberVariants*"
  - "**/*SplitGroups*"
  - "**/Iof*"
  - "**/Uof*"
  - "**/*Matcher*"
  - "docs/relay-scenarios.md"
---

# Course formats (Mixed, Scatter, relay courses) and relay

Notable formats: **Mixed** (змішаний) judges against a course *pattern* (`<seq>` ordered runs,
`[N …]` any-of blocks, nesting) like a set course. **Scatter** (розсіювання) has several valid
orders (variants) per (day, group) stored in the `GroupCourseVariant` event-DB table (renamed from
`ScatterVariant`); each runner's variant is auto-detected from their read-out (best-matching order,
`Toolkit.VariantMatcher`) and then judged like a set course against it. A variant may also be a mixed
pattern (matched through `CourseMatcher`, never expanded into orders). **Relay** (естафета) legs run the
group's course, or — per `GroupDaySettings.CourseAssignmentMode` (Р-41) — a named course from the same
`GroupCourseVariant` rows: `ByBib` (`LegCourseAssignment` per leg number, typed on «Команди» or brought by the course
import — there is no separate leg-course file import), `TeamSet` (a course set per team — its own, else its
leg numbers' `LegCourseAssignment` codes, else the group's — distributed over the read legs by
`Toolkit.TeamCourseAssignment`, least missed controls, Р-42), `PerLeg` («своя на кожен етап»: leg N of every team
runs a variant whose `GroupCourseVariant.Leg` = N — the «Етап» column the same «Групи» courses table shows only in this
mode; several on one leg = the read-out's best match (`VariantMatcher.Best`), unread = «не визначено»; none = «дистанцію
не призначено»; `Leg` is saved in every mode so switching modes loses nothing; a course re-imported under the same code
keeps its leg; outputs show `RelayLegResult.ShownCourseCode` (except the monitor and online results — the variant is not shown there), null only for a code equal to the leg number — migration
`GroupCourseVariantLeg` moved the former «code = leg number» rows into `Leg`). The former `ByLeg` mode is gone (migration
`DropRelayByLeg` turns it into `ByBib` rows). `LegCourseAssignment.GroupId` (nullable) tells the row's group; the
calculator ignores a row naming another group.

**OCAD variations by number.** `IofXmlParser` keeps every variation in `IofCourse.AllVariants` (nothing merged — a
relay's «1AAAAA» and «4AAAAA» run the same controls) besides the distinct `Variants`, and reads the file's number/leg
assignments into `IofCourse.TeamCombinations` (2.0.3 `<TeamCombination>` by `CourseVariationId`, 3.0
`TeamCourseAssignment` / `PersonCourseAssignment` by course name). «Розділення груп» (`SplitGroupsViewModel`) asks per
course with variations «Розсіювання» / «Естафета» + «варіант за номером етапу» / «набір на команду»
(`IofCourse.VariationUse`, default relay for a multi-leg OCAD relay, a relay day or an existing relay group);
`ImportGroupsAsync` then makes a relay group (`LegCount` from the file, all variations, the assignments via
`IEventStore.ReplaceGroupLegCourseAssignmentsAsync`) or a scatter one (all variations when numbered, else the distinct
orders). «Змагання → Естафета → Варіанти за номерами» (beside «Команди»; `NumberVariantsViewModel`,
`GetNumberVariantsAsync` → `NumberVariantsData`) lists number → variation for map handout: `Rows` (every stored
assignment, with its own group and the group of the team/runner holding the number — `GroupMismatch` = the calculator
never applies it, both cells red; F8 «Перенести в групу команди» rewrites the row's group) and, in a second table,
`Unassigned` (legs of numbered teams in `ByBib`, or `TeamSet` without a set of their own; numbered runners of a
non-relay group that has rows). The variation cell is a combo of the group's variants in both tables, saved at once via
`SetLegCoursesAsync` (`LegCourseEntry.GroupId` = the row's group; blank removes the row); rows stay put until reload.
The upper table exports like «Кількість карт».

**IOF 3.0 course details** (`IofXmlParser`): a file of several races reads the `RaceCourseData` whose `raceNumber` is the
day number (`Parse(xml, raceNumber)`; none ⇒ `RaceUnresolved`, `XmlImportFlow` asks for the race); `CrossingPoint` /
`EndOfMarkedRoute` controls are neither control points nor part of an order; a `CourseControl` with several `Control`s
(«one of») or a run of `randomOrder="true"` becomes `IofCourse.Pattern` (`[1: 31 32]`, `[3: 41 42 43]`) and
`ImportGroupsAsync` makes such a single course a «Змішаний» group (`GroupImportResult.MixedGroups`, reported); lengths
are xsd:double, rounded. Every variant keeps the file's course name / family (`GroupCourseVariant.SourceCourseName` /
`SourceCourseFamily`, carried by code across a hand edit) — the IOF export names the runner's course by it.

## Relay (естафета)

Built-in discipline `DisciplineId.Relay` (`RelayStrategy`, `CompetitorModel.TeamWithLegs`; rules Р-20…Р-44 in
`docs/plugins-plan.md`, worked examples in `docs/relay-scenarios.md`). `RelayTeam` (day + group, number unique per
day) holds the team; a runner is placed on a leg via `ParticipantDay.RelayTeamId` + `Leg`; the leg number «100.2»
is computed (`Toolkit.RelayBib`), never written to `Participant.Number`. Group settings: `LegCount`,
`MassStartTime`, restarts (`RestartTimesJson`, `RestartRanking`), `CourseAssignmentMode`. The calculator's relay
pass (`DayResultsCalculator.Relay.cs`) judges legs in sequence (leg N starts at leg N−1's finish or the restart),
sums adjusted leg times, ranks teams and copies place/points to every member; `TeamDayResult` /
`RelayLegResult` carry the team and leg results, `ParticipantDayResult.Team/Leg/LegBib` link a runner to them.
Pages/outputs: «Команди» (teams + legs), day-grid team/leg columns, read-out log, result/start protocols
(`ResultProtocolGroup.RelayTeams`, sub-rows), import (CSV `relay.*` targets, IOF `EntryList` `TeamEntry`), draw
(a team is the unit, leg 1 gets the minute), monitor (`MonitorRowKind.Team/Leg`), HTML splits (leg start + leg
course), summary («за очками» counts relay points; «за часом» skips relay days, Р-39), online (`relay_teams`).
- **A team may have no number** (`RelayTeam.Number` is `int?`): a UOF import makes its teams without numbers, the user
  types them later (in step with OCAD). Show/sort it through `Models/RelayTeamNumbers` (`Text`/`Bib`/`SortKey`/`Label`
  — blank number, blank «100.2», sorted last); such a team is skipped online (`relay_teams.num` is the key) and gets
  no «за номером етапу» course; plugins see `TeamCompetitor.Number` = 0.
- **A runner may be on a team without a leg** (`RelayTeamId` set, `Leg` null — «без етапу»): an import with more
  runners than legs leaves the extras so; evaluated alone, never placed; listed in `RelayTeamRow.Unplaced` and in red
  on «Команди»; `IEventStore.ReleaseRelayRunnerAsync` takes a runner off the team.
- **UOF relay** (`UofXmlParser`): «3Е» in `ProgEvent` = the runner's relay day (`UofParticipant.RelayDayNumbers`),
  `GrupRel` = the relay group there (`GroupOn(day)`), `Prim` = team name, `PsRel` = leg (`<RelayLeg>` is a region-team
  number, not read). The import matches a number-less team by its exact name in the group, sets a missing `LegCount`
  from the file, marks the relay group-days (`RelayGroupDay`) or makes a new all-relay day a relay day, never skips a
  runner, and reports teams created / without a leg / without a team (`ParticipantImportResult`, `RelayImportReport`).
  A **relay event of its own** (baz4051 «2-х етапні естафети»: no «Е» day and no `GrupRel` anywhere) is read otherwise:
  `Group` = relay group, `Prim` = team, `RelayLeg` = leg (`PsRel` 0; a file filling `PsRel` keeps it); such rows are
  `UofParticipant.RelayOnImportedDays` ([JsonIgnore]) and `ResolveImportedRelayDaysAsync` (editor, before the store)
  gives them the import's days — the target day, else the file's days, else the competition's relay days, else every day.
  After any participant import, `RelayDaySwitchOffer` asks per day with new teams whose own type is not a relay
  (`GetRelayDaySwitchesAsync`) to make it one (`SwitchDayToRelayAsync`: groups keep what they run — the former type
  becomes their own override, a built-in relay override is cleared).
