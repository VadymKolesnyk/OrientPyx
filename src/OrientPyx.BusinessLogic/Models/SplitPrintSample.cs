using System.Globalization;
using OrientPyx.BusinessLogic.Services;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The fixed test slip printed from the print-settings dialog («Тестова роздруківка») so an operator can
/// check the printer and roll width without a real read-out: a set-course run with every header line the
/// renderer knows (start/finish, time, distance, pace, place) and a nine-row passage. Pure data, no
/// competition needed — the same formats the real slip uses (<see cref="SplitPrintFormat"/>).
/// </summary>
public static class SplitPrintSample
{
    // Leg geometry of the test course: control code, straight-line leg length (m) and leg time (s). The leg
    // times add up to 29:37, so start 12:34:56 lands the finish on 13:04:33.
    private static readonly (string Code, int Metres, int Seconds)[] Legs =
    [
        ("31", 320, 192),
        ("32", 410, 245),
        ("33", 275, 168),
        ("34", 530, 321),
        ("35", 365, 220),
        ("36", 440, 266),
        ("37", 290, 175),
        ("100", 180, 110),
        ("F", 120, 80)
    ];

    public static SplitPrintDocument Create()
    {
        var start = new TimeSpan(12, 34, 56);
        var rows = new List<SplitPrintRow>(Legs.Length);
        var elapsed = TimeSpan.Zero;
        var totalKm = 0m;
        var index = 0;
        foreach (var leg in Legs)
        {
            var legTime = TimeSpan.FromSeconds(leg.Seconds);
            var legKm = leg.Metres / 1000m;
            elapsed += legTime;
            totalKm += legKm;
            var isFinish = leg.Code == "F";
            if (!isFinish)
                index++;
            rows.Add(new SplitPrintRow(
                Index: isFinish ? string.Empty : index.ToString(CultureInfo.InvariantCulture),
                Code: leg.Code,
                DistanceText: SplitPrintFormat.DistanceMetres(legKm),
                LegText: TimeFormat.Duration(legTime),
                ElapsedText: TimeFormat.Duration(elapsed),
                PaceText: SplitPrintFormat.Pace(SplitPrintFormat.SecondsPerKm(legTime, legKm)),
                PointsText: null,
                OnCourse: true));
        }

        var finish = start + elapsed;
        return new SplitPrintDocument
        {
            FullName = "Колесник Вадим",
            Number = "21",
            ChipNumber = "8000067",
            GroupName = "Ч21Е",
            ResultText = TimeFormat.Result(elapsed),
            StatusText = "OK",
            StartClock = TimeFormat.Clock(start),
            FinishClock = finish.ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture),
            TotalDistanceText = SplitPrintFormat.DistanceMetres(totalKm),
            AvgPaceText = SplitPrintFormat.Pace(SplitPrintFormat.SecondsPerKm(elapsed, totalKm)),
            Place = 3,
            ReadOutCount = 7,
            Rows = rows
        };
    }
}
