using System;
using System.Globalization;
using Autocaster.Core;

namespace Autocaster.App;

internal static class TimingUnits
{
    internal const int MillisecondsPerHour = 3_600_000;

    internal static string Format(int ticks, int ticksPerSecond = 1000) =>
        ((decimal)ticks / ticksPerSecond).ToString("0.######", CultureInfo.InvariantCulture);

    internal static string FormatCountdown(double milliseconds) =>
        (Math.Ceiling(Math.Max(0, milliseconds) / 100) / 10).ToString("0.0", CultureInfo.InvariantCulture);

    internal static string FormatHours(int milliseconds) =>
        ((decimal)milliseconds / MillisecondsPerHour).ToString("0.#######", CultureInfo.InvariantCulture);

    internal static string Format(Timing timing, bool hours = false)
    {
        string Value(int ms) => hours ? FormatHours(ms) : Format(ms);
        string value = timing.Random ? $"{Value(timing.MinMs)}–{Value(timing.MaxMs)}" : Value(timing.FixedMs);
        return $"{value} {(hours ? "h" : "s")}";
    }

    internal static bool TryRead(string text, int min, int max, int ticksPerUnit, out int ticks, bool round = false)
    {
        ticks = 0;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite |
            NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out decimal duration) ||
            duration < (decimal)min / ticksPerUnit || duration > (decimal)max / ticksPerUnit)
            return false;
        decimal value = duration * ticksPerUnit;
        if (round)
            value = decimal.Round(value, MidpointRounding.AwayFromZero);
        if (value != decimal.Truncate(value))
            return false;
        ticks = (int)value;
        return true;
    }
}
