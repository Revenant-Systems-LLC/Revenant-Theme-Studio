using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using System.Windows.Threading;
using Revenant_Theme_Studio.Models;

namespace Revenant_Theme_Studio.Services;

public interface IDisplayService
{
    IReadOnlyList<DisplaySlot> GetSlots();
    void RefreshDisplays();
    void UpdateLayoutRect(int slotId, double x, double y);
}

public sealed class DisplayService : IDisplayService
{
    private const int MaxDisplays = 4;
    private readonly List<DisplaySlot> _slots;
    private readonly string _layoutPath;

    public DisplayService()
    {
        _slots = Enumerable.Range(1, MaxDisplays).Select(id => new DisplaySlot { SlotId = id }).ToList();
        _layoutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevenantThemeStudio", "display-layout.json");
        LoadPersistedLayout();
        RefreshDisplays();
    }

    public IReadOnlyList<DisplaySlot> GetSlots() => _slots;

    public void RefreshDisplays()
    {
        var detected = System.Windows.Forms.Screen.AllScreens
            .OrderBy(s => s.Bounds.X)
            .ThenBy(s => s.Bounds.Y)
            .Take(MaxDisplays)
            .ToList();

        for (var i = 0; i < MaxDisplays; i++)
        {
            var slot = _slots[i];
            if (i >= detected.Count)
            {
                slot.IsActive = false;
                slot.Info = null;
                continue;
            }

            var screen = detected[i];
            slot.IsActive = true;
            slot.Info = new DisplayInfo
            {
                DeviceName = screen.DeviceName,
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                X = screen.Bounds.X,
                Y = screen.Bounds.Y,
                OrientationDegrees = 0
            };

            if (slot.LayoutRect.Width <= 0 || slot.LayoutRect.Height <= 0)
            {
                slot.LayoutRect.Width = Math.Max(140, screen.Bounds.Width / 8d);
                slot.LayoutRect.Height = Math.Max(80, screen.Bounds.Height / 8d);
            }
        }

        PersistLayout();
    }

    public void UpdateLayoutRect(int slotId, double x, double y)
    {
        var slot = _slots.FirstOrDefault(s => s.SlotId == slotId);
        if (slot is null)
        {
            throw new ArgumentOutOfRangeException(nameof(slotId), $"Display slot {slotId} does not exist.");
        }

        slot.LayoutRect.X = x;
        slot.LayoutRect.Y = y;
        PersistLayout();
    }

    private void PersistLayout()
    {
        var directory = Path.GetDirectoryName(_layoutPath);
        if (string.IsNullOrWhiteSpace(directory)) return;

        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(_slots.Select(s => new PersistedSlot
        {
            SlotId = s.SlotId,
            X = s.LayoutRect.X,
            Y = s.LayoutRect.Y,
            Width = s.LayoutRect.Width,
            Height = s.LayoutRect.Height
        }).ToList());
        File.WriteAllText(_layoutPath, json);
    }

    private void LoadPersistedLayout()
    {
        if (!File.Exists(_layoutPath)) return;

        var persisted = JsonSerializer.Deserialize<List<PersistedSlot>>(File.ReadAllText(_layoutPath));
        if (persisted is null) return;

        foreach (var entry in persisted)
        {
            var slot = _slots.FirstOrDefault(s => s.SlotId == entry.SlotId);
            if (slot is null) continue;
            slot.LayoutRect = new DisplayLayoutRect { X = entry.X, Y = entry.Y, Width = entry.Width, Height = entry.Height };
        }
    }

    private sealed class PersistedSlot
    {
        public int SlotId { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}

public interface ISourceProvider
{
    SourceType SupportedType { get; }
    IReadOnlyList<string> GetCandidates(WallpaperSource source);
}

public sealed class WeightResolver
{
    public WallpaperSource ResolveSource(IReadOnlyList<WallpaperSource> sources, Random random)
    {
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("At least one source is required for weighted selection.");
        }

        var totalWeight = sources.Sum(x => Math.Max(0, x.Weight));
        if (totalWeight <= 0)
        {
            throw new InvalidOperationException("At least one source must have a positive weight.");
        }

        var roll = random.Next(1, totalWeight + 1);
        var cumulative = 0;
        foreach (var source in sources)
        {
            cumulative += Math.Max(0, source.Weight);
            if (roll <= cumulative)
            {
                return source;
            }
        }

        return sources[^1];
    }
}

public sealed class WallpaperService
{
    private readonly Dictionary<int, MonitorAssignment> _assignments = new();

    public RenderMode RenderMode { get; set; } = RenderMode.UnifiedCanvas;
    public SpanBehavior SpanBehavior { get; set; } = SpanBehavior.Global;

    public IReadOnlyCollection<MonitorAssignment> Assignments => _assignments.Values;

    public MonitorAssignment GetOrCreateAssignment(int slotId)
    {
        if (!_assignments.TryGetValue(slotId, out var assignment))
        {
            assignment = new MonitorAssignment { SlotId = slotId };
            _assignments[slotId] = assignment;
        }

        return assignment;
    }

    public void AssignContent(int slotId, WallpaperContent content, SourceGroup sources)
    {
        var assignment = GetOrCreateAssignment(slotId);
        assignment.Content = content;
        assignment.Sources = sources;
    }
}

public sealed class RotationEngine : IDisposable
{
    private readonly WallpaperService _wallpaperService;
    private readonly WeightResolver _weightResolver;
    private readonly Dictionary<SourceType, ISourceProvider> _providers;
    private readonly Random _random = new();
    private DispatcherTimer _timer;

    public RotationEngine(WallpaperService wallpaperService, WeightResolver weightResolver, IEnumerable<ISourceProvider> providers)
    {
        _wallpaperService = wallpaperService;
        _weightResolver = weightResolver;
        _providers = providers.ToDictionary(p => p.SupportedType);
    }

    public event EventHandler<string>? WallpaperResolved;

    public void Start(TimeSpan interval)
	{
		if (_timer != null) return;

		_timer = new DispatcherTimer();
		_timer.Interval = interval;
		_timer.Tick += (s, e) => Tick();
		_timer.Start();
	}

    public void Stop()
	{
		if (_timer == null) return;

		_timer.Stop();
		_timer = null;
	}

    public void Tick()
    {
        if (_wallpaperService.Assignments.Count == 0) return;

        var targetAssignments = _wallpaperService.RenderMode == RenderMode.UnifiedCanvas
            ? _wallpaperService.Assignments.Take(1)
            : _wallpaperService.Assignments;

        foreach (var assignment in targetAssignments)
        {
            if (assignment.Sources is null || assignment.Sources.Sources.Count == 0) continue;
            var selectedSource = _weightResolver.ResolveSource(assignment.Sources.Sources, _random);
            if (!_providers.TryGetValue(selectedSource.Type, out var provider)) continue;

            var candidates = provider.GetCandidates(selectedSource);
            if (candidates.Count == 0) continue;

            var selected = candidates[_random.Next(candidates.Count)];
            WallpaperResolved?.Invoke(this, $"Slot {assignment.SlotId} => {selected}");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
