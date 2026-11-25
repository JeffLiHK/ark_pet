# 霍爾海雅語音與字幕

在 ArtPet 1.3.2 或以上的「設定 → 資源管理」按「更新全部模型」，首次安裝會取得日語配音及兩種字幕。日語與普通話可在「語音資源」獨立下載、更新或刪除。下載普通話後，可到「語音與字幕」切換使用。程式優先使用下載的配音，其次是舊版隨包配音和本機匯入。

如果要準備發布用素材或本機匯入，請在專案根目錄執行下列指令。需要 PowerShell 7 和網路連線。

```powershell
pwsh -NoProfile -File scripts/ImportHoolheyakVoice.ps1
```

匯入工具依語音編號配對日語、普通話配音以及日文、繁體中文字幕。資料儲存在 Git 忽略的 `local-data/voices/霍爾海雅`，由各造型共用。`ja` 包含日語音檔及日文字幕，`zh-CN` 包含普通話音檔，`zh-TW` 包含繁體中文字幕。曾匯入日語的使用者可以重新執行指令補齊新素材。獨立使用測試包時，可將同一個 `local-data` 資料夾放在執行檔旁。 [配音檔案來源](https://github.com/PseudoMon/arknights-audio/tree/d6de8b50e23bdfa5cf9d391f4ccb3798d7baf4db) · [日文語音表](https://github.com/Kengxxiao/ArknightsGameData_YoStar/blob/3efb96637b9c344d282118bc91f6f8101d7712d5/ja_JP/gamedata/excel/charword_table.json) · [繁體中文語音表](https://github.com/ArknightsAssets/ArknightsGamedata/blob/3fbae6508ce8b5c562f8c00d931a99cc0e647f26/tw/gamedata/excel/charword_table.json)

其他角色可使用 `local-data/voices/<角色 ID>/<語言代碼>/`。配音檔案使用 `cn_001.mp3` 這類名稱，字幕放在語言資料夾的 `subtitles.json`，以 `cn_001` 這類編號配對文字。

短按小人會播放互動動畫並隨機選取一條可用語音。預設使用日語配音與繁體中文字幕，可到「設定 → 語音與字幕」分別切換配音和字幕，或關閉字幕。再次短按會結束上一句並開始新的一句。移動位置、換裝或隱藏時會清除正在播放的語音和字幕。缺少字幕時仍會播放可用語音，缺少語音時短按仍會播放動畫。
