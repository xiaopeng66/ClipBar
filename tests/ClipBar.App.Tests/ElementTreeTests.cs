using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Documents;
using ClipBar.App.Platform;

namespace ClipBar.App.Tests;

/// <summary>
/// Mouse hit-testing reports <see cref="Run"/> as OriginalSource when the pointer is over clip
/// text. Run is a ContentElement, not a Visual: walking it with VisualTreeHelper used to throw
/// and swallow the click, so text clicks never pasted. These tests pin the parent walk.
/// </summary>
public class ElementTreeTests
{
    [Fact]
    public void ParentOfWalksFromARunToItsTextBlock()
    {
        RunOnSta(() =>
        {
            var run = new Run("clip text");
            var block = new TextBlock { Inlines = { run } };

            Assert.Same(block, ElementTree.ParentOf(run));
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
