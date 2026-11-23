---
paths:
  - "**/Network/**"
  - "**/*Station*"
  - "**/SessionService*"
  - "docs/network-*.md"
---

# Network mode — host & station internals

(The boundary rules — routed interfaces, the RPC contract, identifier-not-path, the change bus — stay in
CLAUDE.md «Network mode».)

- **Host** (`DataAccess/Network/Host`, Kestrel via `FrameworkReference Microsoft.AspNetCore.App`, minimal API, no
  URL ACL): `/pair` (4-digit PIN → bearer token stored in `app.db` `StationTokens`), `/rpc/{iface}/{method}` (JSON
  array of args; `Idempotency-Key` on writes; `/jobs` for the import), `/changes`, `/version`, `/files/*`, `/log`,
  `/ping`; UDP discovery on 47831 (`DataAccess/Network/Discovery`), spoken `ConnectionCode` fallback. **Station**
  (`DataAccess/Network/Station`): `RpcChannel` (timeouts/retries), `RpcProxy` (`DispatchProxy`), `RemoteChangeListener`,
  `NetworkStation : INetworkClient`. Self-checks with one-click fixes: `IStationDiagnostics`
  (`DataAccess/Network/Diagnostics`; nothing changes the OS silently, every fix is a button and is logged).
- **UI**: «Файл → Спільна робота…» is a global overlay (reachable before a competition is selected, because a station
  must connect first), the role indicator in the top bar, the `NetworkActivity` in the activities block and the
  «немає зв'язку» banner (`NetworkNoticeViewModel`) that disables editing while reconnecting. «Очки» / «Розряди» are
  read-only on a station.
- **Flaky networks** (a mobile router silently drops long-lived connections; field test 2026-11-07): the station's
  offline state comes only from `Station/ConnectionHealth` — offline = a failure **and** no answer of any kind for 15 s;
  any answer clears it. A silent `/changes` (3 s heartbeat, ~10 s silence) is reopened quietly and resumed from
  `Last-Event-ID` + stream epoch — never a banner by itself. `RpcChannel` retries within a window (`RpcRetryPolicy`:
  reads 20 s, writes 60 s with one idempotency key; short first attempt; idle pooled connections closed after 4 s),
  sends writes one at a time in call order, shows «Зберігається…» (`INetworkClient.DelayedWrites`) and a toast when a
  write is abandoned (`WriteFailed`). The host runs keyed calls with `CancellationToken.None`. Details:
  `docs/network-protocol.md`.
- **Last session**: `LastSessionRow.Mode/HostId` — a station pointer is restored only after reconnecting to the same
  host, never from the local folder. Builds: run `scripts/locked-build.ps1` when several terminals/agents build the
  same checkout.
