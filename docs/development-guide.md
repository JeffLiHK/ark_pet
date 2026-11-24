# Git 與 Coding Style

## Git

- 在專案根目錄首次用 `git init -b main` 建立版本庫。以 `main` 為主要分支，功能與修正可用短分支。每個提交處理一件事，訊息寫明用途，例如 `feat: add wardrobe`。
- Git 追蹤原始碼、測試、專案設定與 `docs/`。用 `.gitignore` 排除 `bin/`、`obj/`、`.vs/`、發佈產物、本機設定與角色素材。
- Codex 在所屬版本庫以 `git add -- <path>` 暫存本次新增檔案，用 `git diff --cached --name-status` 核對。提交與推送依使用者指示。
- 修改程式後先建置並跑相關測試。需要程式碼審查時，預設使用 Bugbot。[建置與測試](architecture.md#開始建置)

## C# 與 WPF

- 使用 UTF-8、四個空格縮排。C# 大括號另起一行。型別、方法與公開成員用 `PascalCase`，參數與區域變數用 `camelCase`，私有欄位用 `_camelCase`。
- 規則與資料邏輯放在 `ArtPet.Core`，WPF 和 Win32 放在 `ArtPet.Windows`。XAML 負責版面與樣式，檔案讀取和影格解碼放到背景執行。
- 用清楚的名稱，優先沿用現有程式。每個方法聚焦一件事，註解交代像 DPI、焦點和執行緒這類容易誤解的原因。
