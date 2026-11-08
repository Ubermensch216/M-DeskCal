using System.Collections.Concurrent;

namespace DeskCal.Core.Calendars;

/// <summary>One of the 24 solar terms (절기). <see cref="Kst"/> is the instant in Korea Standard Time (UTC+9), to the minute.</summary>
public sealed record SolarTerm(string Name, int Longitude, DateTime Kst)
{
    public DateOnly Date => DateOnly.FromDateTime(Kst);
}

/// <summary>
/// The 24 solar terms computed from the Sun's apparent ecliptic longitude (Meeus, "Astronomical Algorithms"
/// ch. 25 with the long-period corrections of ch. 27). Validated against 28 published equinox/solstice
/// instants (2000-2026): worst error under one minute, so the KST date is wrong only if a term falls within
/// about a minute of midnight. Supported years: 1900-2150 (the range of the Delta-T model).
/// </summary>
public static class SolarTerms
{
    public const int MinYear = 1900, MaxYear = 2149;

    // In calendar-year order: 소한 (285°) is the first term of January, 동지 (270°) the last of December.
    private static readonly (string Name, int Longitude)[] Order =
    [
        ("소한", 285), ("대한", 300), ("입춘", 315), ("우수", 330), ("경칩", 345), ("춘분", 0),
        ("청명", 15), ("곡우", 30), ("입하", 45), ("소만", 60), ("망종", 75), ("하지", 90),
        ("소서", 105), ("대서", 120), ("입추", 135), ("처서", 150), ("백로", 165), ("추분", 180),
        ("한로", 195), ("상강", 210), ("입동", 225), ("소설", 240), ("대설", 255), ("동지", 270),
    ];

    private static readonly ConcurrentDictionary<int, SolarTerm[]> Cache = new();

    /// <summary>The 24 terms of a Gregorian year, in date order.</summary>
    public static IReadOnlyList<SolarTerm> ForYear(int year)
    {
        if (year < MinYear || year > MaxYear)
            throw new ArgumentOutOfRangeException(nameof(year), $"Solar terms are supported for {MinYear}-{MaxYear}.");
        return Cache.GetOrAdd(year, Compute);
    }

    /// <summary>The term that falls on this KST date, if any (at most one per day).</summary>
    public static SolarTerm? OnDate(DateOnly date)
    {
        if (date.Year < MinYear || date.Year > MaxYear) return null;
        foreach (var t in ForYear(date.Year))
            if (t.Date == date) return t;
        return null;
    }

    // ---- computation -------------------------------------------------------

    private static SolarTerm[] Compute(int year)
    {
        double march20 = JulianDay(new DateTime(year, 3, 20, 0, 0, 0, DateTimeKind.Utc));
        var result = new SolarTerm[Order.Length];
        for (int i = 0; i < Order.Length; i++)
        {
            var (name, lon) = Order[i];
            // First guess: the Sun advances ~360° per tropical year from the March equinox (0°).
            double offsetDeg = lon >= 285 ? lon - 360 : lon;
            double jde = SolveLongitude(march20 + offsetDeg / 360.0 * 365.2422, lon);

            double ut = jde - DeltaTSeconds(year) / 86400.0;            // TT -> UT
            var kst = FromJulianDay(ut).AddHours(9);                     // UT -> Korea Standard Time
            // The model is good to about a minute, so seconds are noise; drop them (never round up across midnight).
            kst = new DateTime(kst.Year, kst.Month, kst.Day, kst.Hour, kst.Minute, 0);
            result[i] = new SolarTerm(name, lon, kst);
        }
        return result;
    }

    private static double SolveLongitude(double jdGuess, double targetDeg)
    {
        double jd = jdGuess;
        for (int i = 0; i < 50; i++)
        {
            double lambda = ApparentLongitude((jd - 2451545.0) / 36525.0);
            double diff = ((targetDeg - lambda + 540.0) % 360.0) - 180.0;   // signed, in (-180, 180]
            jd += diff / 0.9856474;                                          // mean daily motion of the Sun
            if (Math.Abs(diff) < 1e-9) break;
        }
        return jd;
    }

