# Art Pet 桌面寵物

Art Pet 啟動後，霍爾海雅會在主螢幕工作列附近待機。短按她會播放基建互動，按住可以拖曳，右鍵可打開衣櫃並換上「歷世流風」。程式離線執行，免登入。

## 啟動與操作

直接開啟本機測試包的 `artifacts/ArtPet-win-x64/ArtPet.Windows.exe`。這個自包含 Windows x64 資料夾已有兩套造型及執行環境。

從原始碼執行時，在裝有 .NET 10 SDK `10.0.4xx` 的 Windows 從專案根目錄輸入以下指令。開發版優先使用 `%LOCALAPPDATA%\ArtPet\assets\hoolheyak\desktop-60` 的素材。舊版 `desktop` 資料夾仍可載入，素材缺失時會顯示原創的「小芽」。

```powershell
dotnet run --project src/ArtPet.Windows/ArtPet.Windows.csproj -c Release
```

- 畫布預設為 160 DIP，在主螢幕工作區右下角，離右邊及底部各 16 DIP，並置頂。
- 短按角色會播放一次互動，連點會沿用當前反應。按住可拖曳，右鍵選單可打開衣櫃、切換置頂或隱藏角色。
- 在右鍵選單的「設定」可選 75%、100%、125% 縮放及 30、60 FPS。預設為 100% 和 30 FPS。兩項會立即套用並保存到 `%LOCALAPPDATA%\ArtPet\settings.json`。
- 從工作列通知區的 **Art Pet** 圖示也可開衣櫃、顯示、隱藏、切換置頂、移回主螢幕右下角或結束。找不到圖示時，展開通知區的隱藏圖示。雙擊圖示會顯示角色。
- 造型、位置、置頂及顯示狀態在當次執行期間有效。再次啟動會套用預設造型和桌面位置。同一 Windows 登入工作階段只會執行一個實例。

## 製作本機測試包

兩套基建模型都使用 Spine 3.8.99，取 `Relax` 和 `Interact` 片段匯出為共用腳底錨點的 256 × 256 透明 PNG，每秒 60 格。30 FPS 模式每兩格取一格，兩種模式的動作長度相同。實際畫面更新受素材幀率、螢幕更新率和系統負載影響。預設造型取自 PRTS，「歷世流風」取自 Ark-Models。角色素材保存在 Git 儲存庫之外。含素材的測試包放在 Git 忽略的 `artifacts` 下。 [PRTS 模型索引](https://prts.wiki/w/霍尔海雅/spine) · [Ark-Models](https://github.com/isHarryh/Ark-Models)

```powershell
dotnet publish src/ArtPet.Windows/ArtPet.Windows.csproj -c Release -r win-x64 --self-contained true -p:BundleHoolheyak=true -o artifacts/ArtPet-win-x64
```

## 驗收條件

1. 霍爾海雅以預設造型出現在主螢幕工作區右下角並持續待機。原本正在使用的視窗保有前景，工作列沒有角色的視窗按鈕。
2. 點透明區域會操作下方視窗。短按角色會播放互動並回到待機，反應中快速連點只播一次。按住角色再拖曳，角色會停在新位置並維持原本的前景視窗。
3. 衣櫃的兩套造型可在原地切換，顯示目前使用中的造型。載入失敗會保留原造型。隱藏時停止動畫，通知區可在目前位置顯示並恢復待機，也可直接移回主螢幕右下角。
4. 通知區的「結束程式」移除視窗、圖示並結束程序。重複啟動只保留原有視窗和圖示。
5. 右鍵「設定」可切換縮放和幀率，勾選狀態反映目前設定。重啟會恢復兩項偏好。

## 視窗與滑鼠

桌面視窗沿用 C#／.NET 10 與 WPF，以 `WindowStyle=None`、`AllowsTransparency=true` 和透明底建立分層視窗。Windows 讓零 Alpha 像素的滑鼠事件穿透。`ShowActivated=false` 和 `WM_MOUSEACTIVATE` 回傳 `MA_NOACTIVATE`，讓角色接收點擊而保留目前的前景視窗。置頂交給 WPF `Topmost`。 [Win32 分層視窗](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows) · [滑鼠啟用訊息](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate)

主鍵按下後才啟動短時間的計時器，追蹤實體按鍵與游標。位移超過 Windows 的拖曳門檻後，用 `SetWindowPos` 在實體像素座標移動並保留前景。短按放開時再用 `WindowFromPoint` 確認命中角色。Per-Monitor V2 用於 DPI 切換，位置依螢幕工作區限制。 [程式架構](docs/architecture.md#桌面互動與動畫)

## 後續里程碑

1. **設定與復原。** 保存相對工作區的錨點、螢幕、置頂和造型，實測混合 DPI、拔除螢幕和解析度變更。若需整隻角色滑鼠穿透，可一起加入通知區解除入口。
2. **效能及相容性。** 以 `win-x64` 自包含測試包量測正式素材在 Windows 11 和 Windows 10 21H2 LTSC x64 的互動、動畫和資源使用，再評估安裝程式。 [發佈與效能門檻](docs/architecture.md#效能設計與發佈門檻) · [.NET 的 Windows 支援版本](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions)

養成和工作提醒保留為後續產品方向。第一版以安靜陪伴為主。

## Windows 驗證紀錄

- 在 Windows 11 Pro、125% 顯示比例下啟動自包含 EXE，確認預設造型待機、透明區實際點擊穿透、短按播放互動、拖曳、右鍵開衣櫃、原地切換兩套造型及退出。
- 實際從右鍵設定切換到 125% 和 60 FPS，重啟後恢復選擇，再從選單切回 30 FPS。程式內測試確認兩個幀率選項有不同的畫面更新頻率，拖曳後縮放時會保持角色的腳底錨點。
- 程式內測試確認衣櫃切換造型時位置不變，快速切換以最後一次選擇為準，隱藏時停止動畫且仍可從衣櫃換裝。也驗證了成品內素材損毀時會嘗試本機素材。
- 發佈檢查核對兩套造型共 720 張影格。缺少清單指定的影格時，素材檢查會失敗。
