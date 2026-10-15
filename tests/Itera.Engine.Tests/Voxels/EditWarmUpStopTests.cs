using System.Globalization;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// <see cref="EditWarmUpStop"/>: when the edit warm-up stops, on made-up rounds and on four starts of the Steam build
/// (composite ReadyToRun) measured in the game.
/// </summary>
public sealed class EditWarmUpStopTests
{
    private const string Steady = "rounds steady under three quarters of the first second's best";

    [Fact]
    public void Code_never_optimised_stops_after_one_round()
    {
        var stop = new EditWarmUpStop(optimisable: false);

        Assert.True(stop.Stop(400, 1000, 0.4));
        Assert.Equal(1, stop.Rounds);
        Assert.Equal(400, stop.FirstPassMilliseconds);
        Assert.Equal("code never optimised (Debug), one round", stop.Reason);
    }

    [Fact]
    public void Two_rounds_in_a_row_under_half_the_first_pass_time_stop_it()
    {
        var stop = new EditWarmUpStop(optimisable: true);
        // The first round compiles; the next four are the first pass (150 ms at least); a fast round alone (the 7th)
        // does not stop it. Something compiles every round, so only the rounds' times can.
        double[] rounds = [300, 150, 155, 160, 150, 140, 70, 120, 70, 60, 60];
        var seconds = 0.0;
        var stoppedAt = 0;
        for (var i = 0; i < rounds.Length && stoppedAt == 0; i++)
        {
            seconds += rounds[i] / 1000;
            stoppedAt = stop.Stop(rounds[i], i, seconds) ? i + 1 : 0;
        }

        Assert.Equal(10, stoppedAt);
        Assert.Equal(150, stop.FirstPassMilliseconds);
        Assert.Equal(60, stop.LastMilliseconds);
        Assert.Equal("two rounds under half the first-pass time", stop.Reason);
    }

    /// <summary>
    /// The case that ran out the time limit (a Release start, 2026-10-09): the code was already optimised by the second
    /// round, so no later round could take half the first-pass time. Here the start-up compiles until 2 s.
    /// </summary>
    [Fact]
    public void Code_fast_from_the_second_round_stops_once_nothing_has_compiled_for_the_quiet_time()
    {
        var stop = new EditWarmUpStop(optimisable: true);
        var seconds = 0.0;
        var compiled = 0L;
        var lastCompile = 0.0;
        var stopped = false;
        while (!stopped)
        {
            seconds += 0.052;
            if (seconds < 2)
            {
                compiled += 7;
                lastCompile = seconds;
            }

            stopped = stop.Stop(52, compiled, seconds);
        }

        Assert.Equal("nothing compiled for 0.75 s", stop.Reason);
        Assert.True(seconds - lastCompile >= EditWarmUpStop.QuietSeconds);
        Assert.True(seconds - 0.052 - lastCompile < EditWarmUpStop.QuietSeconds);
    }

    [Fact]
    public void Quiet_stretches_shorter_than_the_quiet_time_do_not_stop_it_and_the_time_limit_does()
    {
        // First-pass rounds that never speed up, with a compile every 0.75 s: 0.6 s at most without one.
        var stop = new EditWarmUpStop(optimisable: true);
        var seconds = 0.0;
        var compiled = 0L;
        var lastCompile = 0.0;
        var stopped = false;
        while (!stopped)
        {
            seconds += 0.15;
            if (seconds - lastCompile >= 0.7)
            {
                compiled++;
                lastCompile = seconds;
            }

            stopped = stop.Stop(150, compiled, seconds);
        }

        Assert.Equal("time limit", stop.Reason);
        Assert.True(seconds >= EditWarmUpStop.LimitSeconds);
        Assert.True(seconds < EditWarmUpStop.LimitSeconds + 0.15);
    }

    /// <summary>
    /// A start of the Steam build whose process compiled nothing for 312 ms early on (from 0.15 s, the longest such
    /// stretch in twenty Release starts) while its rounds still took twice the best: the warm-up goes on until its
    /// rounds halve, at 2.6 s.
    /// </summary>
    [Fact]
    public void A_steam_start_quiet_early_goes_on_until_its_rounds_are_fast()
    {
        var (stop, at, rounds) = Replay(SteamStartQuietEarly);

        Assert.Equal("two rounds under half the first-pass time", stop.Reason);
        Assert.True(rounds[at].Seconds > 2.5);
        AssertFastAfter(rounds, at);
    }