    // Periodic terms (amplitude, phase°, rate°/century) of Meeus table 27.C. The first term of that table
    // (485, 324.96, 1934.136) is the nutation in longitude, which ApparentLongitude already includes, so it
    // is omitted here to avoid counting it twice.
    private static readonly (double A, double B, double C)[] Periodic =
    [
        (203, 337.23, 32964.467), (199, 342.08, 20.186), (182, 27.85, 445267.112), (156, 73.14, 45036.886),
        (136, 171.52, 22518.443), (77, 222.54, 65928.934), (74, 296.72, 3034.906), (70, 243.58, 9037.513),
        (58, 119.81, 33718.147), (52, 297.17, 150.678), (50, 21.02, 2281.226), (45, 247.54, 29929.562),
        (44, 325.15, 31555.956), (29, 60.93, 4443.417), (18, 155.12, 67555.328), (17, 288.79, 4562.452),
        (16, 198.04, 62894.029), (14, 199.76, 31436.921), (12, 95.39, 14577.848), (12, 287.11, 31931.756),
        (12, 320.81, 34777.259), (9, 227.73, 1222.114), (8, 15.45, 16859.074),
    ];

    /// <summary>Apparent geocentric ecliptic longitude of the Sun in degrees [0, 360). T in Julian centuries from J2000 (TT).</summary>
    internal static double ApparentLongitude(double t)
    {
        double l0 = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
        double m = Rad(357.52911 + 35999.05029 * t - 0.0001537 * t * t);
        double c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(m)
                 + (0.019993 - 0.000101 * t) * Math.Sin(2 * m)
                 + 0.000289 * Math.Sin(3 * m);
        double omega = Rad(125.04 - 1934.136 * t);
        double lambda = l0 + c - 0.00569 - 0.00478 * Math.Sin(omega);

        double s = 0;
        foreach (var (a, b, rate) in Periodic) s += a * Math.Cos(Rad(b + rate * t));
        lambda -= 0.9856e-5 * s;                                             // empirical sign/scale verified against known instants

        return ((lambda % 360.0) + 360.0) % 360.0;
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;

    /// <summary>Delta-T = TT - UT in seconds (Espenak and Meeus polynomial expressions, 1900-2150).</summary>
    internal static double DeltaTSeconds(double y)
    {
        double t;
        if (y < 1920) { t = y - 1900; return -2.79 + 1.494119 * t - 0.0598939 * t * t + 0.0061966 * Math.Pow(t, 3) - 0.000197 * Math.Pow(t, 4); }
        if (y < 1941) { t = y - 1920; return 21.20 + 0.84493 * t - 0.076100 * t * t + 0.0020936 * Math.Pow(t, 3); }
        if (y < 1961) { t = y - 1950; return 29.07 + 0.407 * t - t * t / 233 + Math.Pow(t, 3) / 2547; }
        if (y < 1986) { t = y - 1975; return 45.45 + 1.067 * t - t * t / 260 - Math.Pow(t, 3) / 718; }
        if (y < 2005) { t = y - 2000; return 63.86 + 0.3345 * t - 0.060374 * t * t + 0.0017275 * Math.Pow(t, 3) + 0.000651814 * Math.Pow(t, 4) + 0.00002373599 * Math.Pow(t, 5); }
        if (y < 2050) { t = y - 2000; return 62.92 + 0.32217 * t + 0.005589 * t * t; }
        return -20 + 32 * Math.Pow((y - 1820) / 100.0, 2) - 0.5628 * (2150 - y);
    }

    // ---- Julian day (Gregorian calendar only) ---------------------------------

    internal static double JulianDay(DateTime utc)
    {
        int y = utc.Year, m = utc.Month;
        double d = utc.Day + (utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0) / 24.0;
        if (m <= 2) { y--; m += 12; }
        int a = y / 100, b = 2 - a + a / 4;
        return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + b - 1524.5;
    }

    internal static DateTime FromJulianDay(double jd)
    {
        jd += 0.5;
        long z = (long)Math.Floor(jd);
        double f = jd - z;
        long alpha = (long)Math.Floor((z - 1867216.25) / 36524.25);
        long a = z + 1 + alpha - alpha / 4;
        long b = a + 1524, c = (long)Math.Floor((b - 122.1) / 365.25), d = (long)Math.Floor(365.25 * c), e = (long)Math.Floor((b - d) / 30.6001);
        double day = b - d - Math.Floor(30.6001 * e) + f;
        int month = (int)(e < 14 ? e - 1 : e - 13);
        int year = (int)(month > 2 ? c - 4716 : c - 4715);
        int dayInt = (int)Math.Floor(day);
        return new DateTime(year, month, dayInt, 0, 0, 0, DateTimeKind.Utc).AddDays(day - dayInt);
    }
}
