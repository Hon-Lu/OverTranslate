//! 版本資源、圖示與資訊清單都在**編譯期**就寫進去。
//!
//! 這是這顆啟動器與 Velopack stub 最關鍵的差別：stub 是編譯完成後才被打包工具塞進資源，
//! 「編譯後又被改寫的二進位」本身就是啟發式的警訊。這裡一次做完，之後沒有任何工具會再碰它，
//! 位元組跨版本完全不變。

fn main() {
    if !cfg!(target_os = "windows") {
        return;
    }

    let mut res = winresource::WindowsResource::new();

    // 誠實的身分：它是啟動器，不是主程式本身。stub 的問題之一就是冒用主程式的身分。
    res.set("CompanyName", "Hon.Lu");
    res.set("ProductName", "OverTranslate");
    res.set("FileDescription", "OverTranslate Launcher");
    res.set("InternalName", "OverTranslate.exe");
    res.set("OriginalFilename", "OverTranslate.exe");
    res.set("LegalCopyright", "Copyright (c) Hon.Lu");

    // 版號寫死，不跟著應用程式走——這顆的位元組要能跨版本保持不變，雜湊信譽才累積得起來。
    res.set("FileVersion", "1.0.0.0");
    res.set("ProductVersion", "1.0.0.0");
    res.set_version_info(winresource::VersionInfo::FILEVERSION, 1 << 48);
    res.set_version_info(winresource::VersionInfo::PRODUCTVERSION, 1 << 48);

    // 圖示直接引用主程式那一份，不另外複製一顆進來——兩邊要永遠是同一個圖。
    res.set_icon("../OverTranslate/icons/app.ico");
    res.set_manifest_file("launcher.manifest");

    if let Err(err) = res.compile() {
        panic!("無法編譯 Windows 資源：{err}");
    }
}
