using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NLog;
using OverTranslate.Models;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so these names collide
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace OverTranslate.Views.Settings;

/// <summary>
/// 詞彙表 (#228): the glossaries the two AI services translate names with — a list of them on the
/// left, the one being edited on the right. Drawn over the shell like the other settings cards,
/// opened from 管理 on the glossary row of the settings page.
/// </summary>
/// <remarks>
/// <para>Every change is saved as it is made, as on the page behind it. Each one replaces
/// <see cref="AppSettings.Glossary"/> with an edited copy rather than editing it in place — see
/// <see cref="GlossarySettings"/> — so a translation reading it on another thread never sees a list
/// change under it.</para>
///
/// <para>The 譯文 column's language is picked on its own heading: it describes what is written in
/// that column, so it reads neither as the work's language nor as the language translations go
/// into. The line above the table says what it does.</para>
/// </remarks>
public partial class GlossaryOverlay : UserControl
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));

    /// <summary>The examples the add row and the paste box show, by the language the 譯文 are written in.</summary>
    /// <remarks>
    /// A source in a language other than the target's, the second pair a place: what someone adding
    /// a game's names types. Only for the languages the interface is in; any other shows none rather
    /// than an example in the wrong language.
    /// </remarks>
    private static readonly Dictionary<string, (string Source, string Target)[]> Examples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZH-HANT"] = [("アリサ", "艾莉莎"), ("ミラの森", "蜜菈森林")],
        ["ZH-HANS"] = [("アリサ", "艾莉莎"), ("ミラの森", "蜜菈森林")],
        ["EN-US"] = [("アリサ", "Alisa"), ("ミラの森", "Mira Forest")],
        ["KO"] = [("アリサ", "아리사"), ("ミラの森", "미라의 숲")],
        ["JA"] = [("Alice", "アリス"), ("Mira Forest", "ミラの森")],
        ["RU"] = [("アリサ", "Алиса"), ("ミラの森", "Лес Миры")],
    };

    /// <summary>True while the controls are being filled, so filling them writes nothing back.</summary>
    private bool _loading;

    /// <summary>The <see cref="GlossaryList.Id"/> being edited; empty when there are none.</summary>
    private string _editingId = "";

    /// <summary>Raised once the card has closed, for the settings page to show what changed.</summary>
    public event EventHandler? Closed;

    public GlossaryOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Card.MaxHeight = Math.Max(360, ActualHeight - 48);
    }

    // ── Open / close ─────────────────────────────────────────────────────────

    public void Open()
    {
        var glossary = Settings.Glossary;
        _editingId = glossary.Active?.Id ?? glossary.Lists.FirstOrDefault()?.Id ?? "";
        PastePanel.Visibility = Visibility.Collapsed;
        ConfirmLayer.Visibility = Visibility.Collapsed;
        SearchBox.Text = "";
        NoticeText.SetResourceReference(TextBlock.TextProperty, "S.Settings.AutoSaveHint");
        NoticeText.ClearValue(TextBlock.ForegroundProperty);
        Render();

        Visibility = Visibility.Visible;
        LocalizationService.LanguageChanged += OnLanguageChanged;

        Card.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration };
        fade.Completed += (_, _) => ReleaseAnimations();
        BeginAnimation(OpacityProperty, fade);
        var grow = new DoubleAnimation
        {
            From = 0.96, To = 1, Duration = FadeDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        Focus();
    }

    public void Close()
    {
        if (Visibility != Visibility.Visible) return;
        LocalizationService.LanguageChanged -= OnLanguageChanged;

        var fade = new DoubleAnimation { From = 1, To = 0, Duration = FadeDuration };
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            ReleaseAnimations();
        };
        BeginAnimation(OpacityProperty, fade);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;
        Card.CacheMode = null;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    // Clicks inside a card must not bubble up to a scrim's dismiss handler.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ConfirmLayer.Visibility == Visibility.Visible) CloseConfirm();
            else Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Render();

    // ── State ────────────────────────────────────────────────────────────────

    private static AppSettings Settings => SettingsService.Instance.Current;

    private static GlossaryList? Find(string id) => Settings.Glossary.Lists.FirstOrDefault(list => list.Id == id);

    /// <summary>Saves <paramref name="edit"/> made to a copy of the glossaries, then shows the result.</summary>
    /// <param name="render">False while typing in a box, so the box being typed in is not rebuilt under the caret.</param>
    private void Persist(Action<GlossarySettings> edit, bool render = true)
    {
        if (_loading) return;
        var copy = Settings.Glossary.Clone();
        edit(copy);
        Settings.Glossary = GlossarySettings.Clean(copy);
        SettingsService.Instance.Save();
        if (render) Render();
    }

    private static string Unnamed(string name) => name.Length > 0 ? name : LocalizationService.Get("S.Glossary.Unnamed");

    // ── Rendering ────────────────────────────────────────────────────────────

    private sealed record ListRow(string Id, string Name, string CountText, string LanguageName, Visibility InUseVisibility);

    private void Render()
    {
        _loading = true;
        try
        {
            var glossary = Settings.Glossary;
            if (Find(_editingId) is null) _editingId = glossary.Lists.FirstOrDefault()?.Id ?? "";

            var rows = glossary.Lists.Select(list => new ListRow(
                list.Id,
                Unnamed(list.Name),
                LocalizationService.Format("S.Glossary.Count", list.Entries.Count),
                LanguageData.GetTargetDisplayName(list.TargetLanguage),
                list.Id == glossary.ActiveId ? Visibility.Visible : Visibility.Collapsed)).ToList();
            ListsBox.ItemsSource = rows;
            ListsBox.SelectedItem = rows.FirstOrDefault(row => row.Id == _editingId);
            NoListsText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NewBtn.IsEnabled = ImportBtn.IsEnabled = glossary.Lists.Count < GlossarySettings.MaxLists;

            var editing = Find(_editingId);
            Editor.IsEnabled = editing is not null;
            Editor.Opacity = editing is null ? 0.5 : 1;
            if (editing is null)
            {
                NameBox.Text = "";
                EntriesBox.ItemsSource = null;
                AppliesText.Text = "";
                CountText.Text = "";
                EmptyText.Visibility = Visibility.Collapsed;
                return;
            }

            if (!NameBox.IsKeyboardFocused) NameBox.Text = editing.Name;

            LanguageBox.ItemsSource = LanguageData.TargetLanguages;
            LanguageBox.SelectedValue = LanguageData.GetValidTargetCode(editing.TargetLanguage);
            LanguageBox.Items.Refresh();

            var language = LanguageData.GetTargetDisplayName(editing.TargetLanguage);
            AppliesText.Text = LocalizationService.Format("S.Glossary.Applies", language);
            CountText.Text = LocalizationService.Format("S.Glossary.EntryCount", editing.Entries.Count, GlossarySettings.MaxEntries);

            var example = Examples.TryGetValue(editing.TargetLanguage, out var pairs) ? pairs : null;
            AddSourceBox.Tag = example is null ? "" : LocalizationService.Format("S.Glossary.Example", example[0].Source);
            AddTargetBox.Tag = example is null ? "" : LocalizationService.Format("S.Glossary.Example", example[0].Target);
            PasteBox.Tag = example is null ? "" : string.Join("\n", example.Select(pair => pair.Source + "=" + pair.Target));

            RenderEntries(editing);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>One entry as the table shows it; <see cref="Index"/> is its place in the list.</summary>
    private sealed record EntryRow(int Index, string Source, string Target);

    private void RenderEntries(GlossaryList editing)
    {
        var query = SearchBox.Text.Trim();
        EntriesBox.ItemsSource = editing.Entries
            .Select((entry, index) => new EntryRow(index, entry.Source, entry.Target))
            .Where(row => query.Length == 0
                || row.Source.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Target.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        EmptyText.Visibility = editing.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── The list ─────────────────────────────────────────────────────────────

    private void ListsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ListsBox.SelectedItem is not ListRow row || row.Id == _editingId) return;
        _editingId = row.Id;
        SearchBox.Text = "";
        PastePanel.Visibility = Visibility.Collapsed;
        Render();
    }

    private void NewBtn_Click(object sender, RoutedEventArgs e)
    {
        var id = GlossarySettings.NewId();
        _editingId = id;
        Persist(glossary =>
        {
            glossary.Lists.Add(new GlossaryList
            {
                Id = id,
                Name = LocalizationService.Get("S.Glossary.DefaultName"),
                TargetLanguage = LanguageData.GetValidTargetCode(Settings.TargetLanguage),
            });
            // A glossary is made to be used: the new one is the one in use.
            glossary.ActiveId = id;
        });
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        });
    }

    private void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = LocalizationService.Get("S.Glossary.ImportFilter"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        string text;
        try
        {
            text = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "Glossary import could not read the file");
            Notice(LocalizationService.Format("S.Glossary.ImportFailed", Path.GetFileName(dialog.FileName)));
            return;
        }

        var entries = GlossarySettings.CleanEntries(GlossaryText.Entries(GlossaryText.Parse(text)));
        if (entries.Count == 0)
        {
            Notice(LocalizationService.Format("S.Glossary.ImportNothing", Path.GetFileName(dialog.FileName)));
            return;
        }

        var id = GlossarySettings.NewId();
        _editingId = id;
        Persist(glossary => glossary.Lists.Add(new GlossaryList
        {
            Id = id,
            Name = Path.GetFileNameWithoutExtension(dialog.FileName),
            TargetLanguage = GlossaryText.TargetOf(text) ?? LanguageData.GetValidTargetCode(Settings.TargetLanguage),
            Entries = entries,
        }));
        // Counts only: what a glossary holds is the user's.
        Log.Info("Glossary imported: {Count} entries", entries.Count);
        Notice(LocalizationService.Format("S.Glossary.Imported", Path.GetFileName(dialog.FileName), entries.Count), warning: false);
    }

    /// <summary>A line in place of 設定變更後會自動儲存 until the card is opened again.</summary>
    private void Notice(string text, bool warning = true)
    {
        NoticeText.Text = text;
        if (warning) NoticeText.SetResourceReference(TextBlock.ForegroundProperty, "AppWarning");
        else NoticeText.ClearValue(TextBlock.ForegroundProperty);
    }

    // ── The glossary being edited ────────────────────────────────────────────

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var id = _editingId;
        var name = NameBox.Text;
        Persist(glossary =>
        {
            if (glossary.Lists.FirstOrDefault(list => list.Id == id) is { } list) list.Name = name;
        }, render: false);
        RefreshListRows();
    }

    /// <summary>The list on the left again, without touching the box being typed in.</summary>
    private void RefreshListRows()
    {
        _loading = true;
        try
        {
            var glossary = Settings.Glossary;
            var rows = glossary.Lists.Select(list => new ListRow(
                list.Id, Unnamed(list.Name),
                LocalizationService.Format("S.Glossary.Count", list.Entries.Count),
                LanguageData.GetTargetDisplayName(list.TargetLanguage),
                list.Id == glossary.ActiveId ? Visibility.Visible : Visibility.Collapsed)).ToList();
            ListsBox.ItemsSource = rows;
            ListsBox.SelectedItem = rows.FirstOrDefault(row => row.Id == _editingId);
        }
        finally
        {
            _loading = false;
        }
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedValue is not string code) return;
        var id = _editingId;
        Persist(glossary =>
        {
            if (glossary.Lists.FirstOrDefault(list => list.Id == id) is { } list) list.TargetLanguage = code;
        });
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || Find(_editingId) is not { } editing) return;
        RenderEntries(editing);
    }

    private void ExportBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Find(_editingId) is not { } editing) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = LocalizationService.Get("S.Glossary.ExportFilter"),
            DefaultExt = ".tsv",
            FileName = SafeFileName(Unnamed(editing.Name)),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, GlossaryText.Export(editing), GlossaryText.ExportEncoding);
            Notice(LocalizationService.Format("S.Glossary.Exported", Path.GetFileName(dialog.FileName)), warning: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "Glossary export could not write the file");
            Notice(LocalizationService.Format("S.Glossary.ExportFailed", Path.GetFileName(dialog.FileName)));
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    // ── Entries ──────────────────────────────────────────────────────────────

    private void AddSourceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddTargetBox.Focus();
        e.Handled = true;
    }

    /// <summary>Enter on the add row's 譯文: the entry goes first in the table and the row empties for the next.</summary>
    private void AddTargetBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        var source = AddSourceBox.Text.Trim();
        var target = AddTargetBox.Text.Trim();
        if (source.Length == 0)
        {
            AddSourceBox.Focus();
            return;
        }
        if (target.Length == 0) return;

        var id = _editingId;
        Persist(glossary =>
        {
            if (glossary.Lists.FirstOrDefault(list => list.Id == id) is not { } list) return;
            var same = list.Entries.FindIndex(entry => entry.Source == source);
            if (same >= 0) list.Entries[same].Target = target;
            else list.Entries.Insert(0, new GlossaryEntry { Source = source, Target = target });
        });
        AddSourceBox.Text = "";
        AddTargetBox.Text = "";
        AddSourceBox.Focus();
    }

    /// <summary>An entry's box written back when it is left: blank goes back to what it was.</summary>
    private void Cell_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitCell((TextBox)sender);

    private void Cell_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.Key == Key.Enter)
        {
            CommitCell(box);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && box.DataContext is EntryRow row)
        {
            box.Text = (string)box.Tag == "0" ? row.Source : row.Target;
            e.Handled = true;
        }
    }

    private void CommitCell(TextBox box)
    {
        if (_loading || box.DataContext is not EntryRow row) return;
        var isSource = (string)box.Tag == "0";
        var text = box.Text.Trim();
        var was = isSource ? row.Source : row.Target;
        if (text == was) return;
        if (text.Length == 0)
        {
            box.Text = was;
            return;
        }

        var id = _editingId;
        // After the focus has moved on, so the table is rebuilt once the next box has the caret.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Persist(glossary =>
        {
            if (glossary.Lists.FirstOrDefault(list => list.Id == id) is not { } list || row.Index >= list.Entries.Count) return;
            var entry = list.Entries[row.Index];
            if (isSource) entry.Source = text;
            else entry.Target = text;
        }));
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EntryRow row) return;
        var id = _editingId;
        Persist(glossary =>
        {
            if (glossary.Lists.FirstOrDefault(list => list.Id == id) is { } list && row.Index < list.Entries.Count)
                list.Entries.RemoveAt(row.Index);
        });
    }

    // ── 貼上多筆 ─────────────────────────────────────────────────────────────

    private void PasteBtn_Click(object sender, RoutedEventArgs e)
    {
        var open = PastePanel.Visibility != Visibility.Visible;
        PastePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (!open) return;
        RenderPaste();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PasteBox.Focus());
    }

    private void PasteCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        PastePanel.Visibility = Visibility.Collapsed;
        PasteBox.Text = "";
    }

    private void PasteBox_TextChanged(object sender, TextChangedEventArgs e) => RenderPaste();

    /// <summary>One line of the preview under the paste box.</summary>
    private sealed record PasteRow(
        int Number, string Source, string Target, string Replaces, int SourceSpan,
        Visibility TargetVisibility, Brush SourceBrush, Brush NumberBrush, Brush RowBackground);

    /// <summary>What the paste box is read as, line by line, and how many of its lines can be added.</summary>
    private void RenderPaste()
    {
        var lines = GlossaryText.Parse(PasteBox.Text);
        var existing = Find(_editingId)?.Entries ?? [];
        var error = (Brush)FindResource("AppError");
        var primary = (Brush)FindResource("AppTextPrimary");
        var secondary = (Brush)FindResource("AppTextSecondary");
        var errorRow = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1F, 0xE8, 0x11, 0x23));
        errorRow.Freeze();

        PasteLines.ItemsSource = lines.Select(line =>
        {
            if (!line.IsEntry)
                return new PasteRow(line.Number,
                    line.Text + "　— " + LocalizationService.Get(line.Problem switch
                    {
                        GlossaryLineProblem.NoSeparator => "S.Glossary.ProblemNoSeparator",
                        GlossaryLineProblem.Empty => "S.Glossary.ProblemEmpty",
                        _ => "S.Glossary.ProblemTooLong",
                    }),
                    "", "", 2, Visibility.Collapsed, error, error, errorRow);

            var old = existing.FirstOrDefault(entry => entry.Source == line.Source);
            var replaces = old is not null && old.Target != line.Target
                ? "  " + LocalizationService.Format("S.Glossary.Replaces", old.Target)
                : "";
            return new PasteRow(line.Number, line.Source, line.Target, replaces, 1,
                Visibility.Visible, primary, secondary, Brushes.Transparent);
        }).ToList();

        var ok = lines.Count(line => line.IsEntry);
        var bad = lines.Count - ok;
        PastePreview.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PasteAddBtn.IsEnabled = ok > 0;

        PasteSummary.Inlines.Clear();
        if (lines.Count == 0) return;
        PasteSummary.Inlines.Add(new System.Windows.Documents.Run(LocalizationService.Format("S.Glossary.PasteCount", ok)));
        if (bad > 0)
        {
            PasteSummary.Inlines.Add(new System.Windows.Documents.Run(LocalizationService.Get("S.Glossary.PasteSeparator")));
            PasteSummary.Inlines.Add(new System.Windows.Documents.Run(LocalizationService.Format("S.Glossary.PasteBad", bad))
            {
                Foreground = error,
            });
        }
    }

    /// <summary>
    /// What could be read added to the glossary — a source it already has takes the pasted
    /// translation, as the preview said — and the box closed.
    /// </summary>
    private void PasteAddBtn_Click(object sender, RoutedEventArgs e)
    {
        var entries = GlossaryText.Entries(GlossaryText.Parse(PasteBox.Text));
        if (entries.Count == 0) return;

        var id = _editingId;
        Persist(glossary =>
        {
            if (glossary.Lists.FirstOrDefault(list => list.Id == id) is not { } list) return;
            foreach (var entry in entries)
            {
                var same = list.Entries.FindIndex(existing => existing.Source == entry.Source);
                if (same >= 0) list.Entries[same].Target = entry.Target;
                else list.Entries.Add(entry);
            }
        });
        Log.Info("Glossary paste: {Count} entries added", entries.Count);
        PasteBox.Text = "";
        PastePanel.Visibility = Visibility.Collapsed;
    }

    // ── 刪除詞彙表 ───────────────────────────────────────────────────────────

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Find(_editingId) is not { } editing) return;
        ConfirmMessage.Text = LocalizationService.Format("S.Glossary.DeleteConfirm", Unnamed(editing.Name));
        ConfirmLayer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ConfirmCancelBtn.Focus());
    }

    private void CloseConfirm()
    {
        ConfirmLayer.Visibility = Visibility.Collapsed;
        DeleteBtn.Focus();
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => CloseConfirm();

    private void ConfirmScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseConfirm();

    private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        var id = _editingId;
        ConfirmLayer.Visibility = Visibility.Collapsed;
        Persist(glossary =>
        {
            glossary.Lists.RemoveAll(list => list.Id == id);
            if (glossary.ActiveId == id) glossary.ActiveId = "";
        });
        Focus();
    }
}
