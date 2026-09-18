---
name: git-release-overtranslate
description: "Write OverTranslate GitHub release notes from the commits between two tags, for end users rather than developers. Two modes: A drafts the Traditional Chinese body for review, B renders the agreed body into the five-language release template as a file. Examples: \"整理 2.2.1-beta.1 到 2.2.1-beta.2 的更新內容\", \"幫我寫這個版本的發布說明\", \"內容確定了，產出最終版\""
---

# OverTranslate 發布說明

從兩個 tag 之間的 commit 產出 GitHub Release 的內文。分兩趟走：先只寫繁體中文正文給使用者過目，改到定案，再一次產出五語系完整版。

路徑都以 repo 根目錄為準。

## 判斷現在是 A 還是 B

| 使用者說的話 | 模式 |
|---|---|
| 「整理 X 到 Y 的內容」「幫我寫發布說明」——第一次提到這個版本 | **A** |
| 「就這樣」「沒問題了」「產出最終版」——正文已經討論過 | **B** |

同一個版本的 B 一定發生在 A 之後。使用者若直接要求最終版而正文還沒定案，先跑 A 讓他們看過。

## 模式 A：繁體中文正文

### 1. 取材

```bash
python .codex/skills/git-release-overtranslate/collect.py 2.2.1-beta.1 2.2.1-beta.2
```

印出三段：合併進來的 PR 分支、使用者看得到的 feat/fix commit、以及濾掉的數量。實測 190 個 commit 會收斂到 117 個：18 個是合併，另外 55 個是重構、測試、文件與內部量測工具。

合併分支那段是功能的天然分組——`feat/capture-toolbar-annotate` 底下二十幾個 commit 在發布說明裡就是「標記工具」一段。先看分支，再回頭用 commit 補細節。

### 2. 篩掉不該出現的東西

**發布說明描述的是這個版本裝起來長什麼樣，不是這段期間發生過什麼事。** commit 是過程的紀錄，兩者不是同一件事。以下三種東西 `collect.py` 濾不掉，只能靠你查：

**（a）做了但沒開放的功能。** 開發到一半決定先不給使用者看的東西，程式碼還在、commit 也還在，但介面上沒有。寫進發布說明就是宣傳一個打不開的功能。

`2.2.1-beta.2` 的實例：工具列的「一般／介面」框選類型切換，開發過程有十幾個 commit，但實際出貨的版本裡：

```bash
git show <to-tag>:src/OverTranslate/Services/Ocr/CaptureLayoutPolicy.cs | grep IsModeSelectionAvailable
#   public static bool IsModeSelectionAvailable => false;
```

一個寫死的開關把整組控制項收掉了。**每一條要寫進 `◆` 的新功能，都到目標 tag 的原始碼確認它真的看得到**，找這幾種形狀：

```bash
# 寫死的可用性旗標
git show <to-tag>:<檔案> | grep -n "=> false"
# XAML 上直接收起來的控制項
git show <to-tag>:<檔案> | grep -n 'Visibility="Collapsed"'
```

看到 `Visibility="Collapsed"` 還要再往 code-behind 追一層——有些是啟動時才決定要不要顯示，那就要看決定它的那個值。

**（b）做了又拿掉、或改了又改的東西。** 只寫最後的樣子。圖示重畫三次是一條「更新應用程式圖示」；某個做法試了又換掉，就整條不寫。

**（c）使用者從沒遇過的 bug。** 修的是這個版本才寫出來的 bug，使用者手上那版根本沒有，寫出來只會讓人以為自己遇過。

### 3. 寫正文

**收件人是一般使用者，不是開發者。** 他們沒讀過這份程式碼，也不在乎裡面怎麼改的。每一條都要能回答「這對我有什麼差別」。

`example.md` 是 `2.2.1-beta.2` 的定稿，使用者親手改過，文案與排版都以它為準。**動筆前先讀它**，下面只是把它的規則講明白。

#### 章節怎麼分

