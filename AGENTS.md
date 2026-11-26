# ArkPet Repository Guidelines

ArkPet 是 Windows 桌面寵物。桌面功能可離線使用，下載和檢查更新需要網路連線。

## Project Structure

`ArtPet.slnx` contains the C#/.NET 10 WPF desktop app in `src/ArkPet/`. `Desktop/` owns the pet and settings windows, tray icon, and Win32 interop. `Media/` handles character packs, animation, and voice. `Wardrobe/` contains the outfit picker and previews. `docs/` covers architecture and acceptance, and `scripts/` contains asset migration, validation, and voice import tools.

Keep character frames, Spine models, and local settings under `local-data/`. That directory and release output under `artifacts/` are Git-ignored.

## Build and Run

Run commands from the repository root on Windows with the .NET 10.0.4xx SDK specified in `global.json`.

- `dotnet build ArtPet.slnx -c Release` checks that the solution compiles.
- `dotnet run --project src/ArkPet/ArkPet.csproj -c Debug` builds and launches the current source for routine testing.
- `dotnet run --file scripts/ValidateHoolheyakPack.cs -- '<pack-directory>'` checks the character pack manifest and frames. Pass the directory that contains `character.json`.

## 啟動與操作

開發版優先從 `local-data/assets/霍爾海雅` 載入預設造型、精英二立繪、歷世流風和恆久祭禮。精英二有自己的立繪，並共用預設造型的基建模型與動畫。舊版 `local-data/assets/hoolheyak/desktop-60` 和 `desktop` 仍可載入，沒有可用造型時，可從通知區開啟設定或衣櫃。

- 100% 縮放時，桌面模型依待機畫面的可見高度調整至約 90 DIP。小人預設出現在主螢幕工作區右下角，並置頂。
- 顯示或換裝時先播 Idle。待機循環以 12 次為預設基準，每段會隨機選擇循環次數和下一個動作。抽到同一動作時會接著播放，換動作時淡化過渡。Move 會在桌面原位播放。短按互動結束後會回到原待機動作。
- 安裝完整資源包後，短按霍爾海雅會隨機播放一條語音，預設使用日語配音與繁體中文字幕。 [語音與字幕](docs/hoolheyak-voice.md)
- 按住可將角色拖到螢幕邊緣或拖出螢幕。右鍵可開衣櫃、切換置頂或隱藏。衣櫃以立繪顯示角色列表，可按角色名稱搜尋，選擇角色後可查看各造型的模型預覽和立繪。
- 右鍵選「轉身」可水平翻轉目前小人。在「設定 → 一般」開啟自由模式後，小人會依造型可用的動畫，隨機選擇散步、發呆、坐下或睡覺。每段行走的方向、距離和步速，以及停留時間都會變動。可調整「自由模式步速」為預設慢步的 50%–150%。互動期間會暫停走動。
- 在「設定 → 一般」開啟「物理模式」後，把小人拖到半空中再放開，就會落到可見的視窗上緣、工作列上緣或螢幕底部。有坐姿或睡姿的造型會在對應的位置休息。若也開啟自由模式，小人會沿著支撐面散步，走出邊緣時繼續下落。
- 從小人右鍵選單或通知區選「新增一隻小人」，會新增相同造型的小人。每隻可獨立換裝、拖曳和隱藏。通知區的「管理小人」可找回隱藏的小人或移除新增的小人。重新啟動時從一隻小人開始。
- 右鍵選單和工作列通知區都可開啟設定視窗。「一般」可調整縮放、幀率、待機循環基準和互動模式，也可為目前 Windows 使用者啟用登入時啟動。「外觀」可分別調整高亮描邊和陰影。「語音與字幕」可調整配音、字幕和語音音量，音量設為 0% 即靜音。循環次數可直接輸入，按 Enter 或離開欄位即可套用。畫面與語音偏好會自動保存到 `local-data/settings.json`。關閉互動模式後，滑鼠會穿透角色，仍可從通知區開設定恢復。
- 「設定 → 一般」的「模型建構硬件加速」預設開啟，產生新影格時會優先使用 GPU，失敗時回退至軟件渲染。關閉後會直接用軟件渲染。可在「設定 → 資源管理」按「更新全部模型」，程式會下載所有官方角色的素材、圖示和字幕。資源管理沿用角色和造型卡片，可手動建置或刪除動畫影格。在衣櫃套用造型時也可按需生成影格。普通話配音可在資源管理獨立下載或刪除。新版模型清單於下次啟動在衣櫃生效。「檢查程式更新」可另外更新 Windows x64 自包含程式。程式會以 GitHub 提供的 SHA-256 驗證下載檔案。
- 從通知區的 **ArkPet** 圖示可開衣櫃、顯示、隱藏、切換置頂、移回主螢幕右下角或結束。找不到圖示時，展開通知區的隱藏圖示。雙擊圖示會顯示角色。
- 造型、位置、置頂及顯示狀態在當次執行期間有效。再次啟動會套用預設造型和桌面位置。同一 Windows 登入工作階段只會執行一個實例。

