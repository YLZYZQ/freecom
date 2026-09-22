using FreeCom.Core.Transports;
using Xunit;

namespace FreeCom.Tests;

/// <summary>VirtualComManager 纯逻辑单测（setupc 输出解析/端口建议，不依赖驱动与提权）。</summary>
public class VirtualComManagerTests
{
    [Fact]
    public void ParseList_PairedPorts()
    {
        const string output = """
               CNCA0 PortName=COM20
               CNCB0 PortName=COM21
               CNCA1 PortName=COM22
               CNCB1 PortName=COM23
               CNCA2 PortName=COM24
               CNCB2 PortName=COM25
            """;
        var pairs = VirtualComManager.ParseListOutput(output);
        Assert.Equal(3, pairs.Count);
        Assert.Equal("COM20", pairs[0].PortA);
        Assert.Equal("COM21", pairs[0].PortB);
        Assert.Equal("CNCA0", pairs[0].IdA);
        Assert.Equal("COM22", pairs[1].PortA);
        Assert.Equal("COM25", pairs[2].PortB);
    }

    [Fact]
    public void ParseList_SkipsUnnamedAndNoise()
    {
        const string output = """
               CNCA3 PortName=-
               CNCB3 PortName=-
               some random line
               CNCA0 PortName=COM20
               CNCB0 PortName=COM21
            """;
        var pairs = VirtualComManager.ParseListOutput(output);
        Assert.Single(pairs); // 未命名对（PortName=-）不产出可用端口对
    }

    [Fact]
    public void ParseList_EmptyAndGarbage()
    {
        Assert.Empty(VirtualComManager.ParseListOutput(""));
        Assert.Empty(VirtualComManager.ParseListOutput("随便什么内容\nCNCA0\n"));
    }

    [Fact]
    public void SuggestFreePorts_SkipsUsed()
    {
        var (a, b) = VirtualComManager.SuggestFreePorts(["COM20", "COM21", "COM23", "COM1"]);
        Assert.Equal("COM22", a);
        Assert.Equal("COM24", b);
    }

    [Fact]
    public void DriverInstalled_DetectedOnThisMachine()
    {
        var manager = new VirtualComManager();
        // 本机已按 V0 安装 com0com（测试环境的既定前置）
        Assert.True(manager.DriverInstalled, "com0com setupc.exe 应存在于 Program Files");
    }

    [Fact]
    public void RemovePair_RejectsInvalidId_WithoutElevation()
    {
        var manager = new VirtualComManager();
        // 非法输入必须在提权前就拒绝（不弹 UAC）
        Assert.Throws<ArgumentException>(() => manager.RemovePair("COM26"));
        Assert.Throws<ArgumentException>(() => manager.RemovePair(""));
        Assert.Throws<ArgumentException>(() => manager.RemovePair("CNCA"));
    }
}
