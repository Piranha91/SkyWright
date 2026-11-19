using System.Numerics;

namespace RunControl;

/// <summary>datetime.fromisoformat as CPython 3.12 reads it (its C parser, step for step), for the two timestamps an
/// import compares: the instant, and whether the text names a time zone. Null where Python raises ValueError.</summary>
public static class PyDateTime
{
    /// <summary>An instant in microseconds, and whether the text carried a time zone.</summary>
    public readonly record struct Stamp(BigInteger Microseconds, bool Aware);

    static bool Digit(char c) => c is >= '0' and <= '9';

    // The text as the C parser sees it: a NUL after its last character.
    sealed class Text(string s)
    {
        public int Length => s.Length;
        public char this[int i] => i >= 0 && i < s.Length ? s[i] : '\0';
    }

    // parse_digits: `count` ASCII digits at pos added onto value; false when one is not a digit.
    static bool Digits(Text t, ref int pos, ref int value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            char c = t[pos++];
            if (!Digit(c)) return false;
            value = value * 10 + (c - '0');
        }
        return true;
    }

    // Where the date ends: 7, 8 or 10 characters in; -1 where the C parser gives up.
    static int Separator(Text s)
    {
        int len = s.Length;
        if (len == 7) return 7;
        if (s[4] == '-')
        {
            if (s[5] != 'W') return 10;
            if (len < 8) return -1;
            if (len > 8 && s[8] == '-')
            {
                if (len == 9) return -1;
                return len > 10 && Digit(s[10]) ? 8 : 10;
            }
            return 8;
        }
        if (s[4] != 'W') return 8;
        int idx = 7;
        while (idx < len && Digit(s[idx])) idx++;
        if (idx < 9) return idx;
        return idx % 2 == 0 ? 7 : 8;
    }

    static long Ordinal(int year, int month, int day) => new DateTime(year, month, day).Ticks / TimeSpan.TicksPerDay + 1;

    // parse_isoformat_date and the date's own checks: the proleptic ordinal (1 = 0001-01-01), or null.
    static long? Date(Text d, int len)
    {
        int pos = 0, year = 0, month = 0, day = 0;
        if (!Digits(d, ref pos, ref year, 4)) return null;
        bool sep = d[pos] == '-';
        if (sep) pos++;
        if (d[pos] == 'W')
        {
            pos++;
            int week = 0, dayNo = 0;
            if (!Digits(d, ref pos, ref week, 2)) return null;
            if (pos < len)
            {
                if (sep && d[pos++] != '-') return null;
                if (!Digits(d, ref pos, ref dayNo, 1)) return null;
            }
            else dayNo = 1;
            if (year is < 1 or > 9999) return null;
            long first = Ordinal(year, 1, 1);
            long firstWeekday = (first + 6) % 7;   // Monday is 0
            if (week is <= 0 or >= 53)
            {
                bool long53 = week == 53 && (firstWeekday == 3 || (firstWeekday == 2 && DateTime.IsLeapYear(year)));
                if (!long53) return null;
            }
            if (dayNo is <= 0 or >= 8) return null;
            long week1Monday = first - firstWeekday + (firstWeekday > 3 ? 7 : 0);
            long ord = week1Monday + (week - 1) * 7 + (dayNo - 1);
            return ord < 1 || ord > Ordinal(9999, 12, 31) ? null : ord;
        }
        if (!Digits(d, ref pos, ref month, 2)) return null;
        if (sep && d[pos++] != '-') return null;
        if (!Digits(d, ref pos, ref day, 2)) return null;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return Ordinal(year, month, day);
    }

    // parse_hh_mm_ss_ff over [start, end): -1 an error, 0 read to the text's end, 1 something follows.
    static int Clock(Text t, int start, int end, int[] vals)
    {
        int p = start;
        bool sep = true;
        for (int i = 0; i < 3; i++)
        {
            if (!Digits(t, ref p, ref vals[i], 2)) return -1;
            char c = t[p++];
            if (i == 0) sep = c == ':';
            if (p >= end) return c != '\0' ? 1 : 0;
            if (sep && c == ':') continue;
            if (c is '.' or ',') break;
            if (!sep) { p--; continue; }
            return -1;
        }
        int take = Math.Min(end - p, 6);
        if (!Digits(t, ref p, ref vals[3], take)) return -1;
        for (int i = take; i < 6; i++) vals[3] *= 10;
        while (Digit(t[p])) p++;
        return t[p] != '\0' ? 1 : 0;
    }

    /// <summary>datetime.fromisoformat(text): the instant and whether it is zone-aware, or null for a ValueError.</summary>
    public static Stamp? FromIso(string text)
    {
        if (text.Length < 7) return null;
        var t = new Text(text);
        int at = Separator(t);
        if (at < 0) return null;
        if (Date(t, at) is not { } ordinal) return null;
        var clock = new int[4];
        BigInteger? offset = null;
        if (text.Length > at)
        {
            // one character separates the date from the time, whatever it is
            int start = at + (char.IsHighSurrogate(t[at]) ? 2 : 1), end = text.Length;
            int tz = start;
            do
            {
                if (t[tz] is 'Z' or '+' or '-') break;
            } while (++tz < end);
            int rv = Clock(t, start, tz, clock);
            if (rv < 0) return null;
            if (tz == end)
            {
                if (rv == 1) return null;
            }
            else if (t[tz] == 'Z')
            {
                if (t[tz + 1] != '\0') return null;
                offset = 0;
            }
            else
            {
                var z = new int[4];
                if (Clock(t, tz + 1, end, z) != 0) return null;
                BigInteger us = ((BigInteger)z[0] * 3600 + z[1] * 60 + z[2]) * 1_000_000 + z[3];
                if (us >= (BigInteger)86400 * 1_000_000) return null;   // an offset must be under a day
                offset = t[tz] == '-' ? -us : us;
            }
        }
        if (clock[0] > 23 || clock[1] > 59 || clock[2] > 59) return null;
        BigInteger local = (((BigInteger)ordinal * 24 + clock[0]) * 60 + clock[1]) * 60 + clock[2];
        local = local * 1_000_000 + clock[3];
        return new Stamp(local - (offset ?? 0), offset != null);
    }
}
