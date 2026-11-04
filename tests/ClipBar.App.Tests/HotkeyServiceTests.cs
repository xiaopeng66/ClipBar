using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using ClipBar.App.Services;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.3 T-40: an occupied hotkey must be refused with a clear message, and a refused change
/// must leave the app with the combo it already had (F-07). The registration is global to the
/// desktop, so the tests claim a combo, have a second <see cref="HotkeyService"/> try to take it,
/// and prove ownership never moved. Everything runs on one STA thread because each service owns
/// an <c>HwndSource</c> message window.
/// </summary>
public class HotkeyServiceTests
{
    /// <summary>A combo no running app is expected to hold, so the test owns it deterministically.</summary>
    private const string FreeCombo = "Ctrl+Alt+Shift+F11";

    /// <summary>
    /// Runs <paramref name="work"/> on a dedicated STA thread whose dispatcher is pumping, which
    /// is what an <c>HwndSource</c> requires. The work runs *inside* the message loop: shutting
    /// the dispatcher down first and then calling <c>Dispatcher.Run</c> throws.
    /// </summary>
    private static void RunOnStaThread(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;

            dispatcher.InvokeAsync(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });

            Dispatcher.Run();
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

    [Fact]
    public void AnOccupiedComboIsRefusedWithAClearMessage()
    {
        RunOnStaThread(() =>
        {
            using var owner = new HotkeyService();
            using var challenger = new HotkeyService();

            Assert.True(owner.Register(FreeCombo, out var ownerError), ownerError);

            var registered = challenger.Register(FreeCombo, out var error);

            Assert.False(registered);
            Assert.Contains("占用", error);
        });
    }

    [Fact]
    public void ARefusedRegistrationDoesNotStealTheComboFromItsOwner()
    {
        RunOnStaThread(() =>
        {
            using var owner = new HotkeyService();
            using var challenger = new HotkeyService();

            Assert.True(owner.Register(FreeCombo, out var ownerError), ownerError);
            Assert.False(challenger.Register(FreeCombo, out _));

            // A third service must still find the combo taken: if the challenger had quietly
            // released it, this one would succeed.
            using var third = new HotkeyService();
            Assert.False(third.Register(FreeCombo, out _));
        });
    }

    [Fact]
    public void AFailedChangeRestoresThePreviousCombo()
    {
        RunOnStaThread(() =>
        {
            using var owner = new HotkeyService();
            using var service = new HotkeyService();

            Assert.True(owner.Register(FreeCombo, out var ownerError), ownerError);
            Assert.True(service.Register("Ctrl+Alt+Shift+F12", out var firstError), firstError);

            // Rejected: someone else holds this combo.
            Assert.False(service.Register(FreeCombo, out var changeError));
            Assert.Contains("占用", changeError);

            // The service must be back on its original combo, so a fresh claim of it fails.
            using var rival = new HotkeyService();
            Assert.False(rival.Register("Ctrl+Alt+Shift+F12", out _));
        });
    }

    [Fact]
    public void AnUnparsableComboIsRejectedWithoutTouchingTheLiveRegistration()
    {
        RunOnStaThread(() =>
        {
            using var service = new HotkeyService();
            Assert.True(service.Register(FreeCombo, out var error), error);

            // The old code called Unregister() before parsing, so a typo dropped the working
            // hotkey and never restored it.
            Assert.False(service.Register("Ctrl+Nonsense", out var parseError));
            Assert.Contains("按键", parseError);

            using var rival = new HotkeyService();
            Assert.False(rival.Register(FreeCombo, out _));
        });
    }

    [Fact]
    public void ReleasingTheServiceReleasesTheCombo()
    {
        RunOnStaThread(() =>
        {
            using (var first = new HotkeyService())
            {
                Assert.True(first.Register(FreeCombo, out var error), error);
            }

            using var second = new HotkeyService();
            Assert.True(second.Register(FreeCombo, out var secondError), secondError);
        });
    }

    [Theory]
    [InlineData("", "快捷键不能为空。")]
    [InlineData("   ", "快捷键不能为空。")]
    [InlineData("V", "快捷键至少需要一个修饰键和一个按键，例如 Win+Shift+V。")]
    [InlineData("Ctrl+Nonsense", "不认识的按键：Nonsense")]
    [InlineData("Ctrl+F99", "不认识的按键：F99")]
    public void TryParseExplainsWhatIsWrong(string combo, string expected)
    {
        Assert.False(HotkeyService.TryParse(combo, out _, out _, out var error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("Win+Shift+V")]
    [InlineData("win+shift+v")]
    [InlineData("  Ctrl + Alt + V  ")]
    [InlineData("Windows+P")]
    public void TryParseAcceptsTheDocumentedSpellings(string combo)
    {
        Assert.True(HotkeyService.TryParse(combo, out var modifiers, out var virtualKey, out var error), error);
        Assert.NotEqual(0u, modifiers);
        Assert.NotEqual(0u, virtualKey);
    }
}
