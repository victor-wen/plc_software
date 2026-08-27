using System.Windows;
using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// A flowing tile grid for the dashboard board (设计 §7 磁贴看板): child tiles occupy a rectangular
/// number of grid columns × rows (attached <see cref="TileColsProperty"/>/<see cref="TileRowsProperty"/>)
/// and are laid out left-to-right / top-to-bottom in row-major order. Tiles that do not fit in the
/// remaining columns wrap to the next row (no gap filling).
/// </summary>
public sealed class TilePanel : Panel
{
    /// <summary>The tile width in grid columns (default 2).</summary>
    public static readonly DependencyProperty TileColsProperty = DependencyProperty.RegisterAttached(
        "TileCols", typeof(int), typeof(TilePanel), new PropertyMetadata(2));

    /// <summary>The tile height in grid rows (default 2).</summary>
    public static readonly DependencyProperty TileRowsProperty = DependencyProperty.RegisterAttached(
        "TileRows", typeof(int), typeof(TilePanel), new PropertyMetadata(2));

    public static int GetTileCols(DependencyObject obj) => (int)obj.GetValue(TileColsProperty);

    public static void SetTileCols(DependencyObject obj, int value) => obj.SetValue(TileColsProperty, value);

    public static int GetTileRows(DependencyObject obj) => (int)obj.GetValue(TileRowsProperty);

    public static void SetTileRows(DependencyObject obj, int value) => obj.SetValue(TileRowsProperty, value);

    /// <summary>The grid width in columns. Defaults to 12 (each unit = width/12).</summary>
    public int Columns { get; set; } = 12;

    /// <summary>The height of one grid row in device-independent pixels. Defaults to 96.</summary>
    public double RowHeight { get; set; } = 96;

    /// <summary>The gap between tiles in pixels. Defaults to 8.</summary>
    public double Spacing { get; set; } = 8;

    /// <summary>Grid units occupied by the item.</summary>
    private static (int Cols, int Rows) CellOf(UIElement child)
    {
        var cols = Math.Clamp(GetTileCols(child), 1, 100);
        var rows = Math.Clamp(GetTileRows(child), 1, 100);
        return (cols, rows);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var unitWidth = Columns > 0 ? (availableSize.Width - Spacing) / Columns : 0;
        var cursorX = 0;
        var cursorY = 0;
        var maxY = 0;

        foreach (UIElement child in Children)
        {
            var (cols, rows) = CellOf(child);
            var childWidth = Math.Max(0, cols * unitWidth + (cols - 1) * Spacing);
            var childHeight = Math.Max(0, rows * RowHeight + (rows - 1) * Spacing);
            child.Measure(new Size(childWidth, childHeight));

            if (cursorX > 0 && cursorX + cols > Columns)
            {
                cursorX = 0;
                cursorY = maxY + 1;
            }

            maxY = Math.Max(maxY, cursorY + rows);
            cursorX += cols;
        }

        var height = maxY > 0 ? maxY * RowHeight + (maxY - 1) * Spacing : 0;
        return new Size(availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var unitWidth = Columns > 0 ? (finalSize.Width - Spacing) / Columns : 0;
        var cursorX = 0;
        var cursorY = 0;
        var maxY = 0;

        foreach (UIElement child in Children)
        {
            var (cols, rows) = CellOf(child);
            var childWidth = Math.Max(0, cols * unitWidth + (cols - 1) * Spacing);
            var childHeight = Math.Max(0, rows * RowHeight + (rows - 1) * Spacing);

            if (cursorX > 0 && cursorX + cols > Columns)
            {
                cursorX = 0;
                cursorY = maxY;
            }

            var x = cursorX * (unitWidth + Spacing);
            var y = cursorY * (RowHeight + Spacing);
            child.Arrange(new Rect(x, y, childWidth, childHeight));

            maxY = Math.Max(maxY, cursorY + rows);
            cursorX += cols;
        }

        return finalSize;
    }
}
