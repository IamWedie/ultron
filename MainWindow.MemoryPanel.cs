// MainWindow.MemoryPanel.cs - memory panel: list, search, edit, add
// Part of MainWindow. Fields and composition root live in MainWindow.xaml.cs.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Ultron.Services;
using Ultron.Services.Apps;
using Ultron.Services.Processes;
using Ultron.Services.Tools.Apps;
using Windows.System;
using Windows.UI;
using Microsoft.UI;
using Xaml = Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Buffers;
using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Ultron;

public sealed partial class MainWindow
{
    /* ===================== MEMORY PANEL ===================== */

    /// <summary>One-time import of the retired SQLite fact log.
    ///
    /// Long-term memory was previously written to two places at once: the model's
    /// <c>save_memory</c> tool appended to the <c>facts</c> table in memory.db,
    /// while the Memory panel and the system prompt used long_term.json. Neither
    /// could see the other's writes. long_term.json is now the only fact store, so
    /// the leftovers are parsed back into (category, key, value) triples and the
    /// old table is emptied. Conversations in memory.db are left alone.</summary>
    private void MigrateLegacyFacts()
    {
        if (_settings.MemoryFactsMigrated) return;
        try
        {
            // Read, import, and only then clear. Importing before deleting means a
            // corrupt or unreadable long_term.json cannot destroy the only copy of
            // these rows: the import throws, the catch below logs it, the marker
            // stays false, and the next launch retries against intact data.
            var rows = _memory.ReadLegacyFactsAsync().GetAwaiter().GetResult();
            if (rows.Count > 0)
            {
                var parsed = rows.Select(ParseLegacyFact).Where(f => f is not null).Select(f => f!).ToList();
                var skipped = rows.Count - parsed.Count;
                _facts.Import(parsed);
                // Unparseable rows are preserved by archiving them under a
                // reserved key rather than silently dropped, then the table is
                // cleared. Worst case the user sees a slightly odd entry in the
                // Notes category instead of losing text outright.
                if (skipped > 0)
                {
                    var archive = string.Join("\n", rows.Where(r => ParseLegacyFact(r) is null));
                    _facts.Upsert("notes", "_legacy_unparsed", archive);
                    Dbg($"Memory migration: archived {skipped} unparseable row(s) under notes/_legacy_unparsed");
                }
                _memory.ClearLegacyFactsAsync().GetAwaiter().GetResult();
                Dbg($"Memory migration: imported {parsed.Count} facts into long_term.json");
            }
            _settings.MemoryFactsMigrated = true;
            _settings.Save();
        }
        catch (Exception ex)
        {
            // Never let a failed migration block startup, and never mark it done,
            // so the next launch tries again.
            AppLog.Write("MainWindow", $"Legacy fact migration failed: {ex.Message}", AppLog.Level.Warn);
        }
    }

    /// <summary>Recover structure from a legacy "[category] key: value" row.
    /// Returns null when the row has no recognisable shape.</summary>
    private static MemoryFact? ParseLegacyFact(string row)
    {
        row = row.Trim();
        if (row.Length == 0) return null;
        var category = "notes";
        var body = row;
        if (row.StartsWith('['))
        {
            var close = row.IndexOf(']');
            if (close > 1)
            {
                category = row[1..close].Trim();
                body = row[(close + 1)..].TrimStart();
            }
        }
        var colon = body.IndexOf(':');
        var key = colon > 0 ? body[..colon].Trim() : body.Trim();
        var value = colon > 0 ? body[(colon + 1)..].Trim() : "";
        if (key.Length == 0) return null;
        if (key.Length > LongTermMemory.MaxKeyLength) key = key[..LongTermMemory.MaxKeyLength];
        if (category.Length == 0 || category.Length > LongTermMemory.MaxCategoryLength)
            category = category[..Math.Min(category.Length, LongTermMemory.MaxCategoryLength)];
        return new MemoryFact(category, key, value, "");
    }

    /// <summary>Rows currently shown, projected from the canonical store.
    /// All mutation goes through <c>_facts</c>; this list is only a view.</summary>
    private List<MemoryFact> _memCache = new();

    private void RefreshMemory()
    {
        _facts.Reload();
        _memCache = _facts.Entries();
        UpdateMemoryCorruptBanner();
        FilterMemory();
    }

    private void UpdateMemoryCorruptBanner()
    {
        if (MemoryCorruptHint is null) return;
        var corrupt = _facts.IsCorrupt;
        MemoryCorruptHint.Visibility = corrupt ? Visibility.Visible : Visibility.Collapsed;
        if (!corrupt) return;
        MemoryCorruptHint.Visibility = Visibility.Visible;
        MemoryCorruptHint.IsTapEnabled = true;
    }

