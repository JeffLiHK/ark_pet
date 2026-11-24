using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArtPet.Windows.Media;

internal sealed class FrameTransition
{
    private readonly WriteableBitmap _bitmap;
    private readonly Int32Rect _bounds;
    private readonly int _stride;
    private readonly byte[] _incomingPixels;
    private readonly byte[] _outgoingPixels;
    private readonly byte[] _blendedPixels;
    private BitmapSource? _incomingFrame;
    private long _started;
    private double _durationSeconds;

    internal FrameTransition(BitmapSource frame)
    {
        // CharacterPack decodes every clip in an outfit at the same canvas size.
        _bounds = new Int32Rect(0, 0, frame.PixelWidth, frame.PixelHeight);
        _stride = frame.PixelWidth * 4;
        int byteCount = _stride * frame.PixelHeight;
        _outgoingPixels = new byte[byteCount];
        _incomingPixels = new byte[byteCount];
        _blendedPixels = new byte[byteCount];
        _bitmap = new WriteableBitmap(frame.PixelWidth, frame.PixelHeight, frame.DpiX, frame.DpiY,
            PixelFormats.Pbgra32, null);
    }

    internal bool IsActive => _durationSeconds > 0;

    internal void Begin(BitmapSource outgoing, long now, double durationSeconds)
    {
        if (ReferenceEquals(outgoing, _bitmap))
        {
            // An interruption starts from the last blended pose on screen.
            Array.Copy(_blendedPixels, _outgoingPixels, _blendedPixels.Length);
        }
        else
        {
            CopyPixels(outgoing, _outgoingPixels);
        }

        _incomingFrame = null;
        _started = now;
        _durationSeconds = durationSeconds;
    }

    internal BitmapSource Blend(BitmapSource incoming, long now)
    {
        double progress = Stopwatch.GetElapsedTime(_started, now).TotalSeconds / _durationSeconds;
        if (progress >= 1)
        {
            _durationSeconds = 0;
            _incomingFrame = null;
            return incoming;
        }

        if (!ReferenceEquals(incoming, _incomingFrame))
        {
            CopyPixels(incoming, _incomingPixels);
            _incomingFrame = incoming;
        }

        double eased = progress * progress * (3 - 2 * progress);
        int incomingWeight = (int)Math.Round(eased * 256);
        int outgoingWeight = 256 - incomingWeight;

        // Blend premultiplied color and alpha together so overlapping opaque pixels stay opaque.
        for (int index = 0; index < _blendedPixels.Length; index++)
        {
            int sum = _outgoingPixels[index] * outgoingWeight + _incomingPixels[index] * incomingWeight;
            _blendedPixels[index] = (byte)((sum + 128) >> 8);
        }

        _bitmap.WritePixels(_bounds, _blendedPixels, _stride, 0);
        return _bitmap;
    }

    private void CopyPixels(BitmapSource frame, byte[] destination)
    {
        if (frame.Format != PixelFormats.Pbgra32)
        {
            frame = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        }

        frame.CopyPixels(destination, _stride, 0);
    }
}
