using System;
using System.IO;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Internal;
using Avalonia.Skia.Helpers;
using Avalonia.Utilities;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Skia based writeable bitmap.
    /// </summary>
    internal class WriteableBitmapImpl : IWriteableBitmapImpl, IDrawableBitmapImpl
    {
        private static readonly SKBitmapReleaseDelegate s_releaseDelegate = ReleaseProc;
        private readonly SKBitmap _bitmap;
        private readonly SKImage _image;
        private readonly SKPixmap _pixmap;
        private readonly object _lock = new();
        
        /// <summary>
        /// Create a WriteableBitmap from given stream.
        /// </summary>
        /// <param name="stream">Stream containing encoded data.</param>
        public WriteableBitmapImpl(Stream stream)
        {
            using (var skiaStream = new SKManagedStream(stream))
            using (var skData = SKData.Create(skiaStream))
            {
                _bitmap = SKBitmap.Decode(skData);

                if (_bitmap == null)
                {
                    throw new ArgumentException("Unable to load bitmap from provided data");
                }

                var info = _bitmap.Info;
                _pixmap = new SKPixmap(info, _bitmap.GetPixels(), info.RowBytes);
                _image = SKImage.FromPixels(_pixmap, null);
                PixelSize = new PixelSize(info.Width, info.Height);
                Dpi = SkiaPlatform.DefaultDpi;
            }
        }

        public WriteableBitmapImpl(Stream stream, int decodeSize, bool horizontal, BitmapInterpolationMode interpolationMode)
        {
            using (var skStream = new SKManagedStream(stream))
            using (var skData = SKData.Create(skStream))
            using (var codec = SKCodec.Create(skData))
            {
                var info = codec.Info;

                // get the scale that is nearest to what we want (eg: jpg returned 512)
                var supportedScale = codec.GetScaledDimensions(horizontal ? ((float)decodeSize / info.Width) : ((float)decodeSize / info.Height));

                // decode the bitmap at the nearest size
                var nearest = new SKImageInfo(supportedScale.Width, supportedScale.Height);
                var bmp = SKBitmap.Decode(codec, nearest);

                // now scale that to the size that we want
                var realScale = horizontal ? ((double)info.Height / info.Width) : ((double)info.Width / info.Height);

                SKImageInfo desired;


                if (horizontal)
                {
                    desired = new SKImageInfo(decodeSize, (int)(realScale * decodeSize));
                }
                else
                {
                    desired = new SKImageInfo((int)(realScale * decodeSize), decodeSize);
                }

                if (bmp.Width != desired.Width || bmp.Height != desired.Height)
                {
                    var scaledBmp = bmp.Resize(desired, interpolationMode.ToSKFilterQuality());
                    bmp.Dispose();
                    bmp = scaledBmp;
                }

                _bitmap = bmp;
                _pixmap = new SKPixmap(info, _bitmap.GetPixels(), info.RowBytes);
                _image = SKImage.FromPixels(_pixmap, null);

                PixelSize = new PixelSize(bmp.Width, bmp.Height);
                Dpi = SkiaPlatform.DefaultDpi;
            }
        }
        
        /// <summary>
        /// Create new writeable bitmap.
        /// </summary>
        /// <param name="size">The size of the bitmap in device pixels.</param>
        /// <param name="dpi">The DPI of the bitmap.</param>
        /// <param name="format">The pixel format.</param>
        /// <param name="alphaFormat">The alpha format.</param>
        public WriteableBitmapImpl(PixelSize size, Vector dpi, PixelFormat format, AlphaFormat alphaFormat)
        {
            PixelSize = size;
            Dpi = dpi;

            SKColorType colorType = format.ToSkColorType();
            SKAlphaType alphaType = alphaFormat.ToSkAlphaType();

            _bitmap = new SKBitmap();

            var nfo = new SKImageInfo(size.Width, size.Height, colorType, alphaType);
            var blob = new UnmanagedBlob(nfo.BytesSize);
            var addr = blob.Address;

            _bitmap.InstallPixels(nfo, addr, nfo.RowBytes, s_releaseDelegate, blob);
            _bitmap.Erase(SKColor.Empty);
            
            _pixmap = new SKPixmap(nfo, addr, nfo.RowBytes);
            _image = SKImage.FromPixels(_pixmap);
        }

        public Vector Dpi { get; }

        /// <inheritdoc />
        public PixelSize PixelSize { get; }

        public int Version { get; private set; } = 1;

        /// <inheritdoc />
        public void Draw(DrawingContextImpl context, SKRect sourceRect, SKRect destRect, SKPaint paint)
        {
            lock (_lock)
            {
                if (sourceRect == destRect &&
                    MathUtilities.IsZero(sourceRect.Left) && 
                    MathUtilities.IsZero(sourceRect.Top) && 
                    // I'm using Right and Bottom since they are effectively the Width
                    // and Height since Left and Top will be zero at this point
                    MathUtilities.AreClose(sourceRect.Right, PixelSize.Width) && 
                    MathUtilities.AreClose(sourceRect.Bottom, PixelSize.Height))
                {
                    context.Canvas.DrawImage(_image, 0, 0, paint);
                }
                else
                {
                    context.Canvas.DrawImage(_image, sourceRect, destRect, paint);
                }
            }
        }
        
        /// <inheritdoc />
        public virtual void Dispose()
        {
            _bitmap.Dispose();
            _pixmap.Dispose();
            _image.Dispose();
        }

        /// <inheritdoc />
        public void Save(Stream stream, int? quality = null)
        {
            using (var image = GetSnapshot())
            {
                ImageSavingHelper.SaveImage(image, stream, quality);
            }
        }

        /// <inheritdoc />
        public void Save(string fileName, int? quality = null)
        {
            using (var image = GetSnapshot())
            {
                ImageSavingHelper.SaveImage(image, fileName, quality);
            }
        }

        public PixelFormat? Format => _bitmap.ColorType.ToAvalonia();

        public AlphaFormat? AlphaFormat => _bitmap.AlphaType.ToAlphaFormat();

        /// <inheritdoc />
        public ILockedFramebuffer Lock() => new BitmapFramebuffer(this, _bitmap);

        /// <summary>
        /// Get snapshot as image.
        /// </summary>
        /// <returns>Image snapshot.</returns>
        public SKImage GetSnapshot()
        {
            lock (_lock)
                return SKImage.FromPixels(_bitmap.Info, _bitmap.GetPixels(), _bitmap.RowBytes);
        }

        /// <summary>
        /// Release given unmanaged blob.
        /// </summary>
        /// <param name="address">Blob address.</param>
        /// <param name="ctx">Blob.</param>
        private static void ReleaseProc(IntPtr address, object ctx)
        {
            ((UnmanagedBlob)ctx).Dispose();
        }

        /// <summary>
        /// Framebuffer for bitmap.
        /// </summary>
        private class BitmapFramebuffer : ILockedFramebuffer
        {
            private WriteableBitmapImpl _parent;
            private SKBitmap _bitmap;

            /// <summary>
            /// Create framebuffer from given bitmap.
            /// </summary>
            /// <param name="parent">Parent bitmap impl.</param>
            /// <param name="bitmap">Bitmap.</param>
            public BitmapFramebuffer(WriteableBitmapImpl parent, SKBitmap bitmap)
            {
                _parent = parent;
                _bitmap = bitmap;
                Monitor.Enter(parent._lock);
            }

            /// <inheritdoc />
            public void Dispose()
            {
                _bitmap.NotifyPixelsChanged();
                _parent.Version++;
                Monitor.Exit(_parent._lock);
                _bitmap = null!;
                _parent = null!;
            }
            
            /// <inheritdoc />
            public IntPtr Address => _bitmap.GetPixels();

            /// <inheritdoc />
            public PixelSize Size => new PixelSize(_bitmap.Width, _bitmap.Height);

            /// <inheritdoc />
            public int RowBytes => _bitmap.RowBytes;

            /// <inheritdoc />
            public Vector Dpi => _parent.Dpi;
            /// <inheritdoc />
            public PixelFormat Format => _bitmap.ColorType.ToPixelFormat();
        }
    }
}
