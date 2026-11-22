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
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace OverTranslate.Views.Settings;

/// <summary>
/// 詞彙表 (#228): the glossaries the two AI services translate names with — a list of them in a
/// sidebar, the one being edited beside it. Drawn over the shell like the other settings cards,
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
///
/// <para>The card is a fixed height (<see cref="FitCard"/>): what it holds only scrolls the table.
/// With no glossary at all the sidebar and editor give way to a single invitation to make one; the
/// first version disabled an empty editor instead, which showed a row of dead fields over the grey
/// the stock ListBox fills itself with when disabled. 批次輸入 is a layer of its own over the card
/// rather than a panel opened inside it, which pushed the table off the card.</para>
/// </remarks>
public partial class GlossaryOverlay : UserControl
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(140));
    private static readonly Duration FreshDuration = new(TimeSpan.FromMilliseconds(1400));

    private const double CardWidth = 900;
    private const double CardMaxHeight = 900;
    private const double BatchWidth = 640;
    private const double BatchMaxHeight = 540;

    /// <summary>The examples the add row and the batch box show, by the language the 譯文 are written in.</summary>
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

    /// <summary>The glossary the confirmation is asking about — not necessarily the one being edited.</summary>
    private string _pendingDeleteId = "";

    /// <summary>The trash that opened the confirmation, for focus to return to when it is cancelled.</summary>
    private Button? _confirmOrigin;

    /// <summary>
    /// Set while a trash on the list is being pressed: the press lands on the row as well, and the
    /// row must not take it as a choice of that glossary.
    /// </summary>
    private bool _listDeletePressed;

    /// <summary>The source of the entry just added, lit up once the table has been rebuilt with it.</summary>
    private string? _freshSource;

    /// <summary>The row lit up for having just been added, cleared if the list scrolls under it.</summary>
    private Border? _flashing;

    /// <summary>Raised once the card has closed, for the settings page to show what changed.</summary>
    public event EventHandler? Closed;

    public GlossaryOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, _) => FitCard();
        // A recycled row container may be handed to another entry mid-fade; let the light go with it.
        EntriesBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.VerticalChange != 0) StopFlash();
        }));
    }

    // ── Open / close ─────────────────────────────────────────────────────────

    public void Open()
    {
        var glossary = Settings.Glossary;
        _editingId = glossary.Active?.Id ?? glossary.Lists.FirstOrDefault()?.Id ?? "";
        BatchLayer.Visibility = Visibility.Collapsed;
        ConfirmLayer.Visibility = Visibility.Collapsed;
        _loading = true;
        SearchBox.Text = "";
        AddSourceBox.Text = "";
        AddTargetBox.Text = "";
        _loading = false;
        SetAddNote(null);
        Notice(null);
        Render();

        Visibility = Visibility.Visible;
        FitCard();
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

    /// <summary>
    /// As <c>RealtimeProfileOverlay.FitCard</c>: <see cref="CardWidth"/> wide where the window
    /// allows, and nine tenths of its height up to <see cref="CardMaxHeight"/> whatever it holds, so
    /// switching glossaries, searching or an empty table never move the card. The batch layer keeps
    /// to a fixed height of its own for the same reason: a long paste scrolls its preview.
    /// </summary>
    private void FitCard()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        Card.Width = Math.Max(320, Math.Min(CardWidth, ActualWidth - 32));
        Card.MaxHeight = ActualHeight * 0.9;
        Card.Height = Math.Min(ActualHeight * 0.9, CardMaxHeight);
        BatchCard.Width = Math.Max(320, Math.Min(BatchWidth, ActualWidth - 64));
        BatchCard.Height = Math.Min(ActualHeight * 0.9, Math.Max(360, Math.Min(BatchMaxHeight, ActualHeight * 0.9 - 40)));
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
            else if (BatchLayer.Visibility == Visibility.Visible) CloseBatch();
            else Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Render();
        if (BatchLayer.Visibility == Visibility.Visible) RenderBatchHeader();
        RenderPaste();
    }

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

    private sealed record ListRow(string Id, string Name, string Detail, Visibility InUseVisibility, string DeleteName);

    private static List<ListRow> BuildRows(GlossarySettings glossary) => glossary.Lists.Select(list =>
    {
        var name = Unnamed(list.Name);
        return new ListRow(
            list.Id,
            name,
            LocalizationService.Format("S.Glossary.Count", list.Entries.Count) + " · " + LanguageData.GetTargetDisplayName(list.TargetLanguage),
            list.Id == glossary.ActiveId ? Visibility.Visible : Visibility.Collapsed,
            LocalizationService.Format("S.Glossary.DeleteNamed", name));
    }).ToList();

    private void Render()
    {
        _loading = true;
        try
        {
            var glossary = Settings.Glossary;
            if (Find(_editingId) is null) _editingId = glossary.Lists.FirstOrDefault()?.Id ?? "";

            var none = glossary.Lists.Count == 0;
            SplitView.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Visibility = none ? Visibility.Visible : Visibility.Collapsed;

            var rows = BuildRows(glossary);
            ListsBox.ItemsSource = rows;
            ListsBox.SelectedItem = rows.FirstOrDefault(row => row.Id == _editingId);
            NewBtn.IsEnabled = ImportBtn.IsEnabled = glossary.Lists.Count < GlossarySettings.MaxLists;

            if (Find(_editingId) is not { } editing)
            {
                NameBox.Text = "";
                EntriesBox.ItemsSource = null;
                return;
            }

            if (!NameBox.IsKeyboardFocused) NameBox.Text = editing.Name;

            LanguageBox.ItemsSource = LanguageData.TargetLanguages;
            LanguageBox.SelectedValue = LanguageData.GetValidTargetCode(editing.TargetLanguage);
            LanguageBox.Items.Refresh();

            var language = LanguageData.GetTargetDisplayName(editing.TargetLanguage);
            AppliesText.Text = LocalizationService.Format("S.Glossary.Applies", language);

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
        FlashFresh();
    }

    /// <summary>One entry as the table shows it; <see cref="Index"/> is its place in the list.</summary>
    private sealed record EntryRow(int Index, string Source, string Target);

    private void RenderEntries(GlossaryList editing)
    {
        StopFlash();
        var query = SearchBox.Text.Trim();
        var rows = editing.Entries
            .Select((entry, index) => new EntryRow(index, entry.Source, entry.Target))
            .Where(row => query.Length == 0
                || row.Source.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Target.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        EntriesBox.ItemsSource = rows;

        if (editing.Entries.Count == 0)
        {
            BlankTitle.Text = LocalizationService.Get("S.Glossary.Empty");
            BlankBody.Text = LocalizationService.Get("S.Glossary.EmptyHint");
            BlankBody.Visibility = Visibility.Visible;
        }
        else if (rows.Count == 0)
        {
            BlankTitle.Text = LocalizationService.Format("S.Glossary.NoMatch", query);
            BlankBody.Visibility = Visibility.Collapsed;
        }
        BlankPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        CountText.Text = query.Length > 0
            ? LocalizationService.Format("S.Glossary.MatchCount", rows.Count)
            : LocalizationService.Format("S.Glossary.EntryCount", editing.Entries.Count, GlossarySettings.MaxEntries);
    }

    /// <summary>
    /// The entry just added lit with the accent tint, fading over <see cref="FreshDuration"/>, so the
    /// eye finds where it went. Only the background moves — a moving or scaling row of text would
    /// blur for as long as it moved.
    /// </summary>
    private void FlashFresh()
    {
        var source = _freshSource;
        _freshSource = null;
        if (source is null || EntriesBox.ItemsSource is not List<EntryRow> rows) return;
        var index = rows.FindIndex(row => row.Source == source);
        if (index < 0) return;
        EntriesBox.ScrollIntoView(rows[index]);
        if (!SystemParameters.ClientAreaAnimation) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (EntriesBox.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item
                || item.Template?.FindName("Bd", item) is not Border bd) return;
            StopFlash();
            var tint = ((SolidColorBrush)FindResource("AppAccentSubtleHover")).Color;
            var brush = new SolidColorBrush(tint);
            bd.Background = brush;
            _flashing = bd;
            var fade = new ColorAnimation(Color.FromArgb(0, tint.R, tint.G, tint.B), FreshDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            fade.Completed += (_, _) =>
            {
                if (_flashing == bd) StopFlash();
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, fade);
        });
    }

    private void StopFlash()
    {
        _flashing?.ClearValue(Border.BackgroundProperty);
        _flashing = null;
    }

    // ── The list ─────────────────────────────────────────────────────────────

    private void ListsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (_listDeletePressed)
        {
            // The trash's press reached the row: keep the glossary being edited as it was.
            _loading = true;
            ListsBox.SelectedItem = (ListsBox.ItemsSource as List<ListRow>)?.FirstOrDefault(row => row.Id == _editingId);
            _loading = false;
            return;
        }
        if (ListsBox.SelectedItem is not ListRow row || row.Id == _editingId) return;
        _editingId = row.Id;
        _loading = true;
        SearchBox.Text = "";
        _loading = false;
        SetAddNote(null);
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

    /// <summary>
    /// A line in place of 設定變更後會自動儲存 until the card is opened again — under the invitation
    /// while there is no glossary, since a failed first import leaves nothing else to show it on.
    /// </summary>
    private void Notice(string? text, bool warning = true)
    {
        if (text is null)
        {
            NoticeText.SetResourceReference(TextBlock.TextProperty, "S.Settings.AutoSaveHint");
            NoticeText.ClearValue(TextBlock.ForegroundProperty);
            EmptyNoticeText.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var block in new[] { NoticeText, EmptyNoticeText })
        {
            block.Text = text;
            if (warning) block.SetResourceReference(TextBlock.ForegroundProperty, "AppWarning");
            else block.ClearValue(TextBlock.ForegroundProperty);
        }
        EmptyNoticeText.Visibility = Visibility.Visible;
    }

    // ── 刪除詞彙表: the trash on each row of the list ─────────────────────────

    private void ListDelete_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _listDeletePressed = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _listDeletePressed = false);
    }

    /// <summary>Asks about this row's glossary, whichever one is being edited.</summary>
    private void ListDelete_Click(object sender, RoutedEventArgs e)
    {
        _listDeletePressed = false;
        if (((FrameworkElement)sender).DataContext is not ListRow row) return;
        _pendingDeleteId = row.Id;
        _confirmOrigin = sender as Button;
        ShowConfirm(ConfirmKind.DeleteGlossary,
            LocalizationService.Get("S.Glossary.DeleteTitle"),
            LocalizationService.Format("S.Glossary.DeleteConfirm", row.Name));
    }

    /// <summary>What the confirmation card is asking about.</summary>
    private enum ConfirmKind { DeleteGlossary, BatchPartial }

    private ConfirmKind _confirmKind;

    /// <summary>
    /// The confirmation card for <paramref name="kind"/>: deleting a glossary is the red 刪除 beside
    /// 取消; adding a batch some of whose lines cannot be read is the accent 加入 beside 返回修改,
    /// marked as a warning rather than as an error since nothing is lost either way.
    /// </summary>
    private void ShowConfirm(ConfirmKind kind, string title, string message)
    {
        _confirmKind = kind;
        var delete = kind == ConfirmKind.DeleteGlossary;
        ConfirmTitle.Text = title;
        ConfirmMessage.Text = message;
        ConfirmMark.SetResourceReference(TextBlock.ForegroundProperty, delete ? "AppError" : "AppWarning");
        ConfirmCancelBtn.SetResourceReference(ContentProperty, delete ? "S.Common.Cancel" : "S.Glossary.BatchConfirmBack");
        ConfirmActionBtn.SetResourceReference(ContentProperty, delete ? "S.Glossary.DeleteAction" : "S.Glossary.PasteAdd");
        ConfirmActionBtn.SetResourceReference(StyleProperty, delete ? "DangerButton" : "AccentButton");
        ConfirmActionBtn.Height = 34;
        System.Windows.Automation.AutomationProperties.SetAutomationId(ConfirmActionBtn,
            delete ? "GlossaryConfirmDeleteBtn" : "GlossaryConfirmAddBtn");
        ConfirmLayer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ConfirmCancelBtn.Focus());
    }

    /// <summary>Back to where the confirmation was asked from, nothing done; the batch box keeps its text.</summary>
    private void CloseConfirm()
    {
        ConfirmLayer.Visibility = Visibility.Collapsed;
        _pendingDeleteId = "";
        if (_confirmKind == ConfirmKind.BatchPartial && BatchLayer.Visibility == Visibility.Visible) PasteBox.Focus();
        else if (_confirmOrigin is { IsVisible: true } origin) origin.Focus();
        else Focus();
        _confirmOrigin = null;
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => CloseConfirm();

    private void ConfirmScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseConfirm();

    private void ConfirmAction_Click(object sender, RoutedEventArgs e)
    {
        ConfirmLayer.Visibility = Visibility.Collapsed;
        if (_confirmKind == ConfirmKind.BatchPartial) AddBatch();
        else DeleteGlossary();
    }

    /// <summary>
    /// The glossary deleted; when it was the one being edited, the one above it is edited instead
    /// (the one below when it was first), and with none left the card shows the invitation.
    /// </summary>
    private void DeleteGlossary()
    {
        var id = _pendingDeleteId;
        _pendingDeleteId = "";
        _confirmOrigin = null;
        if (id.Length == 0) return;

        if (id == _editingId)
        {
            var lists = Settings.Glossary.Lists;
            var at = lists.FindIndex(list => list.Id == id);
            var next = at > 0 ? lists[at - 1] : lists.ElementAtOrDefault(at + 1);
            _editingId = next?.Id ?? "";
            _loading = true;
            SearchBox.Text = "";
            _loading = false;
            SetAddNote(null);
        }
        Persist(glossary =>
        {
            glossary.Lists.RemoveAll(list => list.Id == id);
            if (glossary.ActiveId == id) glossary.ActiveId = "";
        });
        Focus();
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
            var rows = BuildRows(Settings.Glossary);
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

    /// <summary>The line under the add row: what the last add did, until the next keystroke.</summary>
    private void SetAddNote(string? text)
    {
        AddNoteText.Text = text ?? "";
        AddNoteText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AddBtn.IsEnabled = AddSourceBox.Text.Trim().Length > 0 && AddTargetBox.Text.Trim().Length > 0;
        if (!_loading && AddNoteText.Visibility == Visibility.Visible) SetAddNote(null);
    }

    private void AddSourceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddTargetBox.Focus();
        e.Handled = true;
    }

    /// <summary>Enter on the add row's 譯文 does what its Enter key cap does.</summary>
    private void AddTargetBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddEntry();
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e) => AddEntry();

    /// <summary>
    /// The add row's entry put first in the table — a source already there takes the new
    /// translation and moves up with it — and the row emptied for the next, the caret back in 原文.
    /// </summary>
    private void AddEntry()
    {
        var source = AddSourceBox.Text.Trim();
        var target = AddTargetBox.Text.Trim();
        if (source.Length == 0)
        {
            AddSourceBox.Focus();
            return;
        }
        if (target.Length == 0)
        {
            AddTargetBox.Focus();
            return;
        }

        var id = _editingId;
        var existed = Find(id)?.Entries.Any(entry => entry.Source == source) == true;
        // Full: a new entry put first would push the last one out of GlossarySettings.Clean unseen.
        if (!existed && Find(id)?.Entries.Count >= GlossarySettings.MaxEntries)
        {
            SetAddNote(LocalizationService.Format("S.Glossary.Full", GlossarySettings.MaxEntries));
            return;
        }
        _loading = true;
        SearchBox.Text = "";
        AddSourceBox.Text = "";
        AddTargetBox.Text = "";
        _loading = false;
        _freshSource = source;
        Persist(glossary =>
        {
            if (glossary.Lists.FirstOrDefault(list => list.Id == id) is not { } list) return;
            list.Entries.RemoveAll(entry => entry.Source == source);
            list.Entries.Insert(0, new GlossaryEntry { Source = source, Target = target });
        });
        SetAddNote(existed ? LocalizationService.Format("S.Glossary.Updated", source) : null);
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

    // ── 批次輸入 ─────────────────────────────────────────────────────────────

    private void PasteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Find(_editingId) is null) return;
        PasteBox.Text = "";
        RenderBatchHeader();
        RenderPaste();
        BatchLayer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PasteBox.Focus());
    }

    private void RenderBatchHeader() =>
        BatchTargetText.Text = LocalizationService.Format("S.Glossary.BatchTarget", Unnamed(Find(_editingId)?.Name ?? ""));

    /// <summary>The batch layer closed; what was typed in it is not kept.</summary>
    private void CloseBatch()
    {
        BatchLayer.Visibility = Visibility.Collapsed;
        PasteBox.Text = "";
        PasteBtn.Focus();
    }

    private void PasteCancelBtn_Click(object sender, RoutedEventArgs e) => CloseBatch();

    private void BatchScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseBatch();

    private void PasteBox_TextChanged(object sender, TextChangedEventArgs e) => RenderPaste();

    /// <summary>One line of the preview under the batch box.</summary>
    private sealed record PasteRow(
        int Number, string Source, string Target, string Replaces, int SourceSpan,
        Visibility TargetVisibility, Brush SourceBrush, Brush NumberBrush, Brush RowBackground);

    /// <summary>What the batch box is read as, line by line, and how many of its lines can be added.</summary>
    private void RenderPaste()
    {
        var lines = GlossaryText.Parse(PasteBox.Text);
        var existing = Find(_editingId)?.Entries ?? [];
        var error = (Brush)FindResource("AppError");
        var primary = (Brush)FindResource("AppTextPrimary");
        var secondary = (Brush)FindResource("AppTextSecondary");
        var errorRow = new SolidColorBrush(Color.FromArgb(0x1F, 0xE8, 0x11, 0x23));
        errorRow.Freeze();

        PasteLines.ItemsSource = lines.Select(line =>
        {
            // Red already says the line is left out; the reason is all it needs to add.
            if (!line.IsEntry)
                return new PasteRow(line.Number,
                    line.Text + "　" + LocalizationService.Get(line.Problem switch
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
        PasteEmptyText.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PasteAddBtn.IsEnabled = ok > 0;

        PasteSummary.Inlines.Clear();
        System.Windows.Automation.AutomationProperties.SetName(PasteSummary, "");
        if (lines.Count == 0) return;
        var count = LocalizationService.Format("S.Glossary.PasteCount", ok);
        PasteSummary.Inlines.Add(new System.Windows.Documents.Run(count));
        if (bad > 0)
        {
            var separator = LocalizationService.Get("S.Glossary.PasteSeparator");
            var badText = LocalizationService.Format("S.Glossary.PasteBad", bad);
            PasteSummary.Inlines.Add(new System.Windows.Documents.Run(separator));
            PasteSummary.Inlines.Add(new System.Windows.Documents.Run(badText) { Foreground = error });
            count += separator + badText;
        }
        // Runs set from code are not read by UI Automation; the name carries the same text.
        System.Windows.Automation.AutomationProperties.SetName(PasteSummary, count);
    }

    /// <summary>
    /// What could be read put first in the glossary in the order it was typed — a source it already
    /// has takes the new translation and moves up with it, as the preview said — and the layer closed.
    /// </summary>
    /// <remarks>
    /// When some lines cannot be read, asks first: they are left out, and the way back to fix them
    /// keeps the box as it is. Every line readable, it adds straight away.
    /// </remarks>
    private void PasteAddBtn_Click(object sender, RoutedEventArgs e)
    {
        var lines = GlossaryText.Parse(PasteBox.Text);
        var bad = lines.Count(line => !line.IsEntry);
        var ok = GlossarySettings.CleanEntries(GlossaryText.Entries(lines)).Count;
        if (ok == 0) return;
        if (bad == 0)
        {
            AddBatch();
            return;
        }
        ShowConfirm(ConfirmKind.BatchPartial,
            LocalizationService.Get("S.Glossary.BatchConfirmTitle"),
            LocalizationService.Format("S.Glossary.BatchConfirmBody", bad, ok));
    }

    private void AddBatch()
    {
        var entries = GlossarySettings.CleanEntries(GlossaryText.Entries(GlossaryText.Parse(PasteBox.Text)));
        if (entries.Count == 0 || Find(_editingId) is not { } editing) return;

        // Only as many new sources as there is room for: put first, any more would push the oldest
        // entries out of GlossarySettings.Clean unseen. A source already there always fits.
        var have = editing.Entries.Select(entry => entry.Source).ToHashSet();
        var room = GlossarySettings.MaxEntries - editing.Entries.Count;
        var kept = new List<GlossaryEntry>();
        foreach (var entry in entries)
        {
            if (have.Contains(entry.Source)) kept.Add(entry);
            else if (room > 0)
            {
                kept.Add(entry);
                room--;
            }
        }
        var left = entries.Count - kept.Count;
        entries = kept;
        if (entries.Count == 0)
        {
            CloseBatch();
            SetAddNote(LocalizationService.Format("S.Glossary.Full", GlossarySettings.MaxEntries));
            return;
        }

        var id = _editingId;
        _loading = true;
        SearchBox.Text = "";
        _loading = false;
        _freshSource = entries[0].Source;
        Persist(glossary =>
        {
            if (glossary.Lists.FirstOrDefault(list => list.Id == id) is not { } list) return;
            var sources = entries.Select(entry => entry.Source).ToHashSet();
            list.Entries.RemoveAll(existing => sources.Contains(existing.Source));
            list.Entries.InsertRange(0, entries);
        });
        Log.Info("Glossary batch: {Count} entries added", entries.Count);
        BatchLayer.Visibility = Visibility.Collapsed;
        PasteBox.Text = "";
        var note = LocalizationService.Format("S.Glossary.Added", entries.Count);
        if (left > 0) note += LocalizationService.Get("S.Glossary.PasteSeparator") + LocalizationService.Format("S.Glossary.Full", GlossarySettings.MaxEntries);
        SetAddNote(note);
        AddSourceBox.Focus();
    }
}
