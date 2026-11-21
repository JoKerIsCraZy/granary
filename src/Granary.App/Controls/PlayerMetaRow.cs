using Granary.Core.ViewModels.Player;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// The player's line under the title: the episode, the chapter, then the times and the end clock. The times and the end
/// clock always show whole; a narrow line cuts the chapter first and the episode after it (<see cref="MetaLineFit"/>).
/// The first child is the episode, the second the chapter, each with its dot; the others are the times, then the end clock
/// with its dot as one part. None reaches past the line: the end clock goes first, then the times are cut.
/// </summary>
internal sealed partial class PlayerMetaRow : Panel
{
    private double _episodeWidth;
    private double _chapterWidth;
    private double[] _otherWidths = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        double others = 0;
        double height = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (i > 1)
            {
                others += child.DesiredSize.Width;
            }
            height = Math.Max(height, child.DesiredSize.Height);
        }
        double episode = Children.Count > 0 ? Children[0].DesiredSize.Width : 0;
        double chapter = Children.Count > 1 ? Children[1].DesiredSize.Width : 0;
        // The times and the end clock stay inside the line too: the end clock goes first, then the times are cut.
        _otherWidths = MetaLineFit.FitOthers([.. Children.Skip(2).Select(c => c.DesiredSize.Width)], availableSize.Width);
        for (int i = 0; i < _otherWidths.Length; i++)
        {
            var child = Children[i + 2];
            if (_otherWidths[i] < child.DesiredSize.Width)
            {
                others -= child.DesiredSize.Width - _otherWidths[i];
                child.Measure(new Size(_otherWidths[i], availableSize.Height));
            }
        }
        (_episodeWidth, _chapterWidth) = MetaLineFit.Split(episode, chapter, others, availableSize.Width);
        // Measured again at the width they get, so a cut part ends in an ellipsis.
        if (_episodeWidth < episode)
        {
            Children[0].Measure(new Size(_episodeWidth, availableSize.Height));
        }
        if (_chapterWidth < chapter)
        {
            Children[1].Measure(new Size(_chapterWidth, availableSize.Height));
        }
        return new Size(Math.Min(_episodeWidth + _chapterWidth + others, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double left = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            double width = i switch
            {
                0 => _episodeWidth,
                1 => _chapterWidth,
                _ => i - 2 < _otherWidths.Length ? _otherWidths[i - 2] : 0,
            };
            child.Arrange(width > 0 ? new Rect(left, 0, width, finalSize.Height) : default);
            left += width;
        }
        return finalSize;
    }
}
