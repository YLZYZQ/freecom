using System.Text;
using FreeCom.Core.Pipeline;
using Xunit;

namespace FreeCom.Tests;

/// <summary>真实串口分片场景 E2E：一行数据拆多片、片间隔 &lt; 组帧间隔 →
/// 应合并为一个显示条目（一个时间戳一行显示）——修复用户报告的 COM14 分行问题。</summary>
[Collection("SerialBasis")]
public class FrameAssemblyE2ETests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public FrameAssemblyE2ETests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task FragmentedLine_DisplaysAsSingleEntry()
    {
        await _fx.OpenDefaultsAsync();
        // 模拟物理串口分片：一行 UWB 数据拆 4 片，片间隔 5ms（< 30ms 组帧间隔）
        var line = "DISTANCE:43, ANGLE:110.77, PDOA:110.88\r\n";
        var segs = new[] { line[..1], line[1..27], line[27..28], line[28..] };
        long before = _fx.Pipeline.Display.LastSeq;
        foreach (var seg in segs)
        {
            _fx.Peer!.Send(Encoding.ASCII.GetBytes(seg));
            Thread.Sleep(5);
        }
        // 等组帧静默触发 + 管线交付
        await Task.Delay(300);

        var entries = _fx.Pipeline.Display.Snapshot(before, 10)
            .Where(e => e.Dir == DataDirection.Rx).ToList();
        Assert.Single(entries); // 整行一条显示条目（修复前会是 4 条）
        Assert.Equal(line, Encoding.ASCII.GetString(entries[0].Data.ToArray()));

        // Raw 日志同样按帧记录（一条含整行，供 receive/wait 匹配与导出）
        var raw = _fx.Pipeline.Raw.Snapshot(before, 10, DataDirection.Rx).ToList();
        Assert.Contains(raw, r => Encoding.ASCII.GetString(r.Data.ToArray()).Contains("DISTANCE:43, ANGLE:110.77"));
    }

    [Fact]
    public async Task SeparateLines_SlowGap_StaysSeparateEntries()
    {
        await _fx.OpenDefaultsAsync();
        long before = _fx.Pipeline.Display.LastSeq;
        _fx.Peer!.Send(Encoding.ASCII.GetBytes("first\n"));
        Thread.Sleep(120); // 超过组帧间隔：独立成帧
        _fx.Peer!.Send(Encoding.ASCII.GetBytes("second\n"));
        await Task.Delay(300);

        var entries = _fx.Pipeline.Display.Snapshot(before, 10)
            .Where(e => e.Dir == DataDirection.Rx).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("first\n", Encoding.ASCII.GetString(entries[0].Data.ToArray()));
        Assert.Equal("second\n", Encoding.ASCII.GetString(entries[1].Data.ToArray()));
    }
}