    private void FilterMemory()
    {
        MemoryList.Items.Clear();
        var q = MemorySearch.Text?.Trim() ?? "";
        var shown = 0;
        string? lastCat = null;
        foreach (var m in _memCache)
        {
            if (q.Length > 0 &&
                !m.Category.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !m.Key.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !(m.Value ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            if (m.Category != lastCat)
            {
                MemoryList.Items.Add(new TextBlock
                {
                    Text = m.Category.ToUpperInvariant(),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 179, 3)),
                    Margin = new Thickness(0, 12, 0, 4),
                });
                lastCat = m.Category;
            }
            MemoryList.Items.Add(BuildMemoryRow(m));
            shown++;
        }
        MemoryCountText.Text = _memCache.Count == 0 ? "(empty)" : $"{shown}/{_memCache.Count}";
        MemoryHint.Visibility = _memCache.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Microsoft.UI.Xaml.Controls.StackPanel BuildMemoryRow(MemoryFact m)
    {
        var row = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 6) };
        var head = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var key = new TextBlock
        {
            Text = m.Key,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 237, 240, 245)),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var stamp = new TextBlock
        {
            Text = m.Updated ?? "",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 10,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var editBtn = new Button
        {
            Content = "EDIT",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = m,
        };
        editBtn.Click += async (_, _) => await EditMemoryAsync(m);
        var delBtn = new Button
        {
            Content = "DEL",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(51, 255, 46, 46)),
            Tag = m,
        };
        delBtn.Click += (_, _) => DeleteMemory(m);
        head.Children.Add(key);
        head.Children.Add(stamp);
        head.Children.Add(editBtn);
        head.Children.Add(delBtn);
        row.Children.Add(head);
        row.Children.Add(new TextBlock
        {
            Text = m.Value ?? "",
            FontSize = 12,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 201, 206, 214)),
            TextWrapping = TextWrapping.Wrap,
        });
        return row;
    }

    /// <summary>Wrap a store write so a refused write (corrupt file, or memory
    /// logging switched off) surfaces in the transcript instead of failing
    /// silently, which is how the old panel lost data.
    ///
    /// The MemoryLogging check has to live here because this is the single choke
    /// point for every panel mutation. The model-side handlers are gated in their
    /// own file, but the panel writes bypassed that check entirely, so a setting
    /// that claims "nothing is written while off" was not actually true.</summary>
    private bool TryWrite(Action action, string what)
    {
        if (!_settings.MemoryLogging)
        {
            AddMessage("system", $"Memory logging is off — {what} refused. Nothing is written while it is off.");
            return false;
        }
        try
        {
            action();
            RefreshMemory();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Write("MainWindow", $"{what} refused: {ex.Message}", AppLog.Level.Warn);
            AddMessage("system", "Memory not changed — " + ex.Message);
            return false;
        }
    }

    private async Task EditMemoryAsync(MemoryFact m)
    {
        var box = new TextBox
        {
            Text = m.Value ?? "",
            AcceptsReturn = true,
            MinHeight = 120,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var dlg = new ContentDialog
        {
            Title = $"Edit — [{m.Category}] {m.Key}",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            TryWrite(() => _facts.Upsert(m.Category, m.Key, box.Text), "edit memory");
    }

    private void DeleteMemory(MemoryFact m) =>
        TryWrite(() => _facts.Delete(m.Category, m.Key), "delete memory");

    private void MemorySearch_TextChanged(object sender, TextChangedEventArgs e) => FilterMemory();

    private void MemoryRefresh_Click(object sender, RoutedEventArgs e) => RefreshMemory();

    private async void MemoryAdd_Click(object sender, RoutedEventArgs e)
    {
        var catBox = new TextBox
        {
            PlaceholderText = "category (e.g. preferences)",
            Margin = new Thickness(0, 8, 0, 0),
            MaxLength = LongTermMemory.MaxCategoryLength,
        };
        var keyBox = new TextBox
        {
            PlaceholderText = "key (e.g. favorite_color)",
            Margin = new Thickness(0, 8, 0, 0),
            MaxLength = LongTermMemory.MaxKeyLength,
        };
        var valBox = new TextBox
        {
            PlaceholderText = "value",
            AcceptsReturn = true,
            MinHeight = 80,
            Margin = new Thickness(0, 8, 0, 0),
            MaxLength = LongTermMemory.MaxValueLength,
        };
        var panel = new Microsoft.UI.Xaml.Controls.StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Category (identity, preferences, projects, relationships, wishes, notes, or your own)",
            FontSize = 11,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(catBox);
        panel.Children.Add(keyBox);
        panel.Children.Add(valBox);
        var dlg = new ContentDialog
        {
            Title = "Add memory",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(catBox.Text) || string.IsNullOrWhiteSpace(keyBox.Text))
        {
            AddMessage("system", "Add memory needs both a category and a key.");
            return;
        }
        // Upsert, so re-adding an existing (category, key) replaces it instead of
        // silently creating a second copy.
        TryWrite(() => _facts.Upsert(catBox.Text, keyBox.Text, valBox.Text), "add memory");
    }

    private async void MemoryClear_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "Delete all memory?",
            Content = new TextBlock
            {
                Text = "This permanently removes every stored fact. Conversation history in memory.db is not affected.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete everything",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        TryWrite(_facts.Clear, "clear memory");
    }

    /// <summary>Only path that ever replaces an unparseable store, and only after
    /// the user confirms. The old file is kept alongside as <c>.corrupt-&lt;ts&gt;</c>.</summary>
    private async void MemoryDiscardCorrupt_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "Memory file unreadable",
            Content = new TextBlock
            {
                Text = $"{_facts.FilePath} could not be parsed, so ULTRON has not written to it. " +
                       "Discard it to start a fresh, empty memory? The unreadable file is kept as a backup.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Discard and reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            _facts.DiscardAndReset();
            AddMessage("system", "Memory store reset. The previous file was kept as a .corrupt backup.");
            RefreshMemory();
        }
        catch (Exception ex)
        {
            AppLog.Write("MainWindow", "Discard memory failed: " + ex.Message, AppLog.Level.Error);
            AddMessage("system", "Could not reset memory: " + ex.Message);
        }
    }
}
