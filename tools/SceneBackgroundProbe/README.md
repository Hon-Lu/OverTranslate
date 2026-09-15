# 即時翻譯 CPU 背景修補驗證

此工具直接呼叫主程式的 `CpuTextMask`、`CpuHoleRepair`，用於檢查字形遮罩、描邊殘留、複雜背景和效能。只保留 CPU 流程；沒有 GPU 修補模型或歷史背景快取。

## 執行

在儲存庫根目錄執行：

```powershell
dotnet run --project tools/SceneBackgroundProbe/SceneBackgroundProbe.csproj -c Release -- --verify
dotnet run --project tools/SceneBackgroundProbe/SceneBackgroundProbe.csproj -c Release
```

`--verify` 不需外部圖片，檢查原／半解析度分流、分區邊界、遮罩外像素、空遮罩、取消與 1px 輸入。

完整比較需要本機 `.ai/test-images/chat-room/*.png` 和 `docs/images/originals` 的遊戲／影片示範圖片。缺少 OCR 座標時會先使用主程式 OCR 產生；沿用 `artifacts/inpaint-research/*-ocr.json` 與語言標記，只保存文字框，沒有乾淨背景。此批私人素材不包含在工具中；一般 CI 使用單元測試與 `--verify`。

輸出 `artifacts/cpu-adaptive-background/index.html`、各方法 PNG 和 `metrics.json`。可切換：

- 正式版字形遮罩＋自適應解析度修補。
- 相同遮罩＋半解析度，分辨遮罩與解析度選擇各自的影響。
- 舊研究遮罩＋半解析度基準。
- 舊版矩形修補，只供品質對照，與上一項不同。
- 文字本體、描邊遮罩及合成案例的乾淨底圖。

22 張聊天素材沒有乾淨真值；另在晶體、植物、動畫角色背景上合成明／暗描邊文字，共 6 組案例，乾淨背景僅用於評分，不提供給修補器。不要把合成 MAE 當作真實聊天背景還原率。

## 正式功能

`RealtimeCpuBackground` 每個視窗的一次更新只修補一張來源圖，所有翻譯區塊從這張結果裁切；文字取色仍用原圖。核心分開偵測文字本體與相反極性的描邊，最後沿字形擴張邊緣以涵蓋抗鋸齒外圈。以 96px 分區與 24px 周邊觀察範圍處理：薄洞使用原解析度 Navier–Stokes，厚洞共用一次半解析度運算，只寫回遮罩內。

更新目標為每 100ms 一輪，扣除本輪耗時再等待；超時就完成後讀取最新畫面，不排隊補跑。靜止畫面跳過修補；持續變動且超時的場景可能持續占用 CPU，沒有強制負載上限。首次視覺建構仍同步執行，後續更新在背景工作執行；取消可在分區間生效，單次 OpenCV 呼叫不能中途取消。

## 已知限制與量測

密集文字下的重複斜紋、眼睛、植物等細節仍可能模糊；低對比描邊與漏掉的 OCR 字仍可能殘留。文字遮罩是啟發式分割，可能把背景邊緣誤認為字。沒有已知像素可觀察的區域保留原值。

計時先暖機再測 7 次，包含遮罩、補洞及暫存釋放，不含 OCR、擷取、PNG I/O 或 UI；OpenCV 設定 2 執行緒。程序 working set 是取樣而非峰值，managed allocation 不包含原生配置。不能用 probe 耗時直接承諾端到端延遲。

2026-09-15，在相同額外 1px 字形擴張版本比較完整 runtime 與官方 Slim：28 組素材的 230 張既有 PNG 雜湊完全一致。兩輪 adaptive 中位耗時範圍為 4.0–87.9ms 與 4.3–91.7ms；各案例中位數總和差約 2.9%，非交錯受控效能測試，不能歸因於套件。這 230 張不含工具整併後新增的舊版矩形對照。

完整套件的 OpenCV 相關 DLL 合計 98,171,392 bytes（93.6MiB），目前直接引用 `OpenCvSharp4` 與 `OpenCvSharp4.runtime.win.slim` 4.13.0.20260627：包裝 DLL 1,003,008 bytes＋原生 DLL 55,547,904 bytes，合計 53.9MiB，減少約 42.4%。Debug、Release 和獨立 publish 都不含 FFmpeg 或 WPF adapter DLL。這是磁碟體積，不是記憶體降幅。

## 方法選擇與清理

參考 [OpenCV inpainting 官方說明](https://docs.opencv.org/4.x/df/d3d/tutorial_py_inpainting.html)，採用 Navier–Stokes CPU 實作。使用[官方 Slim 套件](https://www.nuget.org/packages/OpenCvSharp4.runtime.win.slim)，保留 imgproc 與 photo，無須自行維護原生編譯。

先前研究過 [MI-GAN](https://github.com/Picsart-AI-Research/MI-GAN) 和 [LaMa](https://github.com/advimman/lama)：MI-GAN 在大片聊天遮罩中生成不屬於場景的物件；LaMa 的部分重複紋理效果較好，但本機 CPU 約 3.2 秒，測試的 ONNX 匯出亦無法在 DirectML 正常初始化。兩者不符合目前輕量 CPU 方向，執行器與下載腳本已移除。

舊版歷史背景／紋理搜尋原型也已移除。舊矩形修補保留在主程式來源中供此工具及回歸測試比較；實際即時視窗已改走 CPU 字形修補。產圖、OCR 座標、模型與建置輸出皆不進版控。