    /// <summary>
    /// A start of the Steam build whose rounds never take half the first pass (43 ms against 28 at best): the warm-up
    /// stops once its rounds have held at 28 ms, at 2.1 s, with its rounds as fast as they get (it ran out the 10 s
    /// limit before 2026-10-09, and stopped once nothing compiled, at 4.6 s, until 2026-10-15).
    /// </summary>
    [Fact]
    public void A_steam_start_whose_rounds_never_halve_stops_once_its_rounds_hold_steady()
    {
        var (stop, at, rounds) = Replay(SteamStartNeverHalving);

        Assert.Equal(Steady, stop.Reason);
        Assert.True(rounds[at].Seconds < 2.5);
        AssertFastAfter(rounds, at);
    }

    /// <summary>
    /// Two starts of the Steam build in which the game's start-up went on compiling a method or a few every second for
    /// 4 to 6 s after the rounds had settled at 28 ms, so nothing went quiet until about 8 s: the warm-up stops once its
    /// rounds have held steady, at 4.4 s and 2.3 s, its rounds as fast as they get.
    /// </summary>
    [Theory]
    [InlineData(SteamStartCompilingOn, 4.5)]
    [InlineData(SteamStartSettlingEarly, 2.5)]
    public void A_steam_start_whose_process_keeps_compiling_stops_once_its_rounds_hold_steady(
        string trace,
        double mostSeconds
    )
    {
        var (stop, at, rounds) = Replay(trace);

        Assert.Equal(Steady, stop.Reason);
        Assert.True(rounds[at].Seconds < mostSeconds, $"stopped at {rounds[at].Seconds} s");
        AssertFastAfter(rounds, at);
    }

    /// <summary>
    /// Made-up rounds of precompiled code: 42 ms for a second, 90 while .NET measures it, then 28 for good, a method
    /// compiled every 0.3 s throughout, so the process never goes quiet. The 8th round in a row at 28 ms stops it; a
    /// round slower than a tenth over the best starts the count again.
    /// </summary>
    [Fact]
    public void Rounds_steady_under_three_quarters_of_the_first_seconds_best_stop_it()
    {
        var stop = new EditWarmUpStop(optimisable: true);
        var seconds = 0.0;
        var compiled = 0L;
        var lastCompile = 0.0;
        var late = 0;
        var stopped = false;
        while (!stopped)
        {
            // 28 ms from 2 s on, but the 4th of those rounds 40.
            var milliseconds =
                seconds < 1 ? 42
                : seconds < 2 ? 90
                : late == 3 ? 40
                : 28;
            late += seconds >= 2 ? 1 : 0;
            seconds += milliseconds / 1000.0;
            if (seconds - lastCompile >= 0.3)
            {
                compiled++;
                lastCompile = seconds;
            }

            stopped = stop.Stop(milliseconds, compiled, seconds);
        }

        Assert.Equal(Steady, stop.Reason);
        Assert.Equal(42, stop.EarlyMilliseconds);
        // Three at 28 ms, the slow one, then the steady rounds.
        Assert.Equal(3 + 1 + EditWarmUpStop.SteadyRounds, late);
    }

    [Fact]
    public void Rounds_steady_at_more_than_three_quarters_of_the_first_seconds_best_do_not_stop_it()
    {
        // 42 ms, then 33 (0.79 of it) for good, a method compiled every 0.3 s: only the time limit stops it.
        var stop = new EditWarmUpStop(optimisable: true);
        var seconds = 0.0;
        var compiled = 0L;
        var lastCompile = 0.0;
        var stopped = false;
        while (!stopped)
        {
            var milliseconds = seconds < 1 ? 42 : 33;
            seconds += milliseconds / 1000.0;
            if (seconds - lastCompile >= 0.3)
            {
                compiled++;
                lastCompile = seconds;
            }

            stopped = stop.Stop(milliseconds, compiled, seconds);
        }

        Assert.Equal("time limit", stop.Reason);
    }

