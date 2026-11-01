namespace QuickJob.Tests;

using QuickJob;

/// <summary>
/// CronSchedule 的行为学：解析（收垃圾）、别名、字段宽窄、Next/Previous 的推进语义、
/// 与 MatchesLocal 的对拍（逐秒穷举三天），以及 DST 档的确定性值。
/// </summary>
public class CronScheduleTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("* * *")]             // 3 fields
    [InlineData("0 0 0 * * * 2030 5")] // 8 fields
    [InlineData("60 0 * * *")]         // minute 60
    [InlineData("0 24 * * *")]         // hour 24
    [InlineData("0 0 0 * *")]          // dom 0
    [InlineData("0 0 * 13 *")]         // month 13
    [InlineData("0 0 * * 8")]          // dow 8
    [InlineData("5-2 * * * *")]        // descending range
    [InlineData("0 */0 * * *")]        // step 0
    [InlineData("0 noon * * *")]       // 4-letter token
    [InlineData("0 0 0 * * * 2100")]   // year outside 1970..2099
    [InlineData(" nonsense ")]         // single junk field
    public void Parse_rejects_garbage(string expression)
    {
        Assert.False(CronSchedule.TryParse(expression, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Throws<QuickJobException>(() => CronSchedule.Parse(expression));
    }

    [Theory]
    [InlineData("0 0 * * *")]
    [InlineData("*/15 9-17 * * mon-fri")]
    [InlineData("0 12 1,15 6 *")]
    [InlineData("30/10 2 * * *")]     // a/s affine posture
    [InlineData("0 0 ? * *")]         // question mark on dom
    [InlineData("0 0 * * ?")]
    [InlineData("* * * * * * 2027")]
    public void Parse_accepts_ordinary_shapes(string expression)
    {
        var schedule = CronSchedule.Parse(expression);
        Assert.Equal(expression, schedule.Template); // 非别名模板原样保留
    }

    [Theory]
    [InlineData("@yearly", "0 0 0 1 1 *")]
    [InlineData("@annually", "0 0 0 1 1 *")]
    [InlineData("@monthly", "0 0 0 1 * *")]
    [InlineData("@weekly", "0 0 0 * * 0")]
    [InlineData("@daily", "0 0 0 * * *")]
    [InlineData("@midnight", "0 0 0 * * *")]
    [InlineData("@hourly", "0 0 * * * *")]
    [InlineData("@minutely", "0 * * * * *")]
    [InlineData("@DAILY", "0 0 0 * * *")] // 别名把大小写一并归一进展开串
    public void Aliases_expand_to_canonical_template_with_seconds(string alias, string canonical)
    {
        var schedule = CronSchedule.Parse(alias);
        Assert.Equal(canonical, schedule.Template); // Template 存 canonical 展开档
        Assert.True(schedule.HasSeconds); // 展开后的档位全带秒字段
        Assert.Throws<QuickJobException>(() => CronSchedule.Parse("@fortnightly"));
    }

    [Fact]
    public void Daily_alias_walks_midnight_boundaries()
    {
        var schedule = CronSchedule.Parse("@daily");
        var tz = TimeZoneInfo.Utc;

        var next = schedule.Next(Utc(2026, 5, 4, 0, 0, 0), tz);
        Assert.NotNull(next);
        Assert.Equal(Utc(2026, 5, 5, 0, 0, 0), next.GetValueOrDefault());

        var later = schedule.Next(Utc(2026, 5, 4, 23, 59, 59), tz);
        Assert.Equal(Utc(2026, 5, 5, 0, 0, 0), later.GetValueOrDefault());
    }

    [Fact]
    public void Minutely_alias_lands_on_minute_boundaries()
    {
        var schedule = CronSchedule.Parse("@minutely");
        Assert.Equal(Utc(2026, 5, 4, 10, 1, 0),
            schedule.Next(Utc(2026, 5, 4, 10, 0, 7), TimeZoneInfo.Utc).GetValueOrDefault());
    }

    [Fact]
    public void Step_minutes_walk_the_lattice()
    {
        var schedule = CronSchedule.Parse("*/15 * * * *");
        Assert.Equal(Utc(2026, 5, 4, 10, 15, 0),
            schedule.Next(Utc(2026, 5, 4, 10, 0, 7), TimeZoneInfo.Utc).GetValueOrDefault());
        Assert.Equal(Utc(2026, 5, 4, 10, 0, 0),
            schedule.Next(Utc(2026, 5, 4, 9, 59, 59), TimeZoneInfo.Utc).GetValueOrDefault());
    }

    [Fact]
    public void Names_and_ranges_land_on_weekdays()
    {
        var schedule = CronSchedule.Parse("0 12 * * mon-fri");
        var tz = TimeZoneInfo.Utc;

        // 2026-05-01 是周五：当天的 12 点已过 ⇒ 下一跳是周一 12 点
        var from = Utc(2026, 5, 1, 13, 0, 0);
        Assert.Equal(Utc(2026, 5, 4, 12, 0, 0), schedule.Next(from, tz).GetValueOrDefault());

        // 周日被 mon-fri 挡住：跳到周一
        var sunday = Utc(2026, 5, 3, 5, 0, 0);
        Assert.Equal(Utc(2026, 5, 4, 12, 0, 0), schedule.Next(sunday, tz).GetValueOrDefault());
    }

    [Fact]
    public void Dow_seven_equals_sunday()
    {
        var schedule = CronSchedule.Parse("0 0 * * 7");
        Assert.Equal(Utc(2026, 5, 3, 0, 0, 0),
            schedule.Next(Utc(2026, 5, 1, 0, 0, 1), TimeZoneInfo.Utc).GetValueOrDefault());
    }

    [Fact]
    public void Vixie_or_rule_fires_dom_or_dow()
    {
        var schedule = CronSchedule.Parse("0 0 13 * 5");
        var tz = TimeZoneInfo.Utc;

        // dom 与 dow 同时受限：或集 —— 第一个周五先响（May 1 2026 是周五）
        Assert.Equal(Utc(2026, 5, 1, 0, 0, 0), schedule.Next(Utc(2026, 4, 30, 1, 0, 0), tz).GetValueOrDefault());
        // 再往后是 May 8 周五（13 号还没到）
        Assert.Equal(Utc(2026, 5, 8, 0, 0, 0), schedule.Next(Utc(2026, 5, 4, 0, 0, 0), tz).GetValueOrDefault());
    }

    [Fact]
    public void Seconds_field_ticks_within_wildcard_hour()
    {
        var schedule = CronSchedule.Parse("*/10 * * * * *");
        Assert.True(schedule.HasSeconds);
        Assert.Equal(Utc(2026, 5, 4, 10, 0, 10),
            schedule.Next(Utc(2026, 5, 4, 10, 0, 7), TimeZoneInfo.Utc).GetValueOrDefault());
    }

    [Fact]
    public void Year_field_bounds_the_horizon()
    {
        var yearly = CronSchedule.Parse("0 0 0 1 1 * 2030");
        Assert.Equal(Utc(2030, 1, 1, 0, 0, 0),
            yearly.Next(Utc(2026, 5, 4, 0, 0, 0), TimeZoneInfo.Utc).GetValueOrDefault());

        var past = CronSchedule.Parse("0 0 0 1 1 * 1975");
        Assert.Null(past.Next(Utc(2026, 5, 4, 0, 0, 0), TimeZoneInfo.Utc));

        // 通配表 + 年容限：起点必须落在地平线内（5 年）—— 从 2049 年底走到 2050 年元旦
        var infinite = CronSchedule.Parse("* * * * * * 2050");
        Assert.Equal(Utc(2050, 1, 1, 0, 0, 0),
            infinite.Next(Utc(2049, 12, 31, 0, 0, 0), TimeZoneInfo.Utc).GetValueOrDefault());
    }

    [Fact]
    public void Previous_walks_back_private_slots()
    {
        var schedule = CronSchedule.Parse("0 12 * * *");
        var tz = TimeZoneInfo.Utc;

        Assert.Equal(Utc(2026, 5, 4, 12, 0, 0), schedule.Previous(Utc(2026, 5, 4, 13, 0, 0), tz).GetValueOrDefault());
        Assert.Equal(Utc(2026, 5, 3, 12, 0, 0), schedule.Previous(Utc(2026, 5, 4, 12, 0, 0), tz).GetValueOrDefault());

        // 1200 这个槽本身 inclusive=true 时落在就绪
        Assert.Equal(Utc(2026, 5, 4, 12, 0, 0), schedule.Previous(Utc(2026, 5, 4, 12, 0, 0), tz, inclusive: true).GetValueOrDefault());
    }

    [Fact]
    public void Next_honours_inclusive_asking()
    {
        var schedule = CronSchedule.Parse("0 12 * * *");
        var tz = TimeZoneInfo.Utc;
        var at = Utc(2026, 5, 4, 12, 0, 0);

        Assert.Equal(at, schedule.Next(at, tz, inclusive: true).GetValueOrDefault());
        Assert.Equal(Utc(2026, 5, 5, 12, 0, 0), schedule.Next(at, tz, inclusive: false).GetValueOrDefault());
    }

    [Theory]
    [InlineData("*/15 9-17 * * mon-fri", 2)]
    [InlineData("30 2 1,15 * *", 2)]
    [InlineData("*/10 * * * * *", 1)]
    public void Next_agrees_with_brute_forced_match_walk(string expression, int days)
    {
        var schedule = CronSchedule.Parse(expression);
        var tz = TimeZoneInfo.Utc;

        // 起点钉在 dom=1 的 00:00 —— dom 受限的表达式必须落在窗口第一天才有命中
        var start = new DateTime(2026, 5, 1, 0, 0, 0);
        var expected = new List<DateTime>();
        for (var day = 0; day < days; day++)
        {
            var dayStart = start.AddDays(day);
            for (var second = 0; second < 86_400; second++)
            {
                var probe = dayStart.AddSeconds(second);
                if (schedule.MatchesLocal(probe)) expected.Add(probe);
            }
        }

        Assert.NotEmpty(expected);

        var cursor = new DateTimeOffset(start, TimeSpan.Zero).AddTicks(-1);
        foreach (var want in expected)
        {
            var got = schedule.Next(cursor, tz);
            Assert.True(got.HasValue, $"Next({cursor:O}) returned null, expected {want:O}");
            Assert.Equal(new DateTimeOffset(want, TimeSpan.Zero), got.GetValueOrDefault());
            cursor = got.GetValueOrDefault();
        }
    }

    [Fact]
    public void Dst_slot_stays_walls_clock_deterministic()
    {
        // America/New_York 在多数平台也能解析（.NET 8+ 默认 ICU 映射 IANA）；解析不了就跳过
        TimeZoneInfo? tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { return; }
        catch (InvalidTimeZoneException) { return; }

        var schedule = CronSchedule.Parse("0 2 * * *");

        // 春季拨快：2026-03-08（周日）02:00 是不存在的壁钟 —— 按过渡瞬间 07:00Z 落点，绝不抛异常
        var spring = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), schedule.Next(spring, tz).GetValueOrDefault());

        // 秋季回拨：11/1 的 02:00 有效且无歧义（回拨发生在 01:00-01:59 的重复段）→ EST 口径
        var autumn = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(-4));
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 1, 2, 0, 0), tz),
            schedule.Next(autumn, tz).GetValueOrDefault());

        // 冬令时一跳的确定性
        var winter = new DateTimeOffset(2026, 1, 30, 12, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 1, 31, 2, 0, 0), tz),
            schedule.Next(winter, tz).GetValueOrDefault());
    }

    [Fact]
    public void Unfireable_month_expression_returns_null()
    {
        // 2 月 30 号是世界上不存在的槽
        var schedule = CronSchedule.Parse("0 0 30 2 *");
        Assert.Null(schedule.Next(Utc(2026, 5, 4, 0, 0, 0), TimeZoneInfo.Utc));
    }

    static DateTimeOffset Utc(int y, int mo, int d, int h, int mi, int s)
        => new(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc));
}
