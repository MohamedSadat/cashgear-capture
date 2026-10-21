using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Interop.Com;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Engine.Objects;

/// <summary>
/// Finds the UI Automation elements of a window at a point, for Capture Object. The selection overlay covers the
/// screen, so UI Automation's own hit test would find the overlay: instead this starts at the window under the
/// point (from the snapshot taken before the overlay) and walks down, one level at a time, into the smallest child
/// that contains the point. Each level is one cross-process call (<c>FindAllBuildCache</c> with the rectangle, name
/// and type cached). It stops at controls whose insides aren't parts of their own (a button's label, an edit box's
/// text): those are what a click means.
/// <para>
/// UI Automation runs on a thread of its own in the multithreaded apartment (a call into an app that is busy or
/// hung blocks only that thread). Requests are served one at a time; one that hasn't started when a newer one
/// arrives is dropped (cancelled), since only where the cursor is now matters.
/// </para>
/// </summary>
public sealed partial class UiaObjectFinder : IObjectFinder, IDisposable
{
    private const int MaxDepth = 64;

    /// <summary>
    /// UI Automation control types whose children are only their own pieces: button, check box, combo box, edit,
    /// hyperlink, list item, menu item, progress bar, radio button, scroll bar, slider, spinner, tab item, tree item,
    /// data item, split button, header item.
    /// </summary>
    private static readonly HashSet<int> WholeControls = [50000, 50002, 50003, 50004, 50005, 50007, 50011, 50012, 50013, 50014, 50015, 50016, 50019, 50024, 50029, 50031, 50035];

    private readonly ILogger<UiaObjectFinder> _logger;
    private readonly BlockingCollection<Request> _requests = new(new ConcurrentQueue<Request>());
    private readonly Thread _thread;
    private Request? _pending;