## 匯入本機角色素材

新增或重建模型時優先使用專案根目錄的 `Ark-Models`，立繪則取自同層的 `ArknightsGameResource`。匯入需要 Node.js 22、Microsoft Edge，以及首次下載 Spine 3.8 匯出工具的網路連線。 [Ark-Models](https://github.com/isHarryh/Ark-Models) · [ArknightsGameResource](https://github.com/yuanyan3060/ArknightsGameResource)

```powershell
npm.cmd install --prefix local-data/tools --no-save --no-package-lock playwright-core@1.56.1
node scripts/ImportHoolheyakAssets.mjs
dotnet run --file scripts/ValidateHoolheyakPack.cs -- 'local-data/assets/霍爾海雅'
```

匯入會將三套基建模型的 `Relax`、`Move`、`Sit`、`Sleep` 和 `Interact` 匯出成 320 × 320、60 FPS 的透明 PNG，各造型共用畫布比例和錨點。原始 Spine 模型存放於 `local-data/models/霍爾海雅/<造型名>`。重複匯入時，舊素材會保存在 `local-data` 的備份目錄。已有舊版影格也可用 `pwsh -File scripts/MigrateHoolheyakAssets.ps1` 移到新版目錄結構。

## 製作本機測試包

先匯入角色素材，再從專案根目錄建置含素材的 Windows x64 自包含測試包。

```powershell
dotnet publish src/ArkPet/ArkPet.csproj -c Release -r win-x64 --self-contained true -p:BundleHoolheyak=true -o artifacts/ArtPet-win-x64
```

測試包的執行檔位於 `artifacts/ArtPet-win-x64/ArtPet.exe`。程式依賴和 .NET 執行環境位於 `data`，霍爾海雅的造型素材位於 `resource/assets`，授權文件位於 `resource/licenses`。如果把測試包移出專案，設定會寫到執行檔旁的 `local-data/settings.json`。

## Publish GitHub Releases

Program and artwork releases have independent tags and update checks. Release only the channel that changed. Run packaging commands from the repository root in PowerShell 7 and use a fresh output directory for each release. For the first rollout, initialize the public `JeffLiHK/ark_pet_assets` repository with a commit to tag. Publish the initial asset release, then publish the first program version that supports it. Set the initial asset release's minimum program version to that program version. Existing users keep their bundled or locally imported assets when upgrading. Fresh installations download the latest compatible artwork and Japanese voice pack during startup.

### Program release

1. Set `Version` in `src/ArkPet/ArkPet.csproj` to the three-part version that matches the requested tag. For example, `v1.3` maps to `1.3.0`. Review against the previous release, stage the intended files by path, commit, and push the commit and tag to `origin`. Confirm that the remote tag points to the intended commit.
2. Build the tagged source on Windows with the SDK selected by `global.json`. Use the [program packaging commands](#program-packaging) to build and ZIP the release under `artifacts/`. Check the assembly version of `data/ArtPet.Windows.dll`. Check that `ArtPet-win-x64.zip` contains `ArtPet.exe`, `data/`, and `resource/` at its root, with no bundled character assets or `local-data/` entries. Record its SHA-256.
3. Create a public, stable Release in `JeffLiHK/ark_pet` from the existing tag. Attach the ZIP and mark the Release as latest. With an authenticated GitHub CLI, use `gh release create <tag> <zip> --repo JeffLiHK/ark_pet --verify-tag --latest --title <title> --notes-file <file>`.
4. Query the public [program latest release endpoint](https://api.github.com/repos/JeffLiHK/ark_pet/releases/latest). Verify the tag, download and GitHub asset digest against the local ZIP. Check that the previous version detects the release. For the first release with `ArtPet.exe`, include the [manual upgrade steps](#manual-upgrade-from-artpetwindowsexe) in the release notes and verify that they preserve installed character assets and settings. For the first release that uses `JeffLiHK/ark_pet`, include the manual upgrade steps from README.md in its release notes. v1.7.0 validates the download URL against the original repository path. For later releases, verify preservation through the in-app update.

#### Program packaging

Set `$programVersion` to the same three-part version as the project file. The tag can use `vX.Y` or `vX.Y.Z`.

首次建置會下載並驗證固定版本的 Spine 3.8 WebGL Runtime，程式將它內嵌供離線轉檔。對外發佈程式前，請確認符合 [Spine Runtime 授權](https://github.com/EsotericSoftware/spine-runtimes/blob/8b4844bd4b193ba9e54487ed397a777993cbad56/LICENSE)。

```powershell
$programVersion = '1.7.0'
$programOutput = "artifacts/program-v$programVersion"
dotnet publish src/ArkPet/ArkPet.csproj -c Release -r win-x64 --self-contained true -o "$programOutput/publish"
if ($LASTEXITCODE -ne 0) { throw '程式本體打包失敗。' }
Compress-Archive -Path "$programOutput/publish/*" -DestinationPath "$programOutput/ArtPet-win-x64.zip"
```

#### Manual upgrade from ArtPet.Windows.exe

從使用 `ArtPet.Windows.exe` 的版本首次升級時，請關閉程式，再把 ZIP 解壓到新資料夾。將舊安裝目錄中的 `local-data`、`assets` 及 `resource` 下的素材資料夾（如有）複製到新安裝的對應位置，再開啟 `ArtPet.exe`。如果原本有啟用登入時啟動，請在設定中關閉後重新啟用，以更新 Windows 的啟動路徑。以後可繼續使用程式內更新。

### Artwork release

1. Choose the next three-part asset version and the oldest supported three-part program version. Increase the asset version for each new package. For a character format change, release a program version that can read both the installed packs and the new schema before requiring that schema in the next asset package.
2. Run the [artwork packaging commands](#artwork-packaging) on Windows. Microsoft Edge WebView2 Runtime generates Spine frames on demand.
3. In `JeffLiHK/ark_pet_assets`, create and push the matching `vX.Y.Z` tag. Attach all four output files to the public, stable Release and mark it as latest. Use `gh release create <tag> <models-zip> <ja-zip> <zh-CN-zip> <manifest> --repo JeffLiHK/ark_pet_assets --verify-tag --latest --title <title> --notes-file <file>`. Keep the attachment names stable and assign a new version when replacing a published payload.
4. Query the public [asset latest release endpoint](https://api.github.com/repos/JeffLiHK/ark_pet_assets/releases/latest). Verify all four downloads and SHA-256 digests. On a clean compatible build, click 「更新全部模型」 and confirm that models, subtitles, and Japanese audio install with empty Spine caches. Restart, select a model in the wardrobe, then check manual model build and deletion. Download and delete Mandarin independently. Verify that clients below the minimum version decline the download.

#### Artwork packaging

Set the two version variables for the release. The split model and voice package format was introduced in program version `1.3.3`. Use the minimum version that supports the schema you are releasing.

```powershell
$assetVersion = '1.2.1'
$minAppVersion = '1.3.3'
pwsh -File scripts/ImportHoolheyakVoice.ps1
if ($LASTEXITCODE -ne 0) { throw '語音匯入失敗。' }
pwsh -File scripts/BuildArtworkAssetRelease.ps1 -AssetVersion $assetVersion -MinAppVersion $minAppVersion
```

The script writes four attachments to `artifacts/assets-v<version>/`. Schema 5 stores subtitles with the models and images.

- `ArtPet-assets.zip` 包含角色、圖片、圖示及日文和繁體中文字幕。
- `ArtPet-voice-ja.zip` 包含日語配音。
- `ArtPet-voice-zh-CN.zip` 包含普通話配音。
- `assets-manifest.json` 記錄資源版本和最低程式版本。

新增官方角色時，將含 `character.json` 的角色資料夾放入 `local-data/release-characters/<角色 ID>/`。同名資料夾可提供現有角色的替換素材。Spine 角色可隨包提供 `portraits/<造型 ID>.png`、`previews/<造型 ID>.png` 和 `icons/<造型 ID>.ico`，其他版本可在造型清單指定圖片。共用圖片可放入 `local-data/release-images/`，應用程式圖示可用 `-AppIcon` 指定。新增角色的語音可放在 `local-data/voices/<角色 ID>/<語言代碼>/`，或透過 `-VoiceDirectory` 指定準備好的語音根目錄。

#### Installed resource layout

正式版在執行檔旁的 `resource/managed-assets/` 下載、解壓和安裝模型、立繪、字幕和配音，並在同一處存放按需生成的影格。手動匯入的角色素材和配音可放在 `resource/imported-assets/` 和 `resource/voices/`。升級後首次啟動時，程式會把舊安裝目錄內的資源移到 `resource/`，目的資料夾已有內容時，舊資源會留在原處。共用資源驗證完成才切換版本，啟動時若共用資源不完整，會嘗試上一個版本、隨附素材及本機匯入素材。檔案總管顯示的 EXE 內嵌圖示由程式 Release 更新。

### GitHub Actions fallback

Use a temporary branch such as `codex/publish-vX.Y` if local Release upload is unavailable. Put the one-time workflow in the repository receiving the Release and trigger it on pushes to that branch.

- For a program release, check out the exact program release commit on a Windows runner and install the SDK selected by `global.json`.
- For an asset release, supply the intended runtime asset files or the locally validated attachments. Pin the program checkout that supplies the build and validation scripts when building the asset package on the runner.
- Give the workflow `contents: write` and set `GH_TOKEN` from its built-in `GITHUB_TOKEN` for the release step. Verify the uploaded asset digests against the input files.
- Wait for the workflow to finish, perform the public checks for the affected channel, then remove the temporary branch.

## Coding Style

Use UTF-8, four-space indentation, and C# braces on separate lines. Use `PascalCase` for types, methods, and public members, `camelCase` for parameters and locals, and `_camelCase` for private fields. Keep layout and styles in XAML. Run file reads and frame decoding off the UI thread. Use `rg --files` for file discovery and `rg` for content searches.

## README Updates

Whenever you add or change an ArkPet feature, update [README.md](README.md) in the same change. Describe what users can do, where to find the feature, and any change to how it works. Include features that are awaiting release and mark them as unavailable in the current public download. Keep the README focused on using ArkPet and put implementation and build details in the developer documentation.

## Testing

The solution currently has no automated test project or coverage gate. [The acceptance guide](docs/testing-and-acceptance.md) describes planned MSTest coverage. After code changes, build and run the relevant Windows checks from source. Check click-through, focus, dragging, tray behavior, or outfit loading as applicable. Verify pet scale and animation frame rate programmatically.

### 驗收條件

1. 霍爾海雅以預設造型出現在主螢幕工作區右下角。Idle、Move、Sit、Sleep 依設定完成循環後淡化輪換。原本正在使用的視窗保有前景，工作列沒有角色的視窗按鈕。
2. 點透明區域會操作下方視窗。短按角色會播放互動，已匯入語音時也會隨機播放一句並顯示字幕。按住拖曳可跨螢幕或靠近螢幕邊緣，並維持原本的前景視窗。
3. 先在衣櫃選角色，再從模型預覽與立繪卡選造型。衣櫃會標示目前造型，載入失敗時保留原造型。隱藏時停止動畫，通知區可恢復顯示或移回主螢幕右下角。
4. 通知區的「結束程式」移除視窗、圖示並結束程序。重複啟動只保留原有視窗和圖示。
5. 設定視窗的偏好在重啟後仍有效。關閉互動模式後，滑鼠可點到角色後方的視窗，可從通知區重新開啟互動。

### Windows 驗證紀錄

- 在 Windows 11 Pro、125% 顯示比例下啟動自包含 EXE，確認預設造型待機、透明區實際點擊穿透、短按播放互動、拖曳、右鍵開衣櫃、原地切換兩套造型及退出。
- 實際從右鍵設定切換到 125% 和 60 FPS，重啟後恢復選擇，再從選單切回 30 FPS。程式內測試確認兩個幀率選項有不同的畫面更新頻率，拖曳後縮放時會保持角色的腳底錨點。
- 程式內測試確認衣櫃切換造型時位置不變，快速切換以最後一次選擇為準，隱藏時停止動畫且仍可從衣櫃換裝。也驗證了成品內素材損毀時會嘗試本機素材。
- 兩套造型均已用程式驗證四段待機動作皆可播到，且 Interact 可中斷 Sleep 並回到待機。舊素材仍可循環播放 Idle。
- 新版素材包共有四張衣櫃卡和三套獨立模型。三套模型各有五段動畫，共 2,898 張影格。Windows 版已驗證逐套切換、立繪和圖示載入，以及換裝時保留桌面位置。素材檢查會核對清單指定的影格。

## 視窗與滑鼠

桌面視窗沿用 C#／.NET 10 與 WPF，以 `WindowStyle=None`、`AllowsTransparency=true` 和透明底建立分層視窗。Windows 讓零 Alpha 像素的滑鼠事件穿透。`ShowActivated=false` 和 `WM_MOUSEACTIVATE` 回傳 `MA_NOACTIVATE`，讓角色接收點擊而保留目前的前景視窗。每隻小人以 WPF `Topmost` 記錄置頂選擇，並在原生視窗的置頂狀態偏離時修正。 [Win32 分層視窗](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows) · [滑鼠啟用訊息](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate)

按下主鍵後，寵物按畫面更新節奏追蹤按鍵與游標。超過 Windows 的拖曳門檻後，用 `SetWindowPos` 在實體像素座標移動並保留前景。短按放開時用 `WindowFromPoint` 確認命中角色。Per-Monitor V2 用於跨螢幕 DPI 切換。 [程式架構](docs/architecture.md#桌面互動與動畫)

## 後續里程碑

1. **設定與復原。** 保存相對工作區的錨點、螢幕、置頂和造型，實測混合 DPI、拔除螢幕和解析度變更。
2. **效能及相容性。** 以 `win-x64` 自包含測試包量測正式素材在 Windows 11 和 Windows 10 21H2 LTSC x64 的互動、動畫和資源使用，再評估安裝程式。 [發佈與效能門檻](docs/architecture.md#效能設計與發佈門檻) · [.NET 的 Windows 支援版本](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions)

養成和工作提醒保留為後續產品方向。第一版以安靜陪伴為主。

## Commits and Pull Requests

After completing and verifying each task that changes repository files, commit the task changes and push the current branch to its upstream remote before reporting completion. Leave unrelated working-tree changes out of the commit. Follow any explicit instruction to skip committing or pushing for the current task.

Keep commits focused and use descriptive subjects such as `feat: add wardrobe outfit switching` or `fix: recover from invalid animation metadata`. Stage files by explicit path and review `git diff --cached --name-status`. In pull requests, summarize the change, record verification, link related issues when available, and include screenshots for visible UI changes.
