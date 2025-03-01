using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Embedding;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.UnitTests;
using Avalonia.VisualTree;
using JetBrains.Annotations;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

public class CompositorTestsBase
{
    public class DebugEvents : ICompositionTargetDebugEvents
    {
        public List<Rect> Rects = new();

        public void RectInvalidated(Rect rc)
        {
            Rects.Add(rc);
        }

        public void Reset()
        {
            Rects.Clear();
        }
    }

    public class ManualRenderTimer : IRenderTimer
    {
        public event Action<TimeSpan> Tick;
        public bool RunsInBackground => false;
        public void TriggerTick() => Tick?.Invoke(TimeSpan.Zero);
        public Task TriggerBackgroundTick() => Task.Run(TriggerTick);
    }

    class TopLevelImpl : ITopLevelImpl
    {
        private readonly Compositor _compositor;
        public CompositingRenderer Renderer { get; private set; }

        public TopLevelImpl(Compositor compositor, Size clientSize)
        {
            ClientSize = clientSize;
            _compositor = compositor;
        }

        public void Dispose()
        {

        }

        public Size ClientSize { get; }
        public Size? FrameSize { get; }
        public double RenderScaling => 1;
        public IEnumerable<object> Surfaces { get; } = Array.Empty<object>();
        public Action<RawInputEventArgs> Input { get; set; }
        public Action<Rect> Paint { get; set; }
        public Action<Size, PlatformResizeReason> Resized { get; set; }
        public Action<double> ScalingChanged { get; set; }
        public Action<WindowTransparencyLevel> TransparencyLevelChanged { get; set; }

        public IRenderer CreateRenderer(IRenderRoot root)
        {
            return Renderer = new CompositingRenderer(root, _compositor, () => Surfaces);
        }

        public void Invalidate(Rect rect)
        {
        }

        public void SetInputRoot(IInputRoot inputRoot)
        {
        }

        public Point PointToClient(PixelPoint point) => default;

        public PixelPoint PointToScreen(Point point) => new();

        public void SetCursor(ICursorImpl cursor)
        {
        }

        public Action Closed { get; set; }
        public Action LostFocus { get; set; }
        public IMouseDevice MouseDevice { get; } = new MouseDevice();
        public IPopupImpl CreatePopup() => throw new NotImplementedException();

        public void SetTransparencyLevelHint(WindowTransparencyLevel transparencyLevel)
        {
        }

        public WindowTransparencyLevel TransparencyLevel { get; }
        public AcrylicPlatformCompensationLevels AcrylicCompensationLevels { get; }
    }

    protected class CompositorServices : IDisposable
    {
        private readonly IDisposable _app;
        public Compositor Compositor { get; }
        public ManualRenderTimer Timer { get; } = new();
        public EmbeddableControlRoot TopLevel { get; }
        public CompositingRenderer Renderer { get; } = null!;
        public DebugEvents Events { get; } = new();

        public void Dispose()
        {
            TopLevel.Renderer.Stop();
            TopLevel.Dispose();
            _app.Dispose();
        }

        public CompositorServices(Size? size = null)
        {
            _app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface);
            try
            {
                AvaloniaLocator.CurrentMutable.Bind<IRenderTimer>().ToConstant(Timer);
                AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>()
                    .ToConstant(new RenderLoop(Timer, Dispatcher.UIThread));

                Compositor = new Compositor(AvaloniaLocator.Current.GetRequiredService<IRenderLoop>(), null);
                var impl = new TopLevelImpl(Compositor, size ?? new Size(1000, 1000));
                TopLevel = new EmbeddableControlRoot(impl)
                {
                    Template = new FuncControlTemplate((parent, scope) =>
                    {
                        var presenter = new ContentPresenter
                        {
                            [~ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty)
                        };
                        scope.Register("PART_ContentPresenter", presenter);
                        return presenter;
                    })
                };
                Renderer = impl.Renderer;
                TopLevel.Prepare();
                TopLevel.Renderer.Start();
                RunJobs();
                Renderer.CompositionTarget.Server.DebugEvents = Events;
            }
            catch
            {
                _app.Dispose();
                throw;
            }
        }

        public void RunJobs()
        {
            Dispatcher.UIThread.RunJobs();
            Timer.TriggerTick();
            Dispatcher.UIThread.RunJobs();
        }

        public void AssertRects(params Rect[] rects)
        {
            RunJobs();
            var toAssert = rects.Select(x => x.ToString()).Distinct().OrderBy(x => x);
            var invalidated = Events.Rects.Select(x => x.ToString()).Distinct().OrderBy(x => x);
            Assert.Equal(toAssert, invalidated);
            Events.Rects.Clear();
        }

        public void AssertHitTest(double x, double y, Func<Visual, bool> filter, params object[] expected)
            => AssertHitTest(new Point(x, y), filter, expected);
        public void AssertHitTest(Point pt, Func<Visual, bool> filter, params object[] expected)
        {
            RunJobs();
            var tested = Renderer.HitTest(pt, TopLevel, filter);
            Assert.Equal(expected, tested);
        }
        
        public void AssertHitTestFirst(Point pt, Func<Visual, bool> filter, object expected)
        {
            RunJobs();
            var tested = Renderer.HitTest(pt, TopLevel, filter).First();
            Assert.Equal(expected, tested);
        }
    }


    protected class CompositorCanvas : CompositorServices
    {
        public Canvas Canvas { get; } = new();

        public CompositorCanvas()
        {
            TopLevel.Content = Canvas;
            RunJobs();
            Events.Reset();
        }
    }
}