    [Localizable(false)] // the thread's name, for debuggers
    public UiaObjectFinder(ILogger<UiaObjectFinder> logger)
    {
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "Object capture (UI Automation)" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public Task<IReadOnlyList<ScreenObject>> FindAtAsync(nint window, PixelPoint point, CancellationToken ct = default)
    {
        var request = new Request(window, point, ct);
        Interlocked.Exchange(ref _pending, request)?.Completion.TrySetCanceled(); // superseded before it started
        try
        {
            _requests.Add(request, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            request.Completion.TrySetResult([]); // disposed
        }

        return request.Completion.Task;
    }

    public void Dispose()
    {
        _requests.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        Session? session = null;
        try
        {
            session = new Session();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            LogUnavailable(ex);
        }

        try
        {
            foreach (var request in _requests.GetConsumingEnumerable())
            {
                Interlocked.CompareExchange(ref _pending, null, request); // started: a newer request no longer drops it
                if (request.Completion.Task.IsCompleted)
                {
                    continue; // superseded while waiting
                }

                if (request.Cancellation.IsCancellationRequested || session is null)
                {
                    request.Completion.TrySetResult([]);
                    continue;
                }

                try
                {
                    var found = session.Find(request.Window, request.Point);
                    LogFound(request.Window, request.Point, found.Count, found.Count > 0 ? found[0].Description : "-");
                    request.Completion.TrySetResult(found);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    LogFailed(ex, request.Window);
                    request.Completion.TrySetResult([]);
                }
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "UI Automation could not be started; Capture Object picks whole windows only")]
    private partial void LogUnavailable(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Objects of window 0x{Window:X} at {Point}: {Count}, innermost {Innermost}")]
    private partial void LogFound(nint window, PixelPoint point, int count, string innermost);

    [LoggerMessage(Level = LogLevel.Debug, Message = "UI Automation could not read window 0x{Window:X}")]
    private partial void LogFailed(Exception ex, nint window);

    private sealed record Request(nint Window, PixelPoint Point, CancellationToken Cancellation)
    {
        public TaskCompletionSource<IReadOnlyList<ScreenObject>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The UI Automation objects of the worker thread.</summary>
    private sealed class Session : IDisposable
    {
        private readonly IUIAutomation _automation;
        private readonly nint _cacheRequest;
        private readonly nint _trueCondition;
        private readonly bool _uninitialize;

        public Session()
        {
            _uninitialize = Ole32.CoInitializeEx(0, Ole32.CoinitMultithreaded) >= 0;
            var clsid = UiAutomationGuids.CUIAutomation;
            var iid = UiAutomationGuids.IUIAutomation;
            GeneratedCom.ThrowIfFailed(Ole32.CoCreateInstance(in clsid, 0, Ole32.ClsctxInprocServer, in iid, out var pointer), "CoCreateInstance(CUIAutomation)");
            try
            {
                _automation = GeneratedCom.Wrap<IUIAutomation>(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }

            GeneratedCom.ThrowIfFailed(_automation.CreateCacheRequest(out _cacheRequest), "CreateCacheRequest");
            var request = GeneratedCom.Wrap<IUIAutomationCacheRequest>(_cacheRequest);
            try
            {
                GeneratedCom.ThrowIfFailed(request.AddProperty(UiAutomationGuids.BoundingRectanglePropertyId), "AddProperty(BoundingRectangle)");
                GeneratedCom.ThrowIfFailed(request.AddProperty(UiAutomationGuids.NamePropertyId), "AddProperty(Name)");
                GeneratedCom.ThrowIfFailed(request.AddProperty(UiAutomationGuids.LocalizedControlTypePropertyId), "AddProperty(LocalizedControlType)");
                GeneratedCom.ThrowIfFailed(request.AddProperty(UiAutomationGuids.ControlTypePropertyId), "AddProperty(ControlType)");
            }
            finally
            {
                GeneratedCom.Release(request);
            }

            GeneratedCom.ThrowIfFailed(_automation.CreateTrueCondition(out _trueCondition), "CreateTrueCondition");
        }

        /// <summary>The chain of objects containing <paramref name="point"/>, innermost first, the window's own element last.</summary>
        public IReadOnlyList<ScreenObject> Find(nint window, PixelPoint point)
        {
            var chain = new List<ScreenObject>();
            var held = new List<IUIAutomationElement>();
            try
            {
                GeneratedCom.ThrowIfFailed(_automation.ElementFromHandleBuildCache(window, _cacheRequest, out var rootPointer), "ElementFromHandleBuildCache");
                var current = Take(rootPointer, held);
                chain.Add(Describe(current));

                for (var depth = 0; depth < MaxDepth; depth++)
                {
                    if (current.GetCachedControlType(out var type) >= 0 && WholeControls.Contains(type) && depth > 0)
                    {
                        break;
                    }

                    if (current.FindAllBuildCache(UiAutomationGuids.TreeScopeChildren, _trueCondition, _cacheRequest, out var arrayPointer) < 0 || arrayPointer == 0)
                    {
                        break;
                    }

                    var next = Smallest(arrayPointer, point, held);
                    if (next is null)
                    {
                        break;
                    }

                    current = next.Value.Element;
                    chain.Add(next.Value.Object);
                }
            }
            finally
            {
                foreach (var element in held)
                {
                    GeneratedCom.Release(element);
                }
            }

            chain.Reverse();
            return chain;
        }

        public void Dispose()
        {
            Marshal.Release(_trueCondition);
            Marshal.Release(_cacheRequest);
            GeneratedCom.Release(_automation);
            if (_uninitialize)
            {
                Ole32.CoUninitialize();
            }
        }

        /// <summary>The child (of the array) with the smallest rectangle containing <paramref name="point"/>.</summary>
        private static (IUIAutomationElement Element, ScreenObject Object)? Smallest(nint arrayPointer, PixelPoint point, List<IUIAutomationElement> held)
        {
            var array = GeneratedCom.Wrap<IUIAutomationElementArray>(arrayPointer);
            Marshal.Release(arrayPointer);
            try
            {
                if (array.GetLength(out var length) < 0)
                {
                    return null;
                }

                (IUIAutomationElement Element, ScreenObject Object)? best = null;
                for (var i = 0; i < length; i++)
                {
                    if (array.GetElement(i, out var pointer) < 0 || pointer == 0)
                    {
                        continue;
                    }

                    var element = Take(pointer, held);
                    if (element.GetCachedBoundingRectangle(out var rect) < 0)
                    {
                        continue;
                    }

                    var bounds = rect.ToPixelRect();
                    if (bounds.Contains(point) && (best is null || bounds.Area < best.Value.Object.Bounds.Area))
                    {
                        best = (element, Describe(element, bounds));
                    }
                }

                return best;
            }
            finally
            {
                GeneratedCom.Release(array);
            }
        }

        private static IUIAutomationElement Take(nint pointer, List<IUIAutomationElement> held)
        {
            try
            {
                var element = GeneratedCom.Wrap<IUIAutomationElement>(pointer);
                held.Add(element);
                return element;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        private static ScreenObject Describe(IUIAutomationElement element, PixelRect? bounds = null)
        {
            if (bounds is null)
            {
                bounds = element.GetCachedBoundingRectangle(out var rect) >= 0 ? rect.ToPixelRect() : PixelRect.Empty;
            }

            return new ScreenObject(bounds.Value, String(element.GetCachedName(out var name), name), String(element.GetCachedLocalizedControlType(out var kind), kind));
        }

        private static string String(int hr, nint bstr)
        {
            if (hr < 0 || bstr == 0)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringBSTR(bstr).Trim();
            }
            finally
            {
                Marshal.FreeBSTR(bstr);
            }
        }
    }
}
