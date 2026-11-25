# Repository Guidelines

## Project Structure

`ArtPet.slnx` contains the C#/.NET 10 WPF desktop app in `src/ArtPet.Windows/`. `Desktop/` owns the pet and settings windows, tray icon, and Win32 interop. `Media/` handles character packs, animation, voice, and the bundled Sprout fallback. `Wardrobe/` contains the outfit picker and previews. `docs/` covers architecture and acceptance, and `scripts/` contains asset migration, validation, and voice import tools.

Keep character frames, Spine models, and local settings under `local-data/`. That directory and release output under `artifacts/` are Git-ignored.

## Build and Run

Run commands from the repository root on Windows with the .NET 10.0.4xx SDK specified in `global.json`.

- `dotnet build ArtPet.slnx -c Release` checks that the solution compiles.
- `dotnet run --project src/ArtPet.Windows/ArtPet.Windows.csproj -c Debug` builds and launches the current source for routine testing.
- `dotnet run --file scripts/ValidateHoolheyakPack.cs -- '<pack-directory>'` checks the character pack manifest and frames. Pass the directory that contains `character.json`.

For release packaging or published-build validation, use the self-contained `win-x64` publish command in [README.md](README.md).

## Publish GitHub Releases

Program and Hoolheyak asset releases have independent tags and update checks. Release only the channel that changed. For the first rollout, initialize the public `JeffLiHK/art_pet_assets` repository with a commit to tag. Publish the initial asset release, then publish the first program version that supports it. Set the initial asset release's minimum program version to that program version. Existing users keep their bundled or locally imported assets when upgrading. Fresh installations use Sprout until Hoolheyak is installed from Settings.

### Program release

1. Set `Version` in `src/ArtPet.Windows/ArtPet.Windows.csproj` to the three-part version that matches the requested tag. For example, `v1.3` maps to `1.3.0`. Review against the previous release, stage the intended files by path, commit, and push the commit and tag to `origin`. Confirm that the remote tag points to the intended commit.
2. Build the tagged source on Windows with the SDK selected by `global.json`. Use the [program publish command](README.md#github-更新包) and a fresh output directory under `artifacts/`. Check the assembly version of `ArtPet.Windows.dll`. ZIP the publish directory contents as `ArtPet-win-x64.zip` with `ArtPet.Windows.exe` at the ZIP root. Check that the archive has no `assets/` or `local-data/` entries and record its SHA-256.
3. Create a public, stable Release in `JeffLiHK/art_pet` from the existing tag. Attach the ZIP and mark the Release as latest. With an authenticated GitHub CLI, use `gh release create <tag> <zip> --repo JeffLiHK/art_pet --verify-tag --latest --title <title> --notes-file <file>`.
4. Query the public [program latest release endpoint](https://api.github.com/repos/JeffLiHK/art_pet/releases/latest). Verify the tag, download and GitHub asset digest against the local ZIP. Check that the previous version detects the program update and that updating preserves its installed character assets and settings.

### Hoolheyak asset release

1. Choose the next three-part asset version and the oldest supported three-part program version. Increase the asset version for each new package. For a character format change, release a program version that can read both the installed packs and the new schema before requiring that schema in the next asset package.
2. On Windows, run `pwsh -File scripts/BuildHoolheyakAssetRelease.ps1 -AssetVersion <version> -MinAppVersion <program-version>` from the repository root. The script writes `ArtPet-hoolheyak-assets.zip` and `assets-manifest.json` under `artifacts/assets-v<version>/`. The ZIP contains the schema 2 `character.json`, Spine skeletons, atlases, texture PNGs, and standalone portraits. The app generates and validates the animation frames locally using the Microsoft Edge WebView2 Runtime. Keep voice imports and user settings in `local-data/`.
3. In `JeffLiHK/art_pet_assets`, create and push the matching `vX.Y.Z` tag. Attach both output files to the public, stable Release and mark it as latest. Use `gh release create <tag> <zip> <manifest> --repo JeffLiHK/art_pet_assets --verify-tag --latest --title <title> --notes-file <file>`. Keep the attachment names stable and assign a new version when replacing a published payload.
4. Query the public [asset latest release endpoint](https://api.github.com/repos/JeffLiHK/art_pet_assets/releases/latest). Verify both downloads and SHA-256 digests. On a compatible program build, use Settings to install the package, restart and check the character and wardrobe. Confirm that an existing installation can still load its previous pack if the downloaded pack becomes unreadable, and that an incompatible build reports the requirement before downloading the ZIP.

### GitHub Actions fallback

Use a temporary branch such as `codex/publish-vX.Y` if local Release upload is unavailable. Put the one-time workflow in the repository receiving the Release and trigger it on pushes to that branch.

- For a program release, check out the exact program release commit on a Windows runner and install the SDK selected by `global.json`.
- For an asset release, supply the intended runtime asset files or the two locally validated attachments. Pin the program checkout that supplies the build and validation scripts when building the asset package on the runner.
- Give the workflow `contents: write` and set `GH_TOKEN` from its built-in `GITHUB_TOKEN` for the release step. Verify the uploaded asset digests against the input files.
- Wait for the workflow to finish, perform the public checks for the affected channel, then remove the temporary branch.

## Coding Style

Use UTF-8, four-space indentation, and C# braces on separate lines. Use `PascalCase` for types, methods, and public members, `camelCase` for parameters and locals, and `_camelCase` for private fields. Keep layout and styles in XAML. Run file reads and frame decoding off the UI thread. Use `rg --files` for file discovery and `rg` for content searches.

## Testing

The solution currently has no automated test project or coverage gate. [The acceptance guide](docs/testing-and-acceptance.md) describes planned MSTest coverage. After code changes, build and run the relevant Windows checks from source. Check click-through, focus, dragging, tray behavior, or outfit loading as applicable. Verify pet scale and animation frame rate programmatically.

## Commits and Pull Requests

Keep commits focused and use descriptive subjects such as `feat: add wardrobe outfit switching` or `fix: recover from invalid animation metadata`. Stage files by explicit path and review `git diff --cached --name-status`. In pull requests, summarize the change, record verification, link related issues when available, and include screenshots for visible UI changes.
