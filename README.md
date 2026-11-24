# Art Pet 桌面寵物

Art Pet 啟動後，霍爾海雅會在主螢幕工作列附近待機，隨機輪播 Idle、Move、Sit 和 Sleep。短按可播放基建互動與日語語音，按住可拖曳。衣櫃可選角色與造型。程式離線執行，免登入。

## 啟動與操作

日常除錯與測試直接從專案根目錄執行以下指令，需要 .NET 10 SDK `10.0.4xx`。開發版優先從專案的 `local-data/assets/霍爾海雅` 載入影格，造型分別位於「預設造型」和「歷世流風」子資料夾。舊版 `local-data/assets/hoolheyak/desktop-60` 和 `desktop` 仍可載入，素材缺失時會顯示原創的「小芽」。執行 `pwsh -File scripts/MigrateHoolheyakAssets.ps1` 可將現有影格與 Spine 模型依此結構複製。Spine 模型位於 `local-data/models/霍爾海雅/<造型名>`。

```powershell
dotnet run --project src/ArtPet.Windows/ArtPet.Windows.csproj -c Debug
```

- 畫布預設為 160 DIP，在主螢幕工作區右下角，離右邊及底部各 16 DIP，並置頂。
- 顯示或換裝時先播 Idle。每個待機動作預設完整循環 12 次，再淡化過渡到另一個動作。Move 會在桌面原位播放。短按互動結束後會回到原待機動作。
- 已匯入語音時，短按霍爾海雅會隨機播放一條日語語音，角色上方會顯示對應字幕。 [語音素材匯入方式](docs/hoolheyak-voice.md)
- 按住可將角色拖到螢幕邊緣或拖出螢幕。右鍵可開衣櫃、切換置頂或隱藏。衣櫃先選角色，再選造型。
- 右鍵選單和工作列通知區都可開啟獨立設定視窗，調整縮放、幀率、待機循環次數和互動模式。變更立即套用並保存到 `local-data/settings.json`。關閉互動模式後，滑鼠會穿透角色，仍可從通知區開設定恢復。
- 從通知區的 **Art Pet** 圖示可開衣櫃、顯示、隱藏、切換置頂、移回主螢幕右下角或結束。找不到圖示時，展開通知區的隱藏圖示。雙擊圖示會顯示角色。
- 造型、位置、置頂及顯示狀態在當次執行期間有效。再次啟動會套用預設造型和桌面位置。同一 Windows 登入工作階段只會執行一個實例。

## 製作本機測試包

需要交付成品或驗證發佈版本時，再執行打包指令。

兩套基建模型都使用 Spine 3.8.99，取 `Relax`（Idle）、`Move`、`Sit`、`Sleep` 和 `Interact` 匯出為共用腳底錨點的 256 × 256 透明 PNG，每秒 60 格。30 FPS 模式每兩格取一格，兩種模式的動作長度相同。實際畫面更新受素材幀率、螢幕更新率和系統負載影響。預設造型取自 PRTS，「歷世流風」取自 Ark-Models。原始模型、動畫影格和使用者設定都在專案的 `local-data`，由 Git 忽略。含素材的測試包放在 Git 忽略的 `artifacts` 下。 [PRTS 模型索引](https://prts.wiki/w/霍尔海雅/spine) · [Ark-Models](https://github.com/isHarryh/Ark-Models)

```powershell
dotnet publish src/ArtPet.Windows/ArtPet.Windows.csproj -c Release -r win-x64 --self-contained true -p:BundleHoolheyak=true -o artifacts/ArtPet-win-x64
```

重新打包後，可開啟 `artifacts/ArtPet-win-x64/ArtPet.Windows.exe`。自包含資料夾內有兩套造型和執行環境。如果把新版測試包移出專案，設定會寫到執行檔旁的 `local-data/settings.json`。

## 驗收條件

1. 霍爾海雅以預設造型出現在主螢幕工作區右下角。Idle、Move、Sit、Sleep 依設定完成循環後淡化輪換。原本正在使用的視窗保有前景，工作列沒有角色的視窗按鈕。
2. 點透明區域會操作下方視窗。短按角色會播放互動，已匯入語音時也會隨機播放一句並顯示字幕。按住拖曳可跨螢幕或靠近螢幕邊緣，並維持原本的前景視窗。
3. 先在衣櫃選角色，再從模型預覽與立繪卡選造型。衣櫃會標示目前造型，載入失敗時保留原造型。隱藏時停止動畫，通知區可恢復顯示或移回主螢幕右下角。
4. 通知區的「結束程式」移除視窗、圖示並結束程序。重複啟動只保留原有視窗和圖示。
5. 設定視窗的偏好在重啟後仍有效。關閉互動模式後，滑鼠可點到角色後方的視窗，可從通知區重新開啟互動。

## 視窗與滑鼠

桌面視窗沿用 C#／.NET 10 與 WPF，以 `WindowStyle=None`、`AllowsTransparency=true` 和透明底建立分層視窗。Windows 讓零 Alpha 像素的滑鼠事件穿透。`ShowActivated=false` 和 `WM_MOUSEACTIVATE` 回傳 `MA_NOACTIVATE`，讓角色接收點擊而保留目前的前景視窗。置頂交給 WPF `Topmost`。 [Win32 分層視窗](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows) · [滑鼠啟用訊息](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate)

按下主鍵後，寵物按畫面更新節奏追蹤按鍵與游標。超過 Windows 的拖曳門檻後，用 `SetWindowPos` 在實體像素座標移動並保留前景。短按放開時用 `WindowFromPoint` 確認命中角色。Per-Monitor V2 用於跨螢幕 DPI 切換。 [程式架構](docs/architecture.md#桌面互動與動畫)

## 後續里程碑

1. **設定與復原。** 保存相對工作區的錨點、螢幕、置頂和造型，實測混合 DPI、拔除螢幕和解析度變更。
2. **效能及相容性。** 以 `win-x64` 自包含測試包量測正式素材在 Windows 11 和 Windows 10 21H2 LTSC x64 的互動、動畫和資源使用，再評估安裝程式。 [發佈與效能門檻](docs/architecture.md#效能設計與發佈門檻) · [.NET 的 Windows 支援版本](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions)

養成和工作提醒保留為後續產品方向。第一版以安靜陪伴為主。

## Windows 驗證紀錄

- 在 Windows 11 Pro、125% 顯示比例下啟動自包含 EXE，確認預設造型待機、透明區實際點擊穿透、短按播放互動、拖曳、右鍵開衣櫃、原地切換兩套造型及退出。
- 實際從右鍵設定切換到 125% 和 60 FPS，重啟後恢復選擇，再從選單切回 30 FPS。程式內測試確認兩個幀率選項有不同的畫面更新頻率，拖曳後縮放時會保持角色的腳底錨點。
- 程式內測試確認衣櫃切換造型時位置不變，快速切換以最後一次選擇為準，隱藏時停止動畫且仍可從衣櫃換裝。也驗證了成品內素材損毀時會嘗試本機素材。
- 兩套造型均已用程式驗證四段待機動作皆可播到，連續輪播不重複，且 Interact 可中斷 Sleep 並回到待機。舊素材仍可循環播放 Idle。
- 素材檢查核對兩套造型各五段動畫，共 1,668 張影格。缺少清單指定的影格時，素材檢查會失敗。
