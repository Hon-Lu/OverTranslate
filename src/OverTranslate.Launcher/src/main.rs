//! OverTranslate 免安裝版的啟動器。
//!
//! 免安裝包的版面是 Velopack 決定的：更新器與 `current\` 放在根目錄，主程式在 `current\` 裡面。
//! 使用者解壓之後需要一個能直接點的入口，這顆就是。它只做一件事——把 `current\` 底下的主程式
//! 叫起來，然後自己結束。
//!
//! 為什麼不沿用 Velopack 出廠的 stub：那顆的版本資源是空的，打包時才被塞進主程式的整棵資源樹，
//! 所以它宣稱自己是 OverTranslate、卻是一顆打包當下才被改寫的未簽章二進位——那正是 Defender 的
//! 機器學習分類器（Wacatac.B!ml）盯上的形狀。這顆反過來做：身分在編譯期就寫死且誠實，編完之後
//! 不再被任何工具改寫，位元組跨版本完全不變。

#![windows_subsystem = "windows"]

use std::env;
use std::path::{Path, PathBuf};
use std::process::Command;

use windows_sys::core::PCWSTR;
use windows_sys::Win32::UI::WindowsAndMessaging::{MessageBoxW, MB_ICONERROR, MB_OK};

/// 主程式在 `current\` 底下的檔名。與 `vpk pack --mainExe` 一致。
const MAIN_EXE: &str = "OverTranslate.exe";

/// 主程式所在的子目錄。Velopack 固定用這個名字，更新時整個資料夾換掉，名字不變。
const CURRENT_DIR: &str = "current";

fn main() {
    let launcher = match env::current_exe() {
        Ok(path) => path,
        Err(err) => {
            show_error(&format!("無法取得啟動器自己的位置：{err}"));
            return;
        }
    };

    let root = match launcher.parent() {
        Some(dir) => dir.to_path_buf(),
        None => {
            show_error("啟動器不在任何資料夾裡，無法決定主程式的位置。");
            return;
        }
    };

    let work_dir = root.join(CURRENT_DIR);
    let target = work_dir.join(MAIN_EXE);

    // 整包被移動、或只把這顆單獨複製出去時會走到這裡。講清楚，不要讓使用者面對一個沒反應的圖示。
    if !target.is_file() {
        show_error(&format!(
            "找不到主程式：\n{}\n\n請確認這個啟動器與 {CURRENT_DIR} 資料夾在一起，\
             不要單獨把它複製到別的地方。",
            display(&target)
        ));
        return;
    }

    // 參數原樣轉交，讓檔案關聯、拖放之類的用法也能經過這顆。
    let args: Vec<_> = env::args_os().skip(1).collect();

    // 工作目錄設在 current\，與 Velopack 自己啟動主程式時一致。
    if let Err(err) = Command::new(&target).current_dir(&work_dir).args(args).spawn() {
        show_error(&format!("啟動主程式失敗：\n{}\n\n{err}", display(&target)));
    }
}

fn display(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

fn show_error(message: &str) {
    let text = to_wide(message);
    let caption = to_wide("OverTranslate");
    unsafe {
        MessageBoxW(
            std::ptr::null_mut(),
            text.as_ptr() as PCWSTR,
            caption.as_ptr() as PCWSTR,
            MB_OK | MB_ICONERROR,
        );
    }
}

fn to_wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

/// 讓 `PathBuf` 的 import 在沒有其他用途時也不會變成警告。
#[allow(dead_code)]
fn _unused(_: PathBuf) {}
