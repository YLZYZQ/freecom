namespace FreeCom.Core.Plots;

/// <summary>曲线快照（渲染与 API 用，含按步长抽稀）。</summary>
public sealed record CurveSnapshot(string Name, double[] Xs, double[] Ys, long TotalPoints);

/// <summary>单条曲线：按需增长的 X/Y 环形数组，超出上限淘汰最旧点。</summary>
public sealed class Curve
{
    private double[] _xs = [];
    private double[] _ys = [];
    private int _start;
    private int _count;
    private int _maxPoints = 500_000;
    private readonly object _lock = new();

    public Curve(int index)
    {
        Index = index;
        Name = $"#{index + 1}";
    }

    public int Index { get; }
    public string Name { get; set; }
    public int MaxPoints
    {
        get { lock (_lock) return _maxPoints; }
        set
        {
            lock (_lock)
            {
                _maxPoints = Math.Max(0, value);
                if (_xs.Length > _maxPoints) Resize(_maxPoints);
            }
        }
    }

    public void AddPoint(double x, double y)
    {
        lock (_lock)
        {
            if (_maxPoints == 0) return;
            if (_count == _xs.Length && _count < _maxPoints)
            {
                int capacity = (int)Math.Min(_maxPoints, Math.Max(256L, (long)_xs.Length * 2));
                Resize(capacity);
            }
            int index = (int)(((long)_start + _count) % _xs.Length);
            _xs[index] = x;
            _ys[index] = y;
            if (_count == _xs.Length)
                _start = (_start + 1) % _xs.Length;
            else
                _count++;
        }
    }

    // 调用方持锁；缩容保留最新点，同时释放超出预算的数组。
    private void Resize(int capacity)
    {
        var xs = capacity == 0 ? Array.Empty<double>() : new double[capacity];
        var ys = capacity == 0 ? Array.Empty<double>() : new double[capacity];
        int keep = Math.Min(_count, capacity);
        for (int i = 0; i < keep; i++)
        {
            int source = (int)(((long)_start + _count - keep + i) % _xs.Length);
            xs[i] = _xs[source];
            ys[i] = _ys[source];
        }
        _xs = xs;
        _ys = ys;
        _start = 0;
        _count = keep;
    }

    public long Count { get { lock (_lock) return _count; } }

    public CurveSnapshot Snapshot(int maxPoints = int.MaxValue)
    {
        lock (_lock)
        {
            int take = Math.Min(_count, Math.Max(0, maxPoints));
            var xs = new double[take];
            var ys = new double[take];
            CopySnapshot(xs, ys, take);
            return new CurveSnapshot(Name, xs, ys, _count);
        }
    }

    /// <summary>
    /// 零分配渲染路径：把抽稀快照写入调用方提供的缓冲区（避免每次渲染产生大数组、
    /// 防止大对象堆碎片化推高内存），返回实际写入点数（≤ xs.Length）。
    /// </summary>
    public int SnapshotInto(double[] xs, double[] ys, int maxPoints)
    {
        lock (_lock)
        {
            int cap = Math.Max(0, Math.Min(Math.Min(maxPoints, xs.Length), ys.Length));
            int take = Math.Min(cap, _count);
            CopySnapshot(xs, ys, take);
            return take;
        }
    }

    private void CopySnapshot(double[] xs, double[] ys, int take)
    {
        for (int i = 0; i < take; i++)
        {
            // 均匀抽稀且包含最新点；只取一个点时取最新点。
            int offset = take == 1 ? _count - 1 : (int)((long)i * (_count - 1) / (take - 1));
            int source = (int)(((long)_start + offset) % _xs.Length);
            xs[i] = _xs[source];
            ys[i] = _ys[source];
        }
    }

    public void Clear() { lock (_lock) { _xs = []; _ys = []; _start = 0; _count = 0; } }
}

/// <summary>绘图窗口：最多 16 条曲线；Version 供 UI 判断是否需要重绘。</summary>
public sealed class PlotWindow
{
    public const int MaxCurves = 16;

    private readonly object _lock = new();
    private readonly List<Curve> _curves = [];
    private long _nextX;

    public PlotWindow(string id, string title, bool autoY, int maxPoints)
    {
        Id = id;
        Title = title;
        AutoY = autoY;
        MaxPointsPerCurve = maxPoints;
    }

    public string Id { get; }
    public string Title { get; set; }
    /// <summary>Y 轴自动范围（新窗口创建时从模板窗口继承——PRD F4.2）。</summary>
    public bool AutoY { get; set; }
    public int MaxPointsPerCurve { get; set; }
    public long Version { get; private set; }
    public long TotalPoints { get; private set; }

    public void Add(double[] values, double? stamp)
    {
        lock (_lock)
        {
            for (int i = 0; i < values.Length && i < MaxCurves; i++)
            {
                while (_curves.Count <= i)
                    _curves.Add(new Curve(_curves.Count) { MaxPoints = MaxPointsPerCurve });
                _curves[i].AddPoint(stamp ?? _nextX, values[i]);
                TotalPoints++;
            }
            if (stamp is null) _nextX++;
            Version++;
        }
    }

    public IReadOnlyList<Curve> Curves { get { lock (_lock) return _curves.ToArray(); } }

    public void ClearData()
    {
        lock (_lock)
        {
            foreach (var c in _curves) c.Clear();
            _nextX = 0;
            TotalPoints = 0;
            Version++;
        }
    }
}

/// <summary>
/// 绘图服务：窗口集合管理；第一个窗口为“默认绘图窗口”（模板），
/// 新窗口继承其 AutoY 等属性（PRD F4.2）。
/// </summary>
public sealed class PlotService
{
    private readonly object _lock = new();
    private readonly List<PlotWindow> _windows = [];
    private int _idSeq;

    public int MaxPointsPerCurve { get; set; } = 500_000;

    /// <summary>模板属性：新窗口继承该值（默认绘图窗口概念）。</summary>
    public bool DefaultAutoY { get; set; } = true;

    public event Action? WindowsChanged;

    public PlotWindow GetOrCreate(string title)
    {
        lock (_lock)
        {
            // 每个解析帧都会查询窗口；避免捕获 title 的 LINQ 闭包逐帧分配。
            // 直接读取当前 Title，保留外部重命名后的匹配语义。
            foreach (var existing in _windows)
                if (string.Equals(existing.Title, title, StringComparison.OrdinalIgnoreCase)) return existing;

            var template = _windows.Count > 0 ? _windows[0] : null;
            var win = new PlotWindow($"w{++_idSeq}", title, template?.AutoY ?? DefaultAutoY, MaxPointsPerCurve);
            _windows.Add(win);
            WindowsChanged?.Invoke();
            return win;
        }
    }

    public void AddPlotFrame(string title, double[] values, double? stamp)
        => GetOrCreate(title).Add(values, stamp);

    public IReadOnlyList<PlotWindow> SnapshotWindows() { lock (_lock) return _windows.ToArray(); }

    public PlotWindow? Find(string idOrTitle)
    {
        lock (_lock)
        {
            return _windows.FirstOrDefault(w => string.Equals(w.Id, idOrTitle, StringComparison.OrdinalIgnoreCase))
                ?? _windows.FirstOrDefault(w => string.Equals(w.Title, idOrTitle, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var w = _windows.FirstOrDefault(x => x.Id == id);
            if (w == null) return false;
            _windows.Remove(w);
            WindowsChanged?.Invoke();
            return true;
        }
    }

    public void ClearData() { lock (_lock) foreach (var w in _windows) w.ClearData(); }
    public void ClearAll() { lock (_lock) { _windows.Clear(); WindowsChanged?.Invoke(); } }
}
