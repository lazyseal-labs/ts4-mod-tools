# Polish main-menu label test mod

This is a one-resource Sims 4 package that changes the Polish main-menu text `Nowa gra` to `Nowa gra (S4MT test)`. It is a visible smoke-test fixture for S4MT's STBL editing and package build path.

## Manual test

1. Close Sims 4.
2. Copy `dist/S4MT-PolishMenuLabel.package` into `Documents/Electronic Arts/The Sims 4/Mods`.
3. Launch the game with Polish selected. If the game says custom content is disabled, enable it in Game Options and restart.
4. At the main menu, check that the button reads `Nowa gra (S4MT test)`.
5. Close Sims 4 and remove `S4MT-PolishMenuLabel.package` to restore the original game text.

The package overrides game resource `0x220557DA:0x00000000:0x0F54935F2685AD48`; another mod replacing that same resource can affect which text appears. Remove this test package after the check.

## Rebuild

From the repository root, run:

```powershell
dotnet build S4MT.sln
pwsh -NoProfile -File scripts/create-polish-menu-mod.ps1
```

The script uses the installed game's `Strings_POL_PL.package` as its source and writes the ready-to-copy package to `dist/S4MT-PolishMenuLabel.package`. It refuses to overwrite the project on a second run; pass `-Force` only when you want to regenerate this example's source files. The package manifest points to `resources/polish-menu.stbl`.
