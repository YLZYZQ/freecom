using System.Text;

namespace FreeCom.Core;

/// <summary>编码工具：UTF-8 / GBK / ASCII 名称解析（GBK 依赖 CodePages 提供程序）。</summary>
public static class EncodingHelper
{
    static EncodingHelper()
    {
        // 注册 CodePages 以支持 GBK 等本地编码（PRD F2.1）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string[] Supported => ["utf-8", "gbk", "ascii"];

    public static Encoding Get(string? name)
    {
        var n = (name ?? "utf-8").Trim().ToLowerInvariant();
        try
        {
            return n switch
            {
                "utf-8" or "utf8" => Encoding.UTF8,
                "gbk" or "gb2312" or "cp936" => Encoding.GetEncoding("GBK"),
                "ascii" or "us-ascii" => Encoding.ASCII,
                _ => Encoding.UTF8,
            };
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}

/// <summary>HEX 文本解析：容错输入（空格/逗号/0x 前缀/大小写），非法字符报错。</summary>
public static class HexParse
{
    public static byte[] ParseLoose(string input)
    {
        var hex = new StringBuilder(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c is ' ' or ',' or '\t' or '\r' or '\n') { i++; continue; }
            if (c is 'x' or 'X' && hex.Length > 0 && input[i - 1] == '0') { hex.Length--; i++; continue; }
            if (!Uri.IsHexDigit(c))
                throw new FormatException($"HEX 输入在位置 {i} 含非法字符 '{c}'");
            hex.Append(c);
            i++;
        }
        if (hex.Length == 0) return [];
        if (hex.Length % 2 != 0)
            throw new FormatException("HEX 输入长度必须为偶数个十六进制字符");
        var bytes = new byte[hex.Length / 2];
        for (int j = 0; j < bytes.Length; j++)
            bytes[j] = Convert.ToByte(hex.ToString(2 * j, 2), 16);
        return bytes;
    }

    public static bool TryParse(string input, out byte[] bytes)
    {
        try { bytes = ParseLoose(input); return true; }
        catch { bytes = []; return false; }
    }

    public static string ToHex(ReadOnlySpan<byte> data)
    {
        var s = Convert.ToHexString(data);
        return s.ToLowerInvariant();
    }

    public static string ToHexSpaced(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "";
        const string digits = "0123456789ABCDEF";
        var sb = new StringBuilder(checked(data.Length * 3 - 1));
        foreach (var b in data)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(digits[b >> 4]).Append(digits[b & 15]);
        }
        return sb.ToString();
    }
}