    /// <summary>The rounds after the stop take within a fifth of the trace's best round.</summary>
    private static void AssertFastAfter(List<Round> rounds, int at)
    {
        var best = double.PositiveInfinity;
        foreach (var round in rounds)
        {
            best = Math.Min(best, round.Milliseconds);
        }

        for (var i = at + 1; i < Math.Min(rounds.Count, at + 6); i++)
        {
            Assert.True(
                rounds[i].Milliseconds < 1.2 * best,
                $"round {i + 1}: {rounds[i].Milliseconds} ms, best {best}"
            );
        }
    }

    private static (EditWarmUpStop Stop, int At, List<Round> Rounds) Replay(string trace)
    {
        var rounds = new List<Round>();
        var compiled = 0L;
        foreach (var entry in trace.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':');
            compiled += long.Parse(parts[2], CultureInfo.InvariantCulture);
            rounds.Add(
                new Round(
                    double.Parse(parts[0], CultureInfo.InvariantCulture) / 1000,
                    double.Parse(parts[1], CultureInfo.InvariantCulture),
                    compiled
                )
            );
        }

        var stop = new EditWarmUpStop(optimisable: true);
        for (var i = 0; i < rounds.Count; i++)
        {
            if (stop.Stop(rounds[i].Milliseconds, rounds[i].Compiled, rounds[i].Seconds))
            {
                return (stop, i, rounds);
            }
        }

