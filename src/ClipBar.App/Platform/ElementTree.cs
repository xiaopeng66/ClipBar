using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ClipBar.App.Platform;

/// <summary>
/// Parent lookup that works for both halves of WPF's object model. Mouse events report their
/// <c>OriginalSource</c> as whatever was hit — often a <see cref="Run"/>, which is a
/// <see cref="FrameworkContentElement"/> and not a <see cref="Visual"/>. Passing it to
/// <c>VisualTreeHelper.GetParent</c> throws, so hit-test walks must branch first.
/// </summary>
public static class ElementTree
{
    /// <summary>Returns the logical parent of a ContentElement and the visual parent otherwise.</summary>
    public static DependencyObject? ParentOf(DependencyObject node) => node switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(node),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(node),
    };
}
