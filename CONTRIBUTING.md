# Contributing

Thanks for helping with Elite HUD Studio.

## Reporting a problem

1. In Studio, open the **Setup** tab and click **Save a diagnostic report**.
2. [Open an issue](../../issues/new/choose) and attach that report.
3. Say what you did, what you expected, and what happened. A screenshot of the game helps a lot when a color shows up in the wrong place.

## Telling us a preview is wrong

The previews are matched to the game by hand, so some parts are still wired to the wrong setting or not wired at all. If a part of the preview doesn't match what you see in Elite, send an in-game screenshot and the name of the setting you changed.

## Changing the code

- `HUD Studio.html` is the whole user interface. It has no build step: edit it and reload.
- The helper exists twice and both must behave the same: `app/server.ps1` (browser version) and `app/launcher/Helper.cs` (app version). A change to one needs the same change in the other.
- Test against a copy of your `EDHM-ini` folder, not your real game: start the helper with `-GameDir <copy> -TestMode` (script) or `--game-dir <copy> --test-mode` (app).
- Keep the wording plain. Studio is meant for players who don't know the setting codes.

## Sharing a theme

Save it in Studio (Themes tab), then attach the `.json` file from the `My Themes` folder to an issue or pull request, with a screenshot.
