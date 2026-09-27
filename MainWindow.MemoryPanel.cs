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
using QRCoder;

namespace Ultron;

public sealed partial class MainWindow
{
    /* ===================== MEMORY PANEL ===================== */

    private static string MemoryFilePath =>
        Path.Combine(AppSettings.DataDir(), "long_term.json");

    private static bool _memoryJsonCorrupt;

    private Dictionary<string, Dictionary<string, object?>> LoadMemoryJson()
    {
        try
        {
            if (File.Exists(MemoryFilePath))
            {
                var root = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object?>>>(File.ReadAllText(MemoryFilePath));
                if (root is not null)
                {
                    _memoryJsonCorrupt = false;
                    return root;
                }
            }
        }
        catch
        {
            _memoryJsonCorrupt = true;
        }
        return new();
    }

    private static readonly JsonSerializerOptions MemoryJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void SaveMemoryJson(Dictionary<string, Dictionary<string, object?>> root)
    {
        if (_memoryJsonCorrupt)
            throw new InvalidOperationException("Memory store is corrupt and was not overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(MemoryFilePath)!);
        var temporary = MemoryFilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(root, MemoryJsonOpts));
        File.Move(temporary, MemoryFilePath, true);
    }

    private string RecallFromMemoryJson(string query)
    {
        var root = LoadMemoryJson();
        var q = query ?? "";
        var results = new List<string>();
        foreach (var (cat, entries) in root)
        {
            if (entries is null) continue;
            foreach (var (key, val) in entries)
            {
                var value = val is Dictionary<string, object?> d && d.TryGetValue("value", out var v) ? v?.ToString() : val?.ToString();
                value ??= "";
                if (q.Length == 0 ||
                    cat.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    value.Contains(q, StringComparison.OrdinalIgnoreCase))
                    results.Add($"[{cat}] {key}: {value}");
            }
        }
        return results.Count > 0 ? string.Join("\n", results.Take(8)) : "Nothing found in memory.";
    }

    private sealed class MemEntry
    {
        public required string Category { get; init; }
        public required string Key { get; init; }
        public string? Value { get; set; }
        public string? Updated { get; init; }
    }

    private List<MemEntry> _memCache = new();

    private void RefreshMemory()
    {
        _memCache = LoadMemory();
        FilterMemory();
    }

    private List<MemEntry> LoadMemory()
    {
        var list = new List<MemEntry>();
        try
        {
            if (!File.Exists(MemoryFilePath)) return list;
            var root = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(File.ReadAllText(MemoryFilePath));
            if (root is null) return list;
            foreach (var (cat, entries) in root)
            {
                foreach (var (key, el) in entries)
                {
                    string? val = null, upd = null;
                    if (el.ValueKind == JsonValueKind.Object)
                    {
                        val = el.GetProperty("value").GetString();
                        upd = el.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    }
                    else if (el.ValueKind == JsonValueKind.String)
                    {
                        val = el.GetString();
                    }
                    list.Add(new MemEntry { Category = cat, Key = key, Value = val, Updated = upd });
                }
            }
            list.Sort((a, b) => string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase) == 0
                ? string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)
                : string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { AppLog.Write("MainWindow", $"LoadMemory failed: {ex.Message}", AppLog.Level.Error); }
        return list;
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

    private Microsoft.UI.Xaml.Controls.StackPanel BuildMemoryRow(MemEntry m)
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

    private void SaveMemory()
    {
        var root = new Dictionary<string, Dictionary<string, object>>();
        foreach (var m in _memCache)
        {
            if (!root.TryGetValue(m.Category, out var cat))
            {
                cat = new Dictionary<string, object>();
                root[m.Category] = cat;
            }
            cat[m.Key] = new Dictionary<string, object?>
            {
                ["value"] = m.Value ?? "",
                ["updated"] = m.Updated ?? DateTime.Now.ToString("yyyy-MM-dd"),
            };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MemoryFilePath)!);
            File.WriteAllText(MemoryFilePath, JsonSerializer.Serialize(root, MemoryJsonOpts));
        }
        catch (Exception ex) { AppLog.Write("MainWindow", $"SaveMemoryJson failed: {ex.Message}", AppLog.Level.Error); }
    }

    private async Task EditMemoryAsync(MemEntry m)
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
        {
            m.Value = box.Text;
            SaveMemory();
            FilterMemory();
        }
    }

    private void DeleteMemory(MemEntry m)
    {
        _memCache.RemoveAll(x => x.Category == m.Category && x.Key == m.Key);
        SaveMemory();
        FilterMemory();
    }

    private void MemorySearch_TextChanged(object sender, TextChangedEventArgs e) => FilterMemory();

    private void MemoryRefresh_Click(object sender, RoutedEventArgs e) => RefreshMemory();

    private async void MemoryAdd_Click(object sender, RoutedEventArgs e)
    {
        var catBox = new TextBox { PlaceholderText = "category (e.g. preferences)", Margin = new Thickness(0, 8, 0, 0) };
        var keyBox = new TextBox { PlaceholderText = "key (e.g. favorite_color)", Margin = new Thickness(0, 8, 0, 0) };
        var valBox = new TextBox { PlaceholderText = "value", AcceptsReturn = true, MinHeight = 80, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new Microsoft.UI.Xaml.Controls.StackPanel();
        panel.Children.Add(new TextBlock { Text = "Category (identity, preferences, projects, relationships, wishes, notes, ...)", FontSize = 11, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 155, 161, 171)) });
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
        if (await dlg.ShowAsync() == ContentDialogResult.Primary &&
            !string.IsNullOrWhiteSpace(catBox.Text) && !string.IsNullOrWhiteSpace(keyBox.Text))
        {
            _memCache.RemoveAll(x => x.Category == catBox.Text.Trim() && x.Key == keyBox.Text.Trim());
            _memCache.Add(new MemEntry { Category = catBox.Text.Trim(), Key = keyBox.Text.Trim(), Value = valBox.Text, Updated = DateTime.Now.ToString("yyyy-MM-dd") });
            SaveMemory();
            RefreshMemory();
        }
    }

    private void MemoryClear_Click(object sender, RoutedEventArgs e)
    {
        _memCache.Clear();
        SaveMemory();
        RefreshMemory();
    }

}
