using System.Windows;
using System.Windows.Controls;
using CashGear.Capture.App.Platform;

namespace CashGear.Capture.App.Tests.Platform;

public sealed class LiveTextThreadTests
{
    /// <summary>
    /// Windows on different threads (the tests have them) watching their text at once: WPF's descriptor is shared and
    /// its list of what it watches isn't safe across threads. Unguarded, this corrupted it and took the process down.
    /// </summary>
    [Fact]
    public void Live_text_on_several_threads_at_once_is_safe()
    {
        var failures = new List<Exception>();
        var threads = Enumerable.Range(0, 8).Select(_ =>
        {
            var thread = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < 400; i++)
                    {
                        var text = new TextBlock();
                        LiveText.SetIsLive(text, true);
                        text.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                        text.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                        LiveText.SetIsLive(text, false);
                    }
                }
                catch (Exception e)
                {
                    lock (failures)
                    {
                        failures.Add(e);
                    }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            return thread;
        }).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join(TimeSpan.FromSeconds(60)).Should().BeTrue("a corrupted dictionary can also hang a thread"));
        failures.Should().BeEmpty();
    }
}
