using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.ViewModels;

public sealed class WallpaperViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DisplayService _displayService;
    private readonly WallpaperService _wallpaperService;
    private readonly RotationEngine _rotationEngine;

    private RenderMode _selectedRenderMode;
    private SpanBehavior _selectedSpanBehavior;
    private DisplaySlotViewModel? _selectedSlot;
    private string _localSourcePath = string.Empty;
    private string _statusMessage = "Wallpaper system ready.";

    public WallpaperViewModel()
    {
        _displayService = new DisplayService();
        _wallpaperService = new WallpaperService();
        _rotationEngine = new RotationEngine(_wallpaperService, new WeightResolver(), new ISourceProvider[] { new LocalFolderSourceProvider() });
        _rotationEngine.WallpaperResolved += (_, message) => StatusMessage = message;

        RenderModes = new ObservableCollection<RenderMode>((RenderMode[])Enum.GetValues(typeof(RenderMode)));
        SpanBehaviors = new ObservableCollection<SpanBehavior>((SpanBehavior[])Enum.GetValues(typeof(SpanBehavior)));
        DisplaySlots = new ObservableCollection<DisplaySlotViewModel>();

        _selectedRenderMode = _wallpaperService.RenderMode;
        _selectedSpanBehavior = _wallpaperService.SpanBehavior;

        RefreshDisplays();
    }

    public ObservableCollection<DisplaySlotViewModel> DisplaySlots { get; }
    public ObservableCollection<RenderMode> RenderModes { get; }
    public ObservableCollection<SpanBehavior> SpanBehaviors { get; }

    public RenderMode SelectedRenderMode
    {
        get => _selectedRenderMode;
        set
        {
            if (_selectedRenderMode == value) return;
            _selectedRenderMode = value;
            _wallpaperService.RenderMode = value;
            OnPropertyChanged();
        }
    }

    public SpanBehavior SelectedSpanBehavior
    {
        get => _selectedSpanBehavior;
        set
        {
            if (_selectedSpanBehavior == value) return;
            _selectedSpanBehavior = value;
            _wallpaperService.SpanBehavior = value;
            OnPropertyChanged();
        }
    }

    public DisplaySlotViewModel? SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            if (_selectedSlot == value) return;
            _selectedSlot = value;
            OnPropertyChanged();
        }
    }

    public string LocalSourcePath
    {
        get => _localSourcePath;
        set
        {
            if (_localSourcePath == value) return;
            _localSourcePath = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage == value) return;
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public void RefreshDisplays()
    {
        _displayService.RefreshDisplays();
        DisplaySlots.Clear();

        foreach (var slot in _displayService.GetSlots())
        {
            DisplaySlots.Add(new DisplaySlotViewModel(slot));
        }

        SelectedSlot ??= DisplaySlots.FirstOrDefault(x => x.IsActive);
    }

    public void UpdateSlotPosition(int slotId, double x, double y)
    {
        _displayService.UpdateLayoutRect(slotId, x, y);
        var slot = DisplaySlots.FirstOrDefault(s => s.SlotId == slotId);
        if (slot is null) return;

        slot.X = x;
        slot.Y = y;
        StatusMessage = $"Moved display slot {slotId} to ({x:0}, {y:0}).";
    }

    public void AssignLocalFolderToSelectedSlot()
    {
        if (SelectedSlot is null)
        {
            StatusMessage = "Select a display slot before assigning wallpaper content.";
            return;
        }

        if (!Directory.Exists(LocalSourcePath))
        {
            StatusMessage = "Local source path does not exist.";
            return;
        }

        var content = new SourceDrivenContent
        {
            Name = $"Source Group for Slot {SelectedSlot.SlotId}",
            SourceGroupId = $"slot-{SelectedSlot.SlotId}-group"
        };

        var group = new SourceGroup
        {
            Id = content.SourceGroupId,
            Sources =
            {
                new WallpaperSource
                {
                    Id = $"slot-{SelectedSlot.SlotId}-local",
                    Weight = 100,
                    Type = SourceType.Local,
                    Location = LocalSourcePath
                }
            }
        };

        _wallpaperService.AssignContent(SelectedSlot.SlotId, content, group);
        StatusMessage = $"Assigned local folder source to slot {SelectedSlot.SlotId}.";
    }

    public void RunRotationTick()
    {
        _rotationEngine.Tick();
    }

    public void Dispose()
    {
        _rotationEngine.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class DisplaySlotViewModel : INotifyPropertyChanged
{
    private double _x;
    private double _y;

    public DisplaySlotViewModel(DisplaySlot slot)
    {
        SlotId = slot.SlotId;
        IsActive = slot.IsActive;
        Label = slot.Info is null
            ? $"Display {slot.SlotId} (inactive)"
            : $"Display {slot.SlotId}: {slot.Info.Width}x{slot.Info.Height}";
        Width = slot.LayoutRect.Width;
        Height = slot.LayoutRect.Height;
        _x = slot.LayoutRect.X;
        _y = slot.LayoutRect.Y;
    }

    public int SlotId { get; }
    public bool IsActive { get; }
    public string Label { get; }
    public double Width { get; }
    public double Height { get; }

    public double X
    {
        get => _x;
        set
        {
            if (Math.Abs(_x - value) < 0.1) return;
            _x = value;
            OnPropertyChanged();
        }
    }

    public double Y
    {
        get => _y;
        set
        {
            if (Math.Abs(_y - value) < 0.1) return;
            _y = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
