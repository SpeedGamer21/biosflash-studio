# Classic UI backup (before the 2026-11-06 declutter)

These are the UI files exactly as they were in commit `cc614f4` (also tagged `ui-classic`).
This folder is outside `src/`, so it is not compiled into the app.

## Restore the old UI

Copy the files back over the new ones and rebuild:

```sh
cp ui-backup/classic-2026-11-06/App.axaml            src/
cp ui-backup/classic-2026-11-06/MainWindow.axaml     src/
cp ui-backup/classic-2026-11-06/MainWindow.axaml.cs  src/
cp ui-backup/classic-2026-11-06/MainWindow.Logo.cs   src/
```

Or with git: `git checkout ui-classic -- src/App.axaml src/MainWindow.axaml src/MainWindow.axaml.cs src/MainWindow.Logo.cs`

`src/Core/Core.cs` gained a `ShowDetails` setting in the new UI; leaving it in place is harmless for the old UI.
