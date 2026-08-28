using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.Views;

/// <summary>
/// The home dashboard board view (设计 §7 磁贴看板): renders the tile grid of a
/// <see cref="DashboardViewModel"/> into a <see cref="TilePanel"/> and provides the edit toolbar
/// (编辑磁贴 / 保存 / 取消 / 恢复默认). In edit mode every tile shows its editor chrome — reorder
/// (↑/↓), resize (−列 +列 −行 +行) and content editors (text, command target, page, status kind) — and
/// the board is rebuilt after each change (the board VM owns the state; the view is stateless).
/// Status/clock tiles are data-bound, so their live values update without a rebuild.
/// </summary>
public partial class DashboardView : UserControl
{
    private DashboardViewModel? _viewModel;
    private DispatcherTimer? _clockTimer;

    /// <summary>The command targets a button tile can be configured to (plus 无 for a plain tile).</summary>
    private static readonly string[] CommandTargetNames =
        Enum.GetNames<CommandTarget>();

    public DashboardView()
    {
        InitializeComponent();
        EditTileButton.Click += (_, _) => { _viewModel?.EditCommand.Execute(null); Rebuild(); };
        SaveTilesButton.Click += (_, _) => { _viewModel?.SaveTilesCommand.Execute(null); Rebuild(); };
        CancelEditButton.Click += (_, _) => { _viewModel?.CancelEditCommand.Execute(null); Rebuild(); };
        ResetTilesButton.Click += (_, _) => { _viewModel?.ResetDefaultsCommand.Execute(null); Rebuild(); };
    }

    /// <summary>Binds the board and rebuilds the grid.</summary>
    public void Apply(DashboardViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
        Rebuild();
        StartClockTimer();
    }

    private void Rebuild()
    {
        TileHost.Children.Clear();
        if (_viewModel is null)
        {
            return;
        }

        var editing = _viewModel.IsEditing;
        EditTileButton.Content = editing ? "编辑中…" : "编辑磁贴";
        SaveTilesButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        CancelEditButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ResetTilesButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        BoardStatusText.Text = _viewModel.StatusText;

        foreach (var tile in _viewModel.Tiles)
        {
            TileHost.Children.Add(BuildTile(tile));
        }
    }

