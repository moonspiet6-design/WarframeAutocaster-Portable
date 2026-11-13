using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Autocaster.Core;
using Autocaster.Windows;

namespace Autocaster.App;

internal sealed class IndexTarget(Func<int>? readWaveSize = null) : LarvaTarget(readWaveSize), IIndexTarget
{
    private IndexVoteLayout? _lastVote;
    private Point? _choicePoint;

    public IndexScreen ReadIndexScreen() => TryGetClientArea(out int x, out int y, out int width, out int height)
        ? ReadFrame(x, y, width, height).Screen : IndexScreen.Unavailable;

    public bool TryPointAtIndexChoice(bool extract)
    {
        _choicePoint = null;
        if (!TryGetClientArea(out int x, out int y, out int width, out int height) ||
            ReadFrame(x, y, width, height) is not { Screen: IndexScreen.Vote } vote)
            return false;

        int center = extract ? 552 : 1370;
        foreach (int row in new[] { 490, 600, 380 })
        foreach (int column in new[] { center, center - 120, center + 120 })
        {
            var point = new Point(x + Scale(vote.VoteLayout.X(column), width, IndexScreenDetector.ReferenceWidth),
                y + Scale(vote.VoteLayout.Y(row), height, IndexScreenDetector.ReferenceHeight));
            if (TryMovePointer(point.X, point.Y))
            {
                _choicePoint = point;
                return true;
            }
        }
        return false;
    }

    public bool IsPointingAtIndexChoice() => _choicePoint is { } point && PointerAt(point.X, point.Y);

    private IndexFrame ReadFrame(int x, int y, int width, int height)
    {
        if (width < 640 || height < 360)
            return new(IndexScreen.Unavailable);
        try
        {
            using var frame = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(frame))
                graphics.CopyFromScreen(x, y, 0, 0, frame.Size);

            var bits = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, frame.PixelFormat);
            try
            {
                var result = IndexScreenDetector.Inspect((px, py) =>
                    Marshal.ReadInt32(bits.Scan0, bits.Stride * Scale(py, height, IndexScreenDetector.ReferenceHeight) +
                        sizeof(int) * Scale(px, width, IndexScreenDetector.ReferenceWidth)), _lastVote);
                if (result.Screen == IndexScreen.Vote)
                    _lastVote = result.VoteLayout;
                return result;
            }
            finally
            {
                frame.UnlockBits(bits);
            }
        }
        catch (Exception error) when (error is Win32Exception or ExternalException)
        {
            return new(IndexScreen.Unavailable);
        }
    }

    private static int Scale(int coordinate, int length, int reference) => (coordinate * length + reference / 2) / reference;
}