按**功能區塊**分，不是按功能一個一段。同一個區塊同時有改善和新東西，就拆成兩段，改善在前：

```
## ◆ 新增多語系語言          ← 單一件事，直接命名
## ◆ 截圖翻譯：優化          ← 同區塊有兩面，「區塊：面向」
## ◆ 截圖翻譯：新功能
## ◆ 設定頁：OpenAI 提示詞    ← 設定裡的東西冠上「設定頁：」
## ◆ 快速翻譯
## ◇ 其他改善                ← 永遠最後一段
## ↓ 安裝 / 更新             ← 照抄不改
```

`◆` 段落之間空一行，條列一律 `- `，不用巢狀。

#### 句子怎麼寫

- **功能名稱用反引號，寫介面上真正的字**：`` `標記` ``、`` `複製文字 / 複製譯文` ``、`` `偵錯輔助` ``。名稱不確定就去 `Strings.zh-Hant.xaml` 查，不要自己翻譯。
- **新功能句型是「新增 `名稱` 功能：說明」**。說明講使用者能拿它做什麼，不講它怎麼實作。
- **補充條件放半形括號**：「可直接辨識原文文字並複製至剪貼簿，(不需要執行 `翻譯` 功能)」。
- **寫完整的句子，不要縮成關鍵詞**。一條一到兩句，把「有什麼差別」講完；「減少因文字被拆分而造成的語意斷句問題」比「改善斷句」有用。
- **修 bug 寫成「優化…可能發生的錯誤問題」**，不描述 bug 的症狀與成因。
- **內部名詞一律換掉或不寫**：門檻、分組（指演算法時）、疊圖、ROI、profile、intent、bypass、harness、契約測試。

| 不要這樣寫 | 要這樣寫 |
|---|---|
| 一般模式放寬段落行距門檻至 1.45 | （不寫；併進「自動合併分組」那條） |
| 放置層產生疊圖 intent，整組靠左上排版 | （不寫，使用者看不到這個概念） |
| 超過翻譯端點長度上限的文字改為分句送出 | 優化翻譯服務遇到字數上限時可能發生的錯誤問題 |
| 標記改用增量繪製與擦除遮罩，成本不再隨畫的量成長 | （不寫；效能是新功能的一部分，不另立一條） |
| 新增截圖翻譯文字複製功能 | 新增 `複製文字 / 複製譯文` 功能：可直接辨識原文文字並複製至剪貼簿，(不需要執行 `翻譯` 功能) |

#### 還要檢查的

- **同一件事只寫一次**。`偵錯輔助` 既是新功能又能寫成改善，挑一個地方放。
- **確認是不是新功能**。commit 寫「新增 X 的獨立設定」不代表 X 是新的。用 `git log --grep=<關鍵字> <from-tag>` 查起點之前有沒有——有就是改善，不要當新功能宣傳。
- **版本號跟著目標 tag 走**，不要自己編。

### 4. 交出去

正文直接印在對話裡讓使用者看，H1 用目標 tag，最後接上安裝章節：

```markdown
# OverTranslate 2.2.1-beta.2

（正文，形狀照 example.md）

## ↓ 安裝 / 更新
- **現有使用者**：支援自動更新，無需手動下載新版；若自動更新較慢，可重新下載新版手動更新（免安裝版本直接覆蓋原檔即可）
- **新使用者**：下載 `OverTranslate-win-Setup.exe` 進行安裝
- **免安裝版本**：下載 `OverTranslate-win-Portable.zip`，解壓後直接執行，同樣支援自動更新
```

安裝章節照抄不改。到這裡停下來，等使用者回應。

## 模式 B：五語系完整版

正文定案後，翻成另外四個語系，填進樣板，輸出成檔案。

### 1. 五份正文各寫一個檔

寫到暫存目錄，檔名固定：

```
body.en.md  body.zh-Hant.md  body.zh-Hans.md  body.ja.md  body.ko.md
```

各檔**只放正文**（`## ◆ …` 那幾段），不要 H1 標題，也不要安裝章節——那兩塊由樣板負責。