        throw new InvalidOperationException("the trace ended before the warm-up stopped");
    }

    /// <summary>
    /// A trace's round: when it ended (seconds), how long it took (milliseconds), and the methods compiled in the process
    /// by then.
    /// </summary>
    private readonly record struct Round(double Seconds, double Milliseconds, long Compiled);

    // Traces of the warm-up's rounds, run for 6 s in two starts of the Steam build (composite ReadyToRun, Release,
    // linux-x64, 2026-10-09): each round's end and its time in milliseconds, and the methods the process compiled
    // during it.

    private const string SteamStartQuietEarly = """
        59:59.4:176 145:85.9:1 225:79.3:0 304:79.8:0 372:68.0:0 415:42.1:0 457:42.1:0 508:51.3:25 566:58.2:0
        622:55.9:0 678:55.5:0 734:56.3:22 788:54.0:0 842:54.0:0 896:53.9:0 950:54.0:0 1004:54.0:23 1062:58.6:6
        1120:57.8:0 1178:57.7:0 1235:57.6:0 1293:57.6:0 1365:71.6:155 1443:78.7:0 1518:75.2:0 1594:75.8:109
        1672:77.3:0 1748:76.3:0 1823:75.2:23 1898:74.8:32 1972:74.5:268 2047:74.3:2 2121:74.3:0 2195:74.2:61
        2269:74.4:132 2333:63.9:167 2370:36.8:1 2407:36.7:0 2444:36.7:16 2480:36.0:23 2534:28.8:140 2587:28.1:159
        2640:28.2:115 2695:30.4:61 2749:28.2:0 2802:28.1:1 2854:27.7:2 2908:28.1:0 2966:33.0:50 3019:28.1:57
        3072:28.0:40 3125:27.9:0 3178:27.6:0 3230:27.6:0 3283:27.9:1 3337:28.4:1 3395:32.9:0 3449:29.5:2
        3504:30.2:1 3557:27.9:0 3610:27.8:0 3663:27.8:0 3716:27.8:0 3769:28.2:0 3822:27.7:0 3875:27.7:0 3928:28.0:1
        3980:27.7:0 4034:28.3:0 4087:28.2:0 4140:28.3:0 4193:28.0:0 4246:27.9:4 4301:29.5:2 4356:30.4:0 4409:27.7:0
        4462:28.0:0 4516:28.3:0 4569:28.1:0 4622:27.9:0 4675:28.0:0 4728:28.0:0 4781:28.0:0 4834:28.0:0 4887:28.4:0
        4941:28.2:0 4994:28.0:0 5047:28.3:0 5100:27.8:0 5153:27.8:0 5206:28.0:0 5259:28.0:0 5312:28.5:0 5365:27.9:0
        5419:28.6:0 5472:28.3:0 5526:28.4:0 5578:27.4:0 5631:28.0:0 5686:29.6:0 5739:28.2:0 5792:27.5:0 5844:27.5:0
        5898:28.2:0 5951:28.2:0 6004:28.3:0
        """;

    private const string SteamStartNeverHalving = """
        56:56.1:148 107:50.6:2 150:43.4:0 193:43.2:0 243:50.0:1 287:43.2:0 330:43.1:0 373:43.3:0 417:44.3:50
        461:43.3:0 503:42.8:0 546:42.6:0 589:42.5:0 631:42.8:0 674:42.5:0 719:44.8:79 761:42.6:154 804:42.8:85
        847:42.3:59 889:42.6:0 933:43.9:57 1033:100.1:62 1126:93.1:0 1217:91.1:52 1308:90.3:0 1397:89.8:107
        1486:88.9:32 1576:89.3:110 1665:89.1:0 1754:88.7:30 1835:81.9:171 1864:28.5:67 1892:28.2:1 1920:28.2:0
        1948:28.1:0 1977:28.1:23 2005:28.3:85 2033:28.1:54 2061:27.9:23 2089:28.5:9 2143:28.6:0 2201:33.2:22
        2255:28.4:1 2312:32.5:7 2368:30.7:49 2422:28.6:41 2476:29.1:0 2530:28.9:30 2584:28.7:1 2637:28.2:0
        2691:29.0:2 2745:28.9:0 2799:28.8:0 2852:28.6:2 2905:27.9:0 2959:28.8:0 3013:28.7:0 3067:28.5:0 3120:28.5:0
        3174:28.4:1 3227:28.6:0 3281:28.4:0 3334:28.6:0 3388:28.7:0 3442:29.0:0 3496:28.5:1 3549:28.3:0 3603:28.4:0
        3656:28.4:2 3709:28.4:0 3763:28.3:1 3816:28.2:2 3870:28.4:0 3923:28.1:0 3976:28.1:0 4030:28.7:0 4083:28.1:0
        4137:28.4:0 4190:28.1:0 4243:28.4:0 4296:28.3:0 4349:27.8:0 4403:28.3:0 4456:28.3:0 4510:28.8:0 4563:28.0:0
        4617:28.6:0 4670:28.1:0 4723:28.3:0 4777:28.3:0 4830:28.2:0 4883:28.0:0 4936:28.0:0 4989:28.1:0 5042:27.9:0
        5095:28.2:0 5149:28.3:0 5202:28.3:0 5255:28.1:0 5309:28.5:0 5362:28.2:0 5415:28.4:0 5469:28.6:0 5523:28.7:0
        5576:28.4:0 5630:28.3:0 5683:28.3:0 5736:28.2:0 5790:28.3:0 5843:27.9:0 5896:27.8:0 5949:28.2:0 6002:28.2:0
        """;

    // Two starts of the game on the Steam build (composite ReadyToRun, Release, linux-x64, offscreen at 2560x1440,
    // 2026-10-15), the first 6 s: the process compiled into the dozens of methods a second until about 5 s, then a few
    // until about 7.5 s, so nothing went quiet for 0.75 s until about 8 s.

    private const string SteamStartCompilingOn = """
        45:44.8:324 87:42.4:298 132:44.3:391 192:60.1:285 236:44.8:176 279:42.2:70 321:42.3:0 363:42.5:0 406:42.7:0
        452:45.9:86 495:43.4:13 539:43.2:0 582:43.1:0 624:42.2:0 666:42.0:34 708:42.4:159 751:42.6:15 795:43.7:0
        837:41.9:0 878:41.9:0 923:44.5:26 968:45.4:128 1011:42.2:1 1052:41.8:0 1099:46.5:105 1143:44.1:119
        1190:47.3:155 1235:44.3:151 1282:46.9:7 1325:42.8:0 1367:42.9:0 1410:42.8:0 1457:47.3:70 1500:43.0:0
        1543:42.7:0 1586:42.6:0 1629:42.7:0 1681:52.5:0 1724:42.8:0 1773:49.2:68 1856:58.2:0 1935:53.8:0 2041:80.5:242
        2166:99.9:2 2285:93.9:0 2405:95.6:186 2524:93.9:43 2642:92.1:126 2759:92.2:22 2876:92.3:360 2993:92.0:186
        3117:98.4:39 3233:91.7:0 3355:96.1:82 3455:75.4:290 3524:43.7:1 3590:41.2:0 3657:42.1:0 3719:37.1:155
        3773:28.2:83 3826:28.0:111 3879:28.0:8 3932:28.5:0 3992:34.0:71 4046:29.4:18 4102:30.7:0 4155:28.6:0
        4208:27.9:0 4262:29.2:103 4315:28.0:86 4370:29.5:115 4423:28.1:38 4476:27.6:16 4529:28.1:0 4582:28.3:38
        4637:29.1:17 4690:28.3:0 4743:28.3:0 4796:27.7:0 4849:28.0:60 4902:28.3:16 4956:28.8:0 5009:27.9:0 5062:28.1:0
        5116:28.4:31 5169:27.7:0 5222:28.0:9 5275:28.4:59 5328:28.2:77 5381:27.9:1 5434:27.7:14 5487:27.6:0 5539:27.6:0
        5592:28.1:2 5646:28.3:0 5699:28.0:0 5752:28.0:1 5805:28.2:1 5859:28.5:2 5911:27.6:0 5964:27.8:0
        """;

    private const string SteamStartSettlingEarly = """
        47:46.9:338 90:42.8:286 144:54.2:448 196:52.0:275 239:43.5:154 282:42.4:45 324:41.9:0 371:46.8:0 413:42.1:0
        460:47.2:8 504:43.9:0 547:43.2:0 590:42.8:0 633:42.8:0 701:67.9:122 775:74.5:0 847:72.0:0 933:85.8:204
        1026:93.3:0 1118:91.5:0 1210:92.4:54 1303:93.2:158 1396:93.0:0 1489:92.3:51 1581:91.9:227 1671:90.4:0
        1760:89.3:271 1820:59.7:120 1873:53.2:0 1928:54.5:0 1982:54.2:0 2034:52.3:72 2062:27.6:57 2089:27.6:34
        2117:27.5:25 2144:27.5:0 2172:27.4:0 2199:27.5:0 2227:27.5:0 2254:27.5:0 2307:27.6:5 2360:27.8:105 2413:27.9:45
        2466:27.7:0 2518:27.5:25 2571:27.9:0 2624:28.0:0 2677:27.7:0 2732:29.6:61 2789:32.1:143 2842:27.9:94
        2895:27.6:126 2948:28.6:1 3003:29.2:0 3056:28.6:0 3110:28.4:8 3162:27.5:128 3215:27.8:55 3268:27.7:1
        3321:28.1:0 3375:29.0:0 3429:28.6:4 3482:27.9:116 3535:27.9:121 3588:28.6:61 3641:27.7:1 3694:27.8:6
        3747:27.8:37 3800:28.0:8 3852:27.4:0 3905:27.4:42 3957:27.5:30 4010:27.3:0 4062:27.4:0 4115:27.5:0 4167:27.4:44
        4220:27.5:19 4272:27.5:0 4325:27.9:0 4378:27.4:0 4430:27.4:30 4483:27.4:0 4535:27.4:7 4588:27.6:59 4640:27.4:58
        4693:27.4:0 4745:27.5:14 4798:27.5:0 4850:27.5:0 4903:28.1:2 4956:27.9:1 5009:28.0:0 5062:27.9:1 5115:28.0:5
        5168:27.9:5 5222:28.1:0 5274:27.6:0 5327:27.5:0 5379:27.5:0 5432:27.3:1 5484:27.4:0 5537:27.4:1 5589:27.8:0
        5643:28.3:0 5695:27.6:0 5748:27.5:0 5801:27.9:0 5854:27.6:0 5906:27.7:0 5959:27.6:0
        """;
}
