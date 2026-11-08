namespace DeskCal.App;

internal static class ReminderText
{
    /// <summary>"10분 전", "1일 전", "2주 전"; 0 is "시작 시각".</summary>
    public static string DescribeOffset(int minutes)
    {
        if (minutes <= 0) return "시작 시각";
        if (minutes % 10080 == 0) return $"{minutes / 10080}주 전";
        if (minutes % 1440 == 0) return $"{minutes / 1440}일 전";
        if (minutes % 60 == 0) return $"{minutes / 60}시간 전";
        return $"{minutes}분 전";
    }
}
