using FreeCom.Core.Pipeline;
using FreeCom.Core.Protocols;
using Xunit;

namespace FreeCom.Tests;

[Collection("SerialBasis")]
public class DiagnosticTests
{
    private static async Task<string> RunBurstAsync(int n)
    {
        using var ctx = PipelineCtx.Create("TEXT");
        for (int i = 0; i < n; i++)
        {
            ctx.Peer.SendText($"{{burst}}{i},{i * 2}\n");
            await ctx.Pipeline.SendTextAsync($"cmd{i}", newline: "lf");
        }
        await Wait.FramesAsync(ctx.Pipeline, n, timeoutMs: 15_000);
        var fault = ctx.Pipeline.ConsumerTask.IsFaulted ? ctx.Pipeline.ConsumerTask.Exception!.ToString() : "none";
        return $"frames={ctx.Pipeline.Counters.FramesParsed}/{n} rx={ctx.Pipeline.Counters.RxBytes} tx={ctx.Pipeline.Counters.TxBytes} " +
               $"consumer={ctx.Pipeline.ConsumerTask.Status} fault={fault}";
    }

    [Fact]
    public async Task Burst_InterleavedSendReceive_OverRealSerial()
    {
        var report = await RunBurstAsync(50);
        Assert.DoesNotContain("fault=System", report);
        Assert.DoesNotContain("consumer=Faulted", report);
    }
}