五份的章節數與每段條列數必須完全一致，`build.py` 會擋（以 `body.zh-Hant.md` 為基準）。

翻譯要讀起來像各語言的官方發布說明，不是翻譯腔。反引號包住的功能名稱換成各語系介面上的實際字串（去 `Strings.<語系>.xaml` 查），不要直譯。繁中→簡中不是換字而已：「介面」→「界面」、「工具列」→「工具栏」、「快捷鍵」→「快捷键」、「檔案」→「文件」。日韓文用敬體。

### 2. 組出完整版

```bash
python .codex/skills/git-release-overtranslate/build.py \
    --version 2.2.1-beta.2 \
    --bodies <上一步的目錄> \
    --out "C:/Users/asd88/AppData/Local/Temp/overtranslate-release-2.2.1-beta.2.md"
```

會印出檔案路徑。`--version` 就是目標 tag，五個語言區塊的 H1 都吃這個值。

輸出放系統 temp（`C:\Users\asd88\AppData\Local\Temp`），不是 session 的 scratchpad——使用者要自己打開這個檔，路徑要好找。

### 3. 交出去

**只回報檔案路徑與一句話摘要，不要把整份內容印在對話裡。** 那是一份要複製貼上的長文，印出來只是讓使用者滾過去兩次。

## Gotchas

- **commit 有的東西不代表使用者有**。做完又用旗標關掉的功能，在 `git log` 裡是十幾個 feat，在使用者手上不存在。`2.2.1-beta.2` 的「一般／介面」切換就是這樣——照著 commit 寫會宣傳一個打不開的功能，只有讀目標 tag 的原始碼才看得出來。
- **`collect.py` 濾掉的是類型不是內容**。它擋 `chore:`／`test:`／`docs:`／`refactor:` 開頭與提到內部工具的 commit，但 `fix:` 一律留著——即使那條 fix 是修一個從沒發布過的功能，使用者根本沒遇過那個 bug。判斷還是你的事。
- **合併分支數會比 PR 數多**。同一個 PR 分支若分兩次合併（例如 #151 與 #152 都是 `feat/ui-languages-zh-hans-ja-ko`），清單裡會出現兩列，是同一件事。
- **`build.py` 輸出 LF**。樣板檔本身是 CRLF（照 repo 慣例），但貼進 GitHub release body 的內容用 CRLF 會在程式碼區塊裡多出空行，所以組檔時轉成 LF。
- **樣板只有 `template.md` 一份**。要改安裝章節或加語言就改它，不要在對話裡重打——五段 CJK 樣板手抄一定會壞掉一個字，而且看起來完全正常。
- **這個 skill 有兩份**：`.claude/skills/git-release-overtranslate/` 與 `.codex/skills/git-release-overtranslate/`，內容相同，只有指令裡的路徑不同。**改了一份就要改另一份**——兩份都進版控，沒同步的那份會安靜地照舊行為做事。

## Troubleshooting

| 症狀 | 原因與處理 |
|---|---|
| `找不到 tag：<名稱>` | tag 名沒有 `v` 前綴（是 `2.2.1-beta.2` 不是 `v2.2.1-beta.2`）。錯誤訊息會列出現有的 tag。 |
| `缺少正文：…body.zh-Hans.md` | 五個語系少寫一個。檔名要完全相符，`zh-Hant`／`zh-Hans` 的大小寫也算。 |
| `正文還留著 TODO` | 樣板的佔位字沒換掉。 |
| `body.en.md 的結構與 body.zh-Hant.md 不一致` | 某個語系漏了一整段或漏了一條，訊息會印出兩邊的段數與每段條數，對一下就知道差在哪。 |
| `UnicodeDecodeError` 出現在 `collect.py` | 不會發生了，`run()` 已固定用 UTF-8 解 git 輸出。若你另外寫了 git 呼叫，記得比照辦理——Windows 的 Python 預設會挑到 cp950，中文 commit 訊息直接炸。 |
