using System.Windows;

namespace FreeCom.App;

/// <summary>主题管理：Dark/Light 资源字典切换（设计稿 v2 双主题）。
/// 色板字典固定在 MergedDictionaries[0]，样式字典引用 DynamicResource，换字典即换肤。</summary>
public static class Theme
{
    public const string Dark = "dark";
    public const string Light = "light";

    private static string _current = Dark;

    public static string Current => _current;
    public static bool IsDark => _current == Dark;

    /// <summary>主题切换后触发（主窗口据此重刷 ScottPlot 与接收区颜色）。</summary>
    public static event Action? Changed;

    public static void Apply(string? name)
    {
        var target = string.Equals(name, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
        if (Application.Current is null) { _current = target; return; }
        if (_current == target && Application.Current.Resources.MergedDictionaries.Count > 0) return;

        var dict = new ResourceDictionary
        {
            Source = new Uri($"Themes/{target}.xaml", UriKind.Relative),
        };
        // 色板固定在第一个位置（App.xaml 中同样约定），Controls.xaml 经 DynamicResource 取色
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict;
        else merged.Insert(0, dict);
        _current = target;
        Changed?.Invoke();
    }
}
