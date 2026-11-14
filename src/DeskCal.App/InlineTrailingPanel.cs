using System;
using System.Windows;
using System.Windows.Controls;

namespace DeskCal.App;

/// <summary>
/// Horizontal panel that places trailing badges (e.g. repeat icon) immediately after the leading text,
/// trimming the text with ellipsis when space runs out while keeping the trailing badges visible.
/// </summary>
internal sealed class InlineTrailingPanel : Panel
{
    public double Gap { get; set; } = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0) return new Size(0, 0);

        double trailingWidth = 0;
        double maxHeight = 0;
        for (int i = 1; i < InternalChildren.Count; i++)
        {
            UIElement child = InternalChildren[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            trailingWidth += child.DesiredSize.Width + Gap;
            maxHeight = Math.Max(maxHeight, child.DesiredSize.Height);
        }

        double textAvailableWidth = double.IsPositiveInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - trailingWidth);

        UIElement textChild = InternalChildren[0];
        textChild.Measure(new Size(textAvailableWidth, availableSize.Height));
        maxHeight = Math.Max(maxHeight, textChild.DesiredSize.Height);

        double totalWidth = textChild.DesiredSize.Width + (InternalChildren.Count > 1 ? trailingWidth : 0);
        double width = double.IsPositiveInfinity(availableSize.Width)
            ? totalWidth
            : Math.Min(availableSize.Width, totalWidth);

        return new Size(width, maxHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0) return finalSize;

        double trailingWidth = 0;
        for (int i = 1; i < InternalChildren.Count; i++)
            trailingWidth += InternalChildren[i].DesiredSize.Width + Gap;

        UIElement textChild = InternalChildren[0];
        double textWidth = Math.Min(textChild.DesiredSize.Width, Math.Max(0, finalSize.Width - trailingWidth));
        textChild.Arrange(new Rect(0, 0, textWidth, finalSize.Height));

        double x = textWidth;
        for (int i = 1; i < InternalChildren.Count; i++)
        {
            UIElement child = InternalChildren[i];
            x += Gap;
            double childW = child.DesiredSize.Width;
            double childH = child.DesiredSize.Height;
            double y = Math.Max(0, (finalSize.Height - childH) / 2);
            child.Arrange(new Rect(x, y, childW, childH));
            x += childW;
        }

        return finalSize;
    }
}
