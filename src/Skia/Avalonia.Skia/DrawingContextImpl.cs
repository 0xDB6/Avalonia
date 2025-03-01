using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Rendering.Utilities;
using Avalonia.Utilities;
using Avalonia.Media.Imaging;
using JetBrains.Annotations;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Skia based drawing context.
    /// </summary>
    internal class DrawingContextImpl : IDrawingContextImpl, IDrawingContextWithAcrylicLikeSupport
    {
        private IDisposable[] _disposables;
        private readonly Vector _dpi;
        private readonly Stack<PaintWrapper> _maskStack = new Stack<PaintWrapper>();
        private readonly Stack<double> _opacityStack = new Stack<double>();
        private readonly Stack<BitmapBlendingMode> _blendingModeStack = new Stack<BitmapBlendingMode>();
        private readonly Matrix? _postTransform;
        private readonly IVisualBrushRenderer _visualBrushRenderer;
        private double _currentOpacity = 1.0f;
        private BitmapBlendingMode _currentBlendingMode = BitmapBlendingMode.SourceOver;
        private readonly bool _canTextUseLcdRendering;
        private Matrix _currentTransform;
        private bool _disposed;
        private GRContext _grContext;
        public GRContext GrContext => _grContext;
        private ISkiaGpu _gpu;
        private readonly SKPaint _strokePaint = SKPaintCache.Get();
        private readonly SKPaint _fillPaint = SKPaintCache.Get();
        private readonly SKPaint _boxShadowPaint = SKPaintCache.Get();
        private static SKShader s_acrylicNoiseShader;
        private readonly ISkiaGpuRenderSession _session;
        private bool _leased = false;

        /// <summary>
        /// Context create info.
        /// </summary>
        public struct CreateInfo
        {
            /// <summary>
            /// Canvas to draw to.
            /// </summary>
            public SKCanvas Canvas;

            /// <summary>
            /// Surface to draw to.
            /// </summary>
            public SKSurface Surface;

            /// <summary>
            /// Dpi of drawings.
            /// </summary>
            public Vector Dpi;

            /// <summary>
            /// Visual brush renderer.
            /// </summary>
            public IVisualBrushRenderer VisualBrushRenderer;

            /// <summary>
            /// Render text without Lcd rendering.
            /// </summary>
            public bool DisableTextLcdRendering;

            /// <summary>
            /// GPU-accelerated context (optional)
            /// </summary>
            public GRContext GrContext;

            /// <summary>
            /// Skia GPU provider context (optional)
            /// </summary>
            public ISkiaGpu Gpu;

            public ISkiaGpuRenderSession CurrentSession;
        }

        class SkiaLeaseFeature : ISkiaSharpApiLeaseFeature
        {
            private readonly DrawingContextImpl _context;

            public SkiaLeaseFeature(DrawingContextImpl context)
            {
                _context = context;
            }

            public ISkiaSharpApiLease Lease()
            {
                _context.CheckLease();
                return new ApiLease(_context);
            }

            class ApiLease : ISkiaSharpApiLease
            {
                private DrawingContextImpl _context;
                private readonly SKMatrix _revertTransform;

                public ApiLease(DrawingContextImpl context)
                {
                    _revertTransform = context.Canvas.TotalMatrix;
                    _context = context;
                    _context._leased = true;
                }

                public SKCanvas SkCanvas => _context.Canvas;
                public GRContext GrContext => _context.GrContext;
                public SKSurface SkSurface => _context.Surface;
                public double CurrentOpacity => _context._currentOpacity;
                
                public void Dispose()
                {
                    _context.Canvas.SetMatrix(_revertTransform);
                    _context._leased = false;
                    _context = null;
                }
            }
        }
        
        /// <summary>
        /// Create new drawing context.
        /// </summary>
        /// <param name="createInfo">Create info.</param>
        /// <param name="disposables">Array of elements to dispose after drawing has finished.</param>
        public DrawingContextImpl(CreateInfo createInfo, params IDisposable[] disposables)
        {
            _dpi = createInfo.Dpi;
            _visualBrushRenderer = createInfo.VisualBrushRenderer;
            _disposables = disposables;
            _canTextUseLcdRendering = !createInfo.DisableTextLcdRendering;
            _grContext = createInfo.GrContext;
            _gpu = createInfo.Gpu;
            if (_grContext != null)
                Monitor.Enter(_grContext);
            Surface = createInfo.Surface;
            Canvas = createInfo.Canvas ?? createInfo.Surface?.Canvas;

            _session = createInfo.CurrentSession;

            if (Canvas == null)
            {
                throw new ArgumentException("Invalid create info - no Canvas provided", nameof(createInfo));
            }

            if (!_dpi.NearlyEquals(SkiaPlatform.DefaultDpi))
            {
                _postTransform =
                    Matrix.CreateScale(_dpi.X / SkiaPlatform.DefaultDpi.X, _dpi.Y / SkiaPlatform.DefaultDpi.Y);
            }

            Transform = Matrix.Identity;
        }
        
        /// <summary>
        /// Skia canvas.
        /// </summary>
        public SKCanvas Canvas { get; }
        public SKSurface Surface { get; }

        private void CheckLease()
        {
            if (_leased)
                throw new InvalidOperationException("The underlying graphics API is currently leased");
        }
        
        /// <inheritdoc />
        public void Clear(Color color)
        {
            CheckLease();
            Canvas.Clear(color.ToSKColor());
        }

        /// <inheritdoc />
        public void DrawBitmap(IRef<IBitmapImpl> source, double opacity, Rect sourceRect, Rect destRect, BitmapInterpolationMode bitmapInterpolationMode)
        {
            CheckLease();
            var drawableImage = (IDrawableBitmapImpl)source.Item;
            var s = sourceRect.ToSKRect();
            var d = destRect.ToSKRect();

            var paint = SKPaintCache.Get();
            paint.Color = new SKColor(255, 255, 255, (byte)(255 * opacity * _currentOpacity));
            paint.FilterQuality = bitmapInterpolationMode.ToSKFilterQuality();
            paint.BlendMode = _currentBlendingMode.ToSKBlendMode();

            drawableImage.Draw(this, s, d, paint);
            SKPaintCache.ReturnReset(paint);
        }

        /// <inheritdoc />
        public void DrawBitmap(IRef<IBitmapImpl> source, IBrush opacityMask, Rect opacityMaskRect, Rect destRect)
        {
            CheckLease();
            PushOpacityMask(opacityMask, opacityMaskRect);
            DrawBitmap(source, 1, new Rect(0, 0, source.Item.PixelSize.Width, source.Item.PixelSize.Height), destRect, BitmapInterpolationMode.Default);
            PopOpacityMask();
        }

        /// <inheritdoc />
        public void DrawLine(IPen pen, Point p1, Point p2)
        {
            CheckLease();
            using (var paint = CreatePaint(_strokePaint, pen, new Size(Math.Abs(p2.X - p1.X), Math.Abs(p2.Y - p1.Y))))
            {
                if (paint.Paint is object)
                {
                    Canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, paint.Paint);
                }
            }
        }

        /// <inheritdoc />
        public void DrawGeometry(IBrush brush, IPen pen, IGeometryImpl geometry)
        {
            CheckLease();
            var impl = (GeometryImpl) geometry;
            var size = geometry.Bounds.Size;

            using (var fill = brush != null ? CreatePaint(_fillPaint, brush, size) : default(PaintWrapper))
            using (var stroke = pen?.Brush != null ? CreatePaint(_strokePaint, pen, 
                size.Inflate(new Thickness(pen?.Thickness / 2 ?? 0))) : default(PaintWrapper))
            {
                if (fill.Paint != null)
                {
                    Canvas.DrawPath(impl.EffectivePath, fill.Paint);
                }

                if (stroke.Paint != null)
                {
                    Canvas.DrawPath(impl.EffectivePath, stroke.Paint);
                }
            }
        }

        struct BoxShadowFilter : IDisposable
        {
            public SKPaint Paint;
            private SKImageFilter _filter;
            public SKClipOperation ClipOperation;

            static float SkBlurRadiusToSigma(double radius) {
                if (radius <= 0)
                    return 0.0f;
                return 0.288675f * (float)radius + 0.5f;
            }
            public static BoxShadowFilter Create(SKPaint paint, BoxShadow shadow, double opacity)
            {
                var ac = shadow.Color;

                SKImageFilter filter = null;
                filter = SKImageFilter.CreateBlur(SkBlurRadiusToSigma(shadow.Blur), SkBlurRadiusToSigma(shadow.Blur));
                var color = new SKColor(ac.R, ac.G, ac.B, (byte)(ac.A * opacity));

                paint.Reset();
                paint.IsAntialias = true;
                paint.Color = color;
                paint.ImageFilter = filter;
                
                return new BoxShadowFilter
                {
                    Paint = paint, _filter = filter,
                    ClipOperation = shadow.IsInset ? SKClipOperation.Intersect : SKClipOperation.Difference
                };
            }

            public void Dispose()
            {
                Paint.Reset();
                Paint = null;
                _filter?.Dispose();
            }
        }

        static SKRect AreaCastingShadowInHole(
            SKRect hole_rect,
            float shadow_blur,
            float shadow_spread,
            float offsetX, float offsetY)
        {
            // Adapted from Chromium
            var bounds = hole_rect;

            bounds.Inflate(shadow_blur, shadow_blur);

            if (shadow_spread < 0)
                bounds.Inflate(-shadow_spread, -shadow_spread);

            var offset_bounds = bounds;
            offset_bounds.Offset(-offsetX, -offsetY);
            bounds.Union(offset_bounds);
            return bounds;
        }

        /// <inheritdoc />
        public void DrawRectangle(IExperimentalAcrylicMaterial material, RoundedRect rect)
        {
            if (rect.Rect.Height <= 0 || rect.Rect.Width <= 0)
                return;
            CheckLease();
            
            var rc = rect.Rect.ToSKRect();
            var isRounded = rect.IsRounded;
            var needRoundRect = rect.IsRounded;
            using var skRoundRect = needRoundRect ? new SKRoundRect() : null;

            if (needRoundRect)
                skRoundRect.SetRectRadii(rc,
                    new[]
                    {
                        rect.RadiiTopLeft.ToSKPoint(), rect.RadiiTopRight.ToSKPoint(),
                        rect.RadiiBottomRight.ToSKPoint(), rect.RadiiBottomLeft.ToSKPoint(),
                    });

            if (material != null)
            {
                using (var paint = CreateAcrylicPaint(_fillPaint, material))
                {
                    if (isRounded)
                    {
                        Canvas.DrawRoundRect(skRoundRect, paint.Paint);
                    }
                    else
                    {
                        Canvas.DrawRect(rc, paint.Paint);
                    }

                }
            }
        }

        /// <inheritdoc />
        public void DrawRectangle(IBrush brush, IPen pen, RoundedRect rect, BoxShadows boxShadows = default)
        {
            if (rect.Rect.Height <= 0 || rect.Rect.Width <= 0)
                return;
            CheckLease();
            // Arbitrary chosen values
            // On OSX Skia breaks OpenGL context when asked to draw, e. g. (0, 0, 623, 6666600) rect
            if (rect.Rect.Height > 8192 || rect.Rect.Width > 8192)
                boxShadows = default;

            var rc = rect.Rect.ToSKRect();
            var isRounded = rect.IsRounded;
            var needRoundRect = rect.IsRounded || (boxShadows.HasInsetShadows);
            using var skRoundRect = needRoundRect ? new SKRoundRect() : null;
            if (needRoundRect)
                skRoundRect.SetRectRadii(rc,
                    new[]
                    {
                        rect.RadiiTopLeft.ToSKPoint(), rect.RadiiTopRight.ToSKPoint(),
                        rect.RadiiBottomRight.ToSKPoint(), rect.RadiiBottomLeft.ToSKPoint(),
                    });

            foreach (var boxShadow in boxShadows)
            {
                if (!boxShadow.IsDefault && !boxShadow.IsInset)
                {
                    using (var shadow = BoxShadowFilter.Create(_boxShadowPaint, boxShadow, _currentOpacity))
                    {
                        var spread = (float)boxShadow.Spread;
                        if (boxShadow.IsInset)
                            spread = -spread;

                        Canvas.Save();
                        if (isRounded)
                        {
                            using var shadowRect = new SKRoundRect(skRoundRect);
                            if (spread != 0)
                                shadowRect.Inflate(spread, spread);
                            Canvas.ClipRoundRect(skRoundRect,
                                shadow.ClipOperation, true);
                            
                            var oldTransform = Transform;
                            Transform = oldTransform * Matrix.CreateTranslation(boxShadow.OffsetX, boxShadow.OffsetY);
                            Canvas.DrawRoundRect(shadowRect, shadow.Paint);
                            Transform = oldTransform;
                        }
                        else
                        {
                            var shadowRect = rc;
                            if (spread != 0)
                                shadowRect.Inflate(spread, spread);
                            Canvas.ClipRect(rc, shadow.ClipOperation);
                            var oldTransform = Transform;
                            Transform = oldTransform * Matrix.CreateTranslation(boxShadow.OffsetX, boxShadow.OffsetY);
                            Canvas.DrawRect(shadowRect, shadow.Paint);
                            Transform = oldTransform;
                        }

                        Canvas.Restore();
                    }
                }
            }

            if (brush != null)
            {
                using (var paint = CreatePaint(_fillPaint, brush, rect.Rect.Size))
                {
                    if (isRounded)
                    {
                        Canvas.DrawRoundRect(skRoundRect, paint.Paint);
                    }
                    else
                    {
                        Canvas.DrawRect(rc, paint.Paint);
                    }
                }
            }

            foreach (var boxShadow in boxShadows)
            {
                if (!boxShadow.IsDefault && boxShadow.IsInset)
                {
                    using (var shadow = BoxShadowFilter.Create(_boxShadowPaint, boxShadow, _currentOpacity))
                    {
                        var spread = (float)boxShadow.Spread;
                        var offsetX = (float)boxShadow.OffsetX;
                        var offsetY = (float)boxShadow.OffsetY;
                        var outerRect = AreaCastingShadowInHole(rc, (float)boxShadow.Blur, spread, offsetX, offsetY);

                        Canvas.Save();
                        using var shadowRect = new SKRoundRect(skRoundRect);
                        if (spread != 0)
                            shadowRect.Deflate(spread, spread);
                        Canvas.ClipRoundRect(skRoundRect,
                            shadow.ClipOperation, true);
                        
                        var oldTransform = Transform;
                        Transform = oldTransform * Matrix.CreateTranslation(boxShadow.OffsetX, boxShadow.OffsetY);
                        using (var outerRRect = new SKRoundRect(outerRect))
                            Canvas.DrawRoundRectDifference(outerRRect, shadowRect, shadow.Paint);
                        Transform = oldTransform;
                        Canvas.Restore();
                    }
                }
            }

            if (pen?.Brush != null)
            {
                using (var paint = CreatePaint(_strokePaint, pen, rect.Rect.Size.Inflate(new Thickness(pen?.Thickness / 2 ?? 0))))
                {
                    if (paint.Paint is object)
                    {
                        if (isRounded)
                        {
                            Canvas.DrawRoundRect(skRoundRect, paint.Paint);
                        }
                        else
                        {
                            Canvas.DrawRect(rc, paint.Paint);
                        }
                    }
                }
            }
        }

        /// <inheritdoc />
        public void DrawEllipse(IBrush brush, IPen pen, Rect rect)
        {
            if (rect.Height <= 0 || rect.Width <= 0)
                return;
            CheckLease();
            
            var rc = rect.ToSKRect();

            if (brush != null)
            {
                using (var paint = CreatePaint(_fillPaint, brush, rect.Size))
                {
                    Canvas.DrawOval(rc, paint.Paint);
                }
            }

            if (pen?.Brush != null)
            {
                using (var paint = CreatePaint(_strokePaint, pen, rect.Size.Inflate(new Thickness(pen?.Thickness / 2 ?? 0))))
                {
                    if (paint.Paint is object)
                    {
                        Canvas.DrawOval(rc, paint.Paint);
                    }
                }
            }
        }
       
        /// <inheritdoc />
        public void DrawGlyphRun(IBrush foreground, GlyphRun glyphRun)
        {
            CheckLease();
            using (var paintWrapper = CreatePaint(_fillPaint, foreground, glyphRun.Size))
            {
                var glyphRunImpl = (GlyphRunImpl)glyphRun.GlyphRunImpl;

                Canvas.DrawText(glyphRunImpl.TextBlob, (float)glyphRun.BaselineOrigin.X,
                    (float)glyphRun.BaselineOrigin.Y, paintWrapper.Paint);
            }
        }

        /// <inheritdoc />
        public IDrawingContextLayerImpl CreateLayer(Size size)
        {
            CheckLease();
            return CreateRenderTarget(size, true);
        }

        /// <inheritdoc />
        public void PushClip(Rect clip)
        {
            CheckLease();
            Canvas.Save();
            Canvas.ClipRect(clip.ToSKRect());
        }

        public void PushClip(RoundedRect clip)
        {
            CheckLease();
            Canvas.Save();
            Canvas.ClipRoundRect(clip.ToSKRoundRect(), antialias:true);
        }

        /// <inheritdoc />
        public void PopClip()
        {
            CheckLease();
            Canvas.Restore();
        }

        /// <inheritdoc />
        public void PushOpacity(double opacity)
        {
            CheckLease();
            _opacityStack.Push(_currentOpacity);
            _currentOpacity *= opacity;
        }

        /// <inheritdoc />
        public void PopOpacity()
        {
            CheckLease();
            _currentOpacity = _opacityStack.Pop();
        }

        /// <inheritdoc />
        public virtual void Dispose()
        {
            if(_disposed)
                return;
            CheckLease();
            try
            {
                // Return leased paints.
                SKPaintCache.ReturnReset(_strokePaint);
                SKPaintCache.ReturnReset(_fillPaint);
                SKPaintCache.ReturnReset(_boxShadowPaint);

                if (_grContext != null)
                {
                    Monitor.Exit(_grContext);
                    _grContext = null;
                }

                if (_disposables != null)
                {
                    foreach (var disposable in _disposables)
                        disposable?.Dispose();
                    _disposables = null;
                }
            }
            finally
            {
                _disposed = true;
            }
        }

        /// <inheritdoc />
        public void PushGeometryClip(IGeometryImpl clip)
        {
            CheckLease();
            Canvas.Save();
            Canvas.ClipPath(((GeometryImpl)clip).EffectivePath, SKClipOperation.Intersect, true);
        }

        /// <inheritdoc />
        public void PopGeometryClip()
        {
            CheckLease();
            Canvas.Restore();
        }

        /// <inheritdoc />
        public void PushBitmapBlendMode(BitmapBlendingMode blendingMode)
        {
            CheckLease();
            _blendingModeStack.Push(_currentBlendingMode);
            _currentBlendingMode = blendingMode;
        }

        /// <inheritdoc />
        public void PopBitmapBlendMode()
        {
            CheckLease();
            _currentBlendingMode = _blendingModeStack.Pop();
        }

        public void Custom(ICustomDrawOperation custom)
        {
            CheckLease();
            custom.Render(this);
        }

        /// <inheritdoc />
        public void PushOpacityMask(IBrush mask, Rect bounds)
        {
            CheckLease();

            var paint = SKPaintCache.Get();

            Canvas.SaveLayer(paint);
            _maskStack.Push(CreatePaint(paint, mask, bounds.Size));
        }

        /// <inheritdoc />
        public void PopOpacityMask()
        {
            CheckLease();

            var paint = SKPaintCache.Get();
            paint.BlendMode = SKBlendMode.DstIn;
            
            Canvas.SaveLayer(paint);
            SKPaintCache.ReturnReset(paint);

            PaintWrapper paintWrapper;
            using (paintWrapper = _maskStack.Pop())
            {
                Canvas.DrawPaint(paintWrapper.Paint);
            }
            // Return the paint wrapper's paint less the reset since the paint is already reset in the Dispose method above.
            SKPaintCache.Return(paintWrapper.Paint);

            Canvas.Restore();

            Canvas.Restore();
        }

        /// <inheritdoc />
        public Matrix Transform
        {
            get { return _currentTransform; }
            set
            {
                CheckLease();
                if (_currentTransform == value)
                    return;

                _currentTransform = value;

                var transform = value;

                if (_postTransform.HasValue)
                {
                    transform *= _postTransform.Value;
                }

                Canvas.SetMatrix(transform.ToSKMatrix());
            }
        }

        [CanBeNull]
        public object GetFeature(Type t)
        {
            if (t == typeof(ISkiaSharpApiLeaseFeature))
                return new SkiaLeaseFeature(this);
            return null;
        }

        /// <summary>
        /// Configure paint wrapper for using gradient brush.
        /// </summary>
        /// <param name="paintWrapper">Paint wrapper.</param>
        /// <param name="targetSize">Target size.</param>
        /// <param name="gradientBrush">Gradient brush.</param>
        private static void ConfigureGradientBrush(ref PaintWrapper paintWrapper, Size targetSize, IGradientBrush gradientBrush)
        {
            var tileMode = gradientBrush.SpreadMethod.ToSKShaderTileMode();
            var stopColors = gradientBrush.GradientStops.Select(s => s.Color.ToSKColor()).ToArray();
            var stopOffsets = gradientBrush.GradientStops.Select(s => (float)s.Offset).ToArray();

            switch (gradientBrush)
            {
                case ILinearGradientBrush linearGradient:
                {
                    var start = linearGradient.StartPoint.ToPixels(targetSize).ToSKPoint();
                    var end = linearGradient.EndPoint.ToPixels(targetSize).ToSKPoint();

                    // would be nice to cache these shaders possibly?
                    if (linearGradient.Transform is null)
                    {
                        using (var shader =
                            SKShader.CreateLinearGradient(start, end, stopColors, stopOffsets, tileMode))
                        {
                            paintWrapper.Paint.Shader = shader;
                        }
                    }
                    else
                    {
                        var transformOrigin = linearGradient.TransformOrigin.ToPixels(targetSize);
                        var offset = Matrix.CreateTranslation(transformOrigin);
                        var transform = (-offset) * linearGradient.Transform.Value * (offset);

                        using (var shader =
                            SKShader.CreateLinearGradient(start, end, stopColors, stopOffsets, tileMode, transform.ToSKMatrix()))
                        {
                            paintWrapper.Paint.Shader = shader;
                        }   
                    }

                    break;
                }
                case IRadialGradientBrush radialGradient:
                {
                    var center = radialGradient.Center.ToPixels(targetSize).ToSKPoint();
                    var radius = (float)(radialGradient.Radius * targetSize.Width);

                    var origin = radialGradient.GradientOrigin.ToPixels(targetSize).ToSKPoint();

                    if (origin.Equals(center))
                    {
                        // when the origin is the same as the center the Skia RadialGradient acts the same as D2D
                        if (radialGradient.Transform is null)
                        {
                            using (var shader =
                                SKShader.CreateRadialGradient(center, radius, stopColors, stopOffsets, tileMode))
                            {
                                paintWrapper.Paint.Shader = shader;
                            }
                        }
                        else
                        {
                            var transformOrigin = radialGradient.TransformOrigin.ToPixels(targetSize);
                            var offset = Matrix.CreateTranslation(transformOrigin);
                            var transform = (-offset) * radialGradient.Transform.Value * (offset);
                        
                            using (var shader =
                                SKShader.CreateRadialGradient(center, radius, stopColors, stopOffsets, tileMode, transform.ToSKMatrix()))
                            {
                                paintWrapper.Paint.Shader = shader;
                            }
                        }
                    }
                    else
                    {
                        // when the origin is different to the center use a two point ConicalGradient to match the behaviour of D2D

                        // reverse the order of the stops to match D2D
                        var reversedColors = new SKColor[stopColors.Length];
                        Array.Copy(stopColors, reversedColors, stopColors.Length);
                        Array.Reverse(reversedColors);

                        // and then reverse the reference point of the stops
                        var reversedStops = new float[stopOffsets.Length];
                        for (var i = 0; i < stopOffsets.Length; i++)
                        {
                            reversedStops[i] = stopOffsets[i];
                            if (reversedStops[i] > 0 && reversedStops[i] < 1)
                            {
                                reversedStops[i] = Math.Abs(1 - stopOffsets[i]);
                            }
                        }
                            
                        // compose with a background colour of the final stop to match D2D's behaviour of filling with the final color
                        if (radialGradient.Transform is null)
                        {
                            using (var shader = SKShader.CreateCompose(
                                SKShader.CreateColor(reversedColors[0]),
                                SKShader.CreateTwoPointConicalGradient(center, radius, origin, 0, reversedColors, reversedStops, tileMode)
                            ))
                            {
                                paintWrapper.Paint.Shader = shader;
                            }
                        }
                        else
                        {
                                
                            var transformOrigin = radialGradient.TransformOrigin.ToPixels(targetSize);
                            var offset = Matrix.CreateTranslation(transformOrigin);
                            var transform = (-offset) * radialGradient.Transform.Value * (offset);
                           
                            using (var shader = SKShader.CreateCompose(
                                SKShader.CreateColor(reversedColors[0]),
                                SKShader.CreateTwoPointConicalGradient(center, radius, origin, 0, reversedColors, reversedStops, tileMode, transform.ToSKMatrix())
                            ))
                            {
                                paintWrapper.Paint.Shader = shader;
                            } 
                        }
                    }

                    break;
                }
                case IConicGradientBrush conicGradient:
                {
                    var center = conicGradient.Center.ToPixels(targetSize).ToSKPoint();

                    // Skia's default is that angle 0 is from the right hand side of the center point
                    // but we are matching CSS where the vertical point above the center is 0.
                    var angle = (float)(conicGradient.Angle - 90);
                    var rotation = SKMatrix.CreateRotationDegrees(angle, center.X, center.Y);

                    if (conicGradient.Transform is { })
                    {
                            
                        var transformOrigin = conicGradient.TransformOrigin.ToPixels(targetSize);
                        var offset = Matrix.CreateTranslation(transformOrigin);
                        var transform = (-offset) * conicGradient.Transform.Value * (offset);

                        rotation = rotation.PreConcat(transform.ToSKMatrix());
                    }

                    using (var shader = 
                        SKShader.CreateSweepGradient(center, stopColors, stopOffsets, rotation))
                    {
                        paintWrapper.Paint.Shader = shader;
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// Configure paint wrapper for using tile brush.
        /// </summary>
        /// <param name="paintWrapper">Paint wrapper.</param>
        /// <param name="targetSize">Target size.</param>
        /// <param name="tileBrush">Tile brush to use.</param>
        /// <param name="tileBrushImage">Tile brush image.</param>
        private void ConfigureTileBrush(ref PaintWrapper paintWrapper, Size targetSize, ITileBrush tileBrush, IDrawableBitmapImpl tileBrushImage)
        {
            var calc = new TileBrushCalculator(tileBrush, tileBrushImage.PixelSize.ToSizeWithDpi(_dpi), targetSize);
            var intermediate = CreateRenderTarget(calc.IntermediateSize, false);

            paintWrapper.AddDisposable(intermediate);

            using (var context = intermediate.CreateDrawingContext(null))
            {
                var sourceRect = new Rect(tileBrushImage.PixelSize.ToSizeWithDpi(96));
                var targetRect = new Rect(tileBrushImage.PixelSize.ToSizeWithDpi(_dpi));

                context.Clear(Colors.Transparent);
                context.PushClip(calc.IntermediateClip);
                context.Transform = calc.IntermediateTransform;
                context.DrawBitmap(
                    RefCountable.CreateUnownedNotClonable(tileBrushImage),
                    1,
                    sourceRect,
                    targetRect,
                    tileBrush.BitmapInterpolationMode);
                context.PopClip();
            }

            var tileTransform =
                tileBrush.TileMode != TileMode.None
                    ? SKMatrix.CreateTranslation(-(float)calc.DestinationRect.X, -(float)calc.DestinationRect.Y)
                    : SKMatrix.CreateIdentity();

            SKShaderTileMode tileX =
                tileBrush.TileMode == TileMode.None
                    ? SKShaderTileMode.Clamp
                    : tileBrush.TileMode == TileMode.FlipX || tileBrush.TileMode == TileMode.FlipXY
                        ? SKShaderTileMode.Mirror
                        : SKShaderTileMode.Repeat;

            SKShaderTileMode tileY =
                tileBrush.TileMode == TileMode.None
                    ? SKShaderTileMode.Clamp
                    : tileBrush.TileMode == TileMode.FlipY || tileBrush.TileMode == TileMode.FlipXY
                        ? SKShaderTileMode.Mirror
                        : SKShaderTileMode.Repeat;


            var image = intermediate.SnapshotImage();
            paintWrapper.AddDisposable(image);

            var paintTransform = default(SKMatrix);

            SKMatrix.Concat(
                ref paintTransform,
                tileTransform,
                SKMatrix.CreateScale((float)(96.0 / _dpi.X), (float)(96.0 / _dpi.Y)));

            if (tileBrush.Transform is { })
            {
                var origin = tileBrush.TransformOrigin.ToPixels(targetSize);
                var offset = Matrix.CreateTranslation(origin);
                var transform = (-offset) * tileBrush.Transform.Value * (offset);

                paintTransform = paintTransform.PreConcat(transform.ToSKMatrix());
            }

            using (var shader = image.ToShader(tileX, tileY, paintTransform))
            {
                paintWrapper.Paint.Shader = shader;
            }
        }

        /// <summary>
        /// Configure paint wrapper to use visual brush.
        /// </summary>
        /// <param name="paintWrapper">Paint wrapper.</param>
        /// <param name="visualBrush">Visual brush.</param>
        /// <param name="visualBrushRenderer">Visual brush renderer.</param>
        /// <param name="tileBrushImage">Tile brush image.</param>
        private void ConfigureVisualBrush(ref PaintWrapper paintWrapper, IVisualBrush visualBrush, IVisualBrushRenderer visualBrushRenderer, ref IDrawableBitmapImpl tileBrushImage)
        {
            if (_visualBrushRenderer == null)
            {
                throw new NotSupportedException("No IVisualBrushRenderer was supplied to DrawingContextImpl.");
            }

            var intermediateSize = visualBrushRenderer.GetRenderTargetSize(visualBrush);

            if (intermediateSize.Width >= 1 && intermediateSize.Height >= 1)
            {
                var intermediate = CreateRenderTarget(intermediateSize, false);

                using (var ctx = intermediate.CreateDrawingContext(visualBrushRenderer))
                {
                    ctx.Clear(Colors.Transparent);

                    visualBrushRenderer.RenderVisualBrush(ctx, visualBrush);
                }

                tileBrushImage = intermediate;
                paintWrapper.AddDisposable(tileBrushImage);
            }
        }

        static SKColorFilter CreateAlphaColorFilter(double opacity)
        {
            if (opacity > 1)
                opacity = 1;
            var c = new byte[256];
            var a = new byte[256];
            for (var i = 0; i < 256; i++)
            {
                c[i] = (byte)i;
                a[i] = (byte)(i * opacity);
            }

            return SKColorFilter.CreateTable(a, c, c, c);
        }

        static byte Blend(byte leftColor, byte leftAlpha, byte rightColor, byte rightAlpha)
        {
            var ca = leftColor / 255d;
            var aa = leftAlpha / 255d;
            var cb = rightColor / 255d;
            var ab = rightAlpha / 255d;
            var r = (ca * aa + cb * ab * (1 - aa)) / (aa + ab * (1 - aa));
            return (byte)(r * 255);
        }

        static Color Blend(Color left, Color right)
        {
            var aa = left.A / 255d;
            var ab = right.A / 255d;
            return new Color(
                (byte)((aa + ab * (1 - aa)) * 255),
                Blend(left.R, left.A, right.R, right.A),
                Blend(left.G, left.A, right.G, right.A),
                Blend(left.B, left.A, right.B, right.A)                
            );
        }

        internal PaintWrapper CreateAcrylicPaint (SKPaint paint, IExperimentalAcrylicMaterial material)
        {
            var paintWrapper = new PaintWrapper(paint);

            paint.IsAntialias = true;

            double opacity = _currentOpacity;

            var tintOpacity =
                material.BackgroundSource == AcrylicBackgroundSource.Digger ?
                material.TintOpacity : 1;

            const double noiseOpcity = 0.0225;

            var tintColor = material.TintColor;
            var tint = new SKColor(tintColor.R, tintColor.G, tintColor.B, tintColor.A);

            if (s_acrylicNoiseShader == null)
            {
                using (var stream = typeof(DrawingContextImpl).Assembly.GetManifestResourceStream("Avalonia.Skia.Assets.NoiseAsset_256X256_PNG.png"))
                using (var bitmap = SKBitmap.Decode(stream))
                {
                    s_acrylicNoiseShader = SKShader.CreateBitmap(bitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat)
                        .WithColorFilter(CreateAlphaColorFilter(noiseOpcity));
                }
            }

            using (var backdrop = SKShader.CreateColor(new SKColor(material.MaterialColor.R, material.MaterialColor.G, material.MaterialColor.B, material.MaterialColor.A)))
            using (var tintShader = SKShader.CreateColor(tint))
            using (var effectiveTint = SKShader.CreateCompose(backdrop, tintShader))
            using (var compose = SKShader.CreateCompose(effectiveTint, s_acrylicNoiseShader))
            {
                paint.Shader = compose;

                if (material.BackgroundSource == AcrylicBackgroundSource.Digger)
                {
                    paint.BlendMode = SKBlendMode.Src;
                }

                return paintWrapper;
            }
        }

        /// <summary>
        /// Creates paint wrapper for given brush.
        /// </summary>
        /// <param name="paint">The paint to wrap.</param>
        /// <param name="brush">Source brush.</param>
        /// <param name="targetSize">Target size.</param>
        /// <returns>Paint wrapper for given brush.</returns>
        internal PaintWrapper CreatePaint(SKPaint paint, IBrush brush, Size targetSize)
        {
            var paintWrapper = new PaintWrapper(paint);

            paint.IsAntialias = true;

            double opacity = brush.Opacity * _currentOpacity;

            if (brush is ISolidColorBrush solid)
            {
                paint.Color = new SKColor(solid.Color.R, solid.Color.G, solid.Color.B, (byte) (solid.Color.A * opacity));

                return paintWrapper;
            }

            paint.Color = new SKColor(255, 255, 255, (byte) (255 * opacity));

            if (brush is IGradientBrush gradient)
            {
                ConfigureGradientBrush(ref paintWrapper, targetSize, gradient);

                return paintWrapper;
            }

            var tileBrush = brush as ITileBrush;
            var visualBrush = brush as IVisualBrush;
            var tileBrushImage = default(IDrawableBitmapImpl);

            if (visualBrush != null)
            {
                ConfigureVisualBrush(ref paintWrapper, visualBrush, _visualBrushRenderer, ref tileBrushImage);
            }
            else
            {
                tileBrushImage = (IDrawableBitmapImpl)(tileBrush as IImageBrush)?.Source?.PlatformImpl.Item;
            }

            if (tileBrush != null && tileBrushImage != null)
            {
                ConfigureTileBrush(ref paintWrapper, targetSize, tileBrush, tileBrushImage);
            }
            else
            {
                paint.Color = new SKColor(255, 255, 255, 0);
            }

            return paintWrapper;
        }

        /// <summary>
        /// Creates paint wrapper for given pen.
        /// </summary>
        /// <param name="paint">The paint to wrap.</param>
        /// <param name="pen">Source pen.</param>
        /// <param name="targetSize">Target size.</param>
        /// <returns></returns>
        private PaintWrapper CreatePaint(SKPaint paint, IPen pen, Size targetSize)
        {
            // In Skia 0 thickness means - use hairline rendering
            // and for us it means - there is nothing rendered.
            if (pen.Thickness == 0d)
            {
                return default;
            }

            var rv = CreatePaint(paint, pen.Brush, targetSize);

            paint.IsStroke = true;
            paint.StrokeWidth = (float) pen.Thickness;

            // Need to modify dashes due to Skia modifying their lengths
            // https://docs.microsoft.com/en-us/xamarin/xamarin-forms/user-interface/graphics/skiasharp/paths/dots
            // TODO: Still something is off, dashes are now present, but don't look the same as D2D ones.

            switch (pen.LineCap)
            {
                case PenLineCap.Round:
                    paint.StrokeCap = SKStrokeCap.Round;
                    break;
                case PenLineCap.Square:
                    paint.StrokeCap = SKStrokeCap.Square;
                    break;
                default:
                    paint.StrokeCap = SKStrokeCap.Butt;
                    break;
            }

            switch (pen.LineJoin)
            {
                case PenLineJoin.Miter:
                    paint.StrokeJoin = SKStrokeJoin.Miter;
                    break;
                case PenLineJoin.Round:
                    paint.StrokeJoin = SKStrokeJoin.Round;
                    break;
                default:
                    paint.StrokeJoin = SKStrokeJoin.Bevel;
                    break;
            }

            paint.StrokeMiter = (float) pen.MiterLimit;

            if (pen.DashStyle?.Dashes != null && pen.DashStyle.Dashes.Count > 0)
            {
                var srcDashes = pen.DashStyle.Dashes;
                var dashesArray = new float[srcDashes.Count];

                for (var i = 0; i < srcDashes.Count; ++i)
                {
                    dashesArray[i] = (float) srcDashes[i] * paint.StrokeWidth;
                }

                var offset = (float)(pen.DashStyle.Offset * pen.Thickness);

                var pe = SKPathEffect.CreateDash(dashesArray, offset);

                paint.PathEffect = pe;
                rv.AddDisposable(pe);
            }

            return rv;
        }

        /// <summary>
        /// Create new render target compatible with this drawing context.
        /// </summary>
        /// <param name="size">The size of the render target in DIPs.</param>
        /// <param name="isLayer">Whether the render target is being created for a layer.</param>
        /// <param name="format">Pixel format.</param>
        /// <returns></returns>
        private SurfaceRenderTarget CreateRenderTarget(Size size, bool isLayer, PixelFormat? format = null)
        {
            var pixelSize = PixelSize.FromSizeWithDpi(size, _dpi);
            var createInfo = new SurfaceRenderTarget.CreateInfo
            {
                Width = pixelSize.Width,
                Height = pixelSize.Height,
                Dpi = _dpi,
                Format = format,
                DisableTextLcdRendering = !_canTextUseLcdRendering,
                GrContext = _grContext,
                Gpu = _gpu,
                Session = _session,
                DisableManualFbo = !isLayer,
            };

            return new SurfaceRenderTarget(createInfo);
        }        

        /// <summary>
        /// Skia cached paint state.
        /// </summary>
        private readonly struct PaintState : IDisposable
        {
            private readonly SKColor _color;
            private readonly SKShader _shader;
            private readonly SKPaint _paint;
            
            public PaintState(SKPaint paint, SKColor color, SKShader shader)
            {
                _paint = paint;
                _color = color;
                _shader = shader;
            }

            /// <inheritdoc />
            public void Dispose()
            {
                _paint.Color = _color;
                _paint.Shader = _shader;
            }
        }

        /// <summary>
        /// Skia paint wrapper.
        /// </summary>
        internal struct PaintWrapper : IDisposable
        {
            //We are saving memory allocations there
            public readonly SKPaint Paint;

            private IDisposable _disposable1;
            private IDisposable _disposable2;
            private IDisposable _disposable3;

            public PaintWrapper(SKPaint paint)
            {
                Paint = paint;

                _disposable1 = null;
                _disposable2 = null;
                _disposable3 = null;
            }

            public IDisposable ApplyTo(SKPaint paint)
            {
                var state = new PaintState(paint, paint.Color, paint.Shader);

                paint.Color = Paint.Color;
                paint.Shader = Paint.Shader;

                return state;
            }

            /// <summary>
            /// Add new disposable to a wrapper.
            /// </summary>
            /// <param name="disposable">Disposable to add.</param>
            public void AddDisposable(IDisposable disposable)
            {
                if (_disposable1 == null)
                {
                    _disposable1 = disposable;
                }
                else if (_disposable2 == null)
                {
                    _disposable2 = disposable;
                }
                else if (_disposable3 == null)
                {
                    _disposable3 = disposable;
                }
                else
                {
                    Debug.Assert(false);

                    // ReSharper disable once HeuristicUnreachableCode
                    throw new InvalidOperationException(
                        "PaintWrapper disposable object limit reached. You need to add extra struct fields to support more disposables.");
                }
            }
            
            /// <inheritdoc />
            public void Dispose()
            {
                Paint?.Reset();
                _disposable1?.Dispose();
                _disposable2?.Dispose();
                _disposable3?.Dispose();
            }
        }
    }
}
