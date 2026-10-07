namespace OverTranslate.Models;

/// <summary>Language preferences used only by 替換翻譯.</summary>
/// <remarks>
/// 替換翻譯 — In-place translation in English — shipped as 快速翻譯 / Quick translate and was
/// renamed in 2.7.0 (#277), because in English, Japanese and Korean it differed from 取詞翻譯 by
/// half a word. Only the names on screen changed. The code keeps calling it QuickTranslate: these
/// property names are the keys in every user's settings file, and renaming them would drop
/// everyone's languages and shortcut back to the defaults for nothing the user can see.
/// </remarks>
public class QuickTranslateSettings
{
    public string SourceLanguage { get; set; } = LanguageData.DefaultSourceLanguage;
    public string TargetLanguage { get; set; } = "EN-US";
}
