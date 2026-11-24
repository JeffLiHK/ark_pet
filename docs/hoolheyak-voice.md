# 霍爾海雅日語語音

在專案根目錄執行下列指令，再啟動桌面寵物。需要 PowerShell 7 和網路連線。

```powershell
pwsh -NoProfile -File scripts/ImportHoolheyakVoice.ps1
```

匯入工具依照語音編號配對 PRTS 的日語 MP3 和日文字幕。語音和字幕儲存在 Git 忽略的 `local-data/voices/霍爾海雅/ja`，由霍爾海雅的造型共用。重新執行可補齊缺少的音檔。獨立使用測試包時，可將同一個 `local-data` 資料夾放在執行檔旁。 [PRTS 語音記錄](https://prts.wiki/w/霍尔海雅/语音记录) · [日文遊戲語音表](https://github.com/Kengxxiao/ArknightsGameData_YoStar/blob/3efb96637b9c344d282118bc91f6f8101d7712d5/ja_JP/gamedata/excel/charword_table.json)

短按小人會播放原有動畫並隨機選取一條可用語音，日文字幕隨播放顯示。再次短按會結束上一句並開始新的一句。移動位置、換裝或隱藏時會清除正在播放的語音和字幕。素材缺失時，短按仍會播放動畫。