    private UIElement BuildTile(TileViewModel tile)
    {
        var content = tile.Kind switch
        {
            UiTileKind.Button => BuildButtonTile(tile),
            UiTileKind.Navigate => BuildNavigateTile(tile),
            UiTileKind.Status => BuildStatusTile(tile),
            UiTileKind.Clock => BuildClockTile(tile),
            UiTileKind.Text => BuildTextTile(tile),
            _ => new Border(),
        };

        var cell = new Border
        {
            Background = ParseColor(tile.Color) ?? Safe<Brush>("ConfigUiTileBrush"),
            BorderBrush = Safe<Brush>("ConfigUiGridLineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content,
            Padding = new Thickness(8),
        };
        TilePanel.SetTileCols(cell, tile.Cols);
        TilePanel.SetTileRows(cell, tile.Rows);

        if (_viewModel is { IsEditing: true })
        {
            cell.BorderThickness = new Thickness(2);
            cell.BorderBrush = Safe<Brush>("ConfigUiAccentBrush");
            ((Border)cell.Child).Margin = new Thickness(0, 24, 0, 0);
            var stacked = new StackPanel();
            stacked.Children.Add(BuildTileEditor(tile));
            var inner = (UIElement)cell.Child;
            stacked.Children.Add(inner);
            cell.Child = stacked;
        }

        return cell;
    }

    // --- Tile bodies --------------------------------------------------------------------------------

    private UIElement BuildButtonTile(TileViewModel tile)
    {
        var button = new Button
        {
            Content = tile.Text,
            Command = tile.ClickCommand,
            Style = Safe<Style>("ConfigUiModuleButtonStyle"),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        return button;
    }

    private UIElement BuildNavigateTile(TileViewModel tile)
    {
        var border = new Border
        {
            Background = Safe<Brush>("ConfigUiButtonBrush"),
            CornerRadius = new CornerRadius(6),
            Child = new Button
            {
                Content = "→ " + tile.Text,
                Command = tile.ClickCommand,
                Foreground = Safe<Brush>("ConfigUiTextBrush"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            },
        };
        return border;
    }

    private UIElement BuildStatusTile(TileViewModel tile)
    {
        var label = new TextBlock
        {
            Text = tile.Text,
            Foreground = Safe<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var value = new TextBlock
        {
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = Safe<Brush>("ConfigUiTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        value.SetBinding(TextBlock.TextProperty, new Binding(nameof(TileViewModel.Value)) { Source = tile });
        var panel = new DockPanel();
        DockPanel.SetDock(label, Dock.Top);
        panel.Children.Add(label);
        panel.Children.Add(value);
        return panel;
    }

    private UIElement BuildClockTile(TileViewModel tile)
    {
        var label = new TextBlock
        {
            Text = tile.Text,
            Foreground = Safe<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var value = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Safe<Brush>("ConfigUiAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        value.SetBinding(TextBlock.TextProperty, new Binding(nameof(TileViewModel.Value)) { Source = tile });
        var panel = new DockPanel();
        DockPanel.SetDock(label, Dock.Top);
        panel.Children.Add(label);
        panel.Children.Add(value);
        return panel;
    }

    private UIElement BuildTextTile(TileViewModel tile)
    {
        return new TextBlock
        {
            Text = tile.Text,
            Foreground = Safe<Brush>("ConfigUiTextBrush"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // --- Edit chrome (visible only in edit mode) ---------------------------------------------------

    private UIElement BuildTileEditor(TileViewModel tile)
    {
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        };
        strip.Children.Add(MiniButton("↑", () => { _viewModel!.MoveUp(tile); Rebuild(); }));
        strip.Children.Add(MiniButton("↓", () => { _viewModel!.MoveDown(tile); Rebuild(); }));
        strip.Children.Add(MiniButton("−列", () => { _viewModel!.Resize(tile, -1, 0); Rebuild(); }));
        strip.Children.Add(MiniButton("+列", () => { _viewModel!.Resize(tile, +1, 0); Rebuild(); }));
        strip.Children.Add(MiniButton("−行", () => { _viewModel!.Resize(tile, 0, -1); Rebuild(); }));
        strip.Children.Add(MiniButton("+行", () => { _viewModel!.Resize(tile, 0, +1); Rebuild(); }));

        if (tile.Kind is UiTileKind.Button)
        {
            var text = new TextBox { Text = tile.Text, Style = Safe<Style>("ConfigUiInputStyle"), MinWidth = 90, FontSize = 12 };
            text.TextChanged += (_, _) => tile.Text = text.Text;
            var targets = new ComboBox
            {
                ItemsSource = CommandTargetNames,
                SelectedItem = tile.Target,
                Style = Safe<Style>("ConfigUiComboStyle"),
                MinWidth = 110,
            };
            targets.SelectionChanged += (_, _) =>
            {
                tile.Target = targets.SelectedItem as string;
                tile.CommitEdit();
            };
            var targetValue = new CheckBox
            {
                Content = "值=1",
                IsChecked = tile.TargetValue,
                Foreground = Safe<Brush>("ConfigUiTextBrush"),
                IsThreeState = false,
            };
            targetValue.Checked += (_, _) => { tile.TargetValue = true; tile.CommitEdit(); };
            targetValue.Unchecked += (_, _) => { tile.TargetValue = false; tile.CommitEdit(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(text);
            row.Children.Add(targets);
            row.Children.Add(targetValue);
            var wrap = new StackPanel { Orientation = Orientation.Vertical };
            wrap.Children.Add(strip);
            wrap.Children.Add(row);
            return wrap;
        }

        if (tile.Kind == UiTileKind.Navigate)
        {
            var text = new TextBox { Text = tile.Text, Style = Safe<Style>("ConfigUiInputStyle"), MinWidth = 90, FontSize = 12 };
            text.TextChanged += (_, _) => tile.Text = text.Text;
            var pages = new ComboBox
            {
                ItemsSource = _viewModel!.Pages,
                SelectedItem = tile.Page,
                Style = Safe<Style>("ConfigUiComboStyle"),
                MinWidth = 110,
                DisplayMemberPath = "Title",
            };
            pages.SelectionChanged += (_, _) =>
            {
                if (pages.SelectedItem is PageOption option)
                {
                    tile.Page = option.Id;
                    tile.CommitEdit();
                }
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(text);
            row.Children.Add(pages);
            var wrap = new StackPanel { Orientation = Orientation.Vertical };
            wrap.Children.Add(strip);
            wrap.Children.Add(row);
            return wrap;
        }

        if (tile.Kind == UiTileKind.Status)
        {
            var statuses = new ComboBox
            {
                ItemsSource = Enum.GetNames<UiTileStatus>(),
                SelectedItem = tile.StatusKind?.ToString(),
                Style = Safe<Style>("ConfigUiComboStyle"),
                MinWidth = 130,
            };
            statuses.SelectionChanged += (_, _) =>
            {
                if (Enum.TryParse<UiTileStatus>(statuses.SelectedItem as string, out var parsed))
                {
                    tile.StatusKind = parsed;
                    tile.CommitEdit();
                }
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = "状态：", Foreground = Safe<Brush>("ConfigUiMutedTextBrush"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(statuses);
            var wrap = new StackPanel { Orientation = Orientation.Vertical };
            wrap.Children.Add(strip);
            wrap.Children.Add(row);
            return wrap;
        }

        return strip;
    }

    private Button MiniButton(string content, System.Action onClick)
    {
        var button = new Button { Content = content, Style = Safe<Style>("ConfigUiMiniButtonStyle") };
        button.Click += (_, _) => onClick();
        return button;
    }

    private T Safe<T>(string key) where T : class
    {
        //  ConfigUiTheme 在 Application 级，UserControl/DashboardView 的本地字典可能找不到；
        //  TryFindResource 向上遍历逻辑树+应用资源，失败则给兜底实例，避免 KeyNotFoundException 闪退。
        if (TryFindResource(key) is T f) return f;
        if (Application.Current != null)
        {
            var r = Application.Current.TryFindResource(key);
            if (r is T f2) return f2;
        }
        if (typeof(T) == typeof(Brush)) return (T)(object)new SolidColorBrush(Color.FromRgb(0x2A, 0x60, 0x88));
        if (typeof(T) == typeof(Style)) return (T)(object)new Style();
        throw new KeyNotFoundException($"Resource '{key}' not found.");
    }

    private void StartClockTimer()
    {
        if (_viewModel is not { HasClock: true })
        {
            _clockTimer?.Stop();
            _clockTimer = null;
            return;
        }

        if (_clockTimer is not null)
        {
            return;
        }

        _viewModel.TickClock();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => _viewModel?.TickClock();
        _clockTimer.Start();
    }

    private static Brush? ParseColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        try
        {
            return (Brush)new BrushConverter().ConvertFromString(color)!;
        }
        catch
        {
            return null;
        }
    }
}
