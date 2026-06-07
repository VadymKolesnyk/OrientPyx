Run the OrientDesk Avalonia desktop app.

```bash
dotnet run --project src/OrientDesk.Presentation
```

Expected on first launch (no last session to restore): the main window opens titled
**OrientDesk** with the **competition selection** screen (Вибір змагання). The app is
gated — until a competition **and** a day are selected, only the selection / creation
screens are shown.

- **Select a competition**: the list is built by scanning the events folder; pick a row
  and open it.
- **Create a competition**: asks for a user-friendly name, an identifier (the folder name
  under `./events`), and a venue.
- Once a competition + day are active, the working shell appears with the top menu and the
  Ukrainian pages: Панель (default), Учасники, Групи, Дистанції, Імпорт відміток,
  Результати, Оренда чіпів. Settings (Налаштування) is a global overlay from the top menu,
  not a sidebar page, and can switch the interface language at runtime.
- The active competition and day are shown in the window title and header, e.g.
  `OrientDesk — Чемпіонат міста 2026 (День: 1)`.

On subsequent launches the **last selected competition + day are restored automatically**
(the pointer is read from the app database once at startup), going straight to the shell.

Databases are created relative to the application directory:

- App database: `./data/app.db` (shared settings + last-session pointer).
- Event database: `./events/<identifier>/event.db` (one per competition).

Both `./data` and `./events` default to the app directory and are editable on the
Settings page.

Diagnostics: set `ORIENTDESK_OPEN_SETTINGS=1` to open the Settings overlay immediately on
startup (used for UI verification).
