using System;
using System.Collections.Generic;

namespace Revenant_Theme_Studio.Models;

public sealed class DisplaySlot
{
    public int SlotId { get; init; }
    public bool IsActive { get; set; }
    public DisplayInfo? Info { get; set; }
    public DisplayLayoutRect LayoutRect { get; set; } = new();
}

public sealed class DisplayInfo
{
    public string DeviceName { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int OrientationDegrees { get; init; }
}

public sealed class DisplayLayoutRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 240;
    public double Height { get; set; } = 135;
}

public enum RenderMode
{
    UnifiedCanvas,
    PerMonitor
}

public enum SpanBehavior
{
    Global,
    Grouped,
    Independent
}

public abstract class WallpaperContent
{
    public string Name { get; init; } = string.Empty;
}

public sealed class ImageContent : WallpaperContent
{
    public string ImagePath { get; init; } = string.Empty;
}

public sealed class SlideshowContent : WallpaperContent
{
    public IReadOnlyList<string> ImagePaths { get; init; } = Array.Empty<string>();
}

public sealed class SourceDrivenContent : WallpaperContent
{
    public string SourceGroupId { get; init; } = string.Empty;
}

public sealed class WebContent : WallpaperContent
{
    public string Url { get; init; } = string.Empty;
}

public sealed class VideoContent : WallpaperContent
{
    public string VideoPath { get; init; } = string.Empty;
}

public sealed class ShaderContent : WallpaperContent
{
    public string ShaderPath { get; init; } = string.Empty;
}

public enum SourceType
{
    Local,
    Web,
    Api
}

public sealed class WallpaperSource
{
    public string Id { get; init; } = string.Empty;
    public int Weight { get; init; }
    public SourceType Type { get; init; }
    public string Location { get; init; } = string.Empty;
}

public sealed class SourceGroup
{
    public string Id { get; init; } = string.Empty;
    public List<WallpaperSource> Sources { get; init; } = new();
}

public sealed class MonitorAssignment
{
    public int SlotId { get; init; }
    public WallpaperContent? Content { get; set; }
    public SourceGroup? Sources { get; set; }
}

public sealed class DisplayLayout
{
    public IReadOnlyList<DisplaySlot> Slots { get; init; } = Array.Empty<DisplaySlot>();
}

public sealed class InputState
{
    public double X { get; init; }
    public double Y { get; init; }
    public bool IsLeftButtonDown { get; init; }
}

public interface IRenderer
{
    void Initialize(DisplayLayout layout);
    void LoadContent(WallpaperContent content);
    void Start();
    void Stop();
    void UpdateInput(InputState input);
}

public sealed class StaticRenderer : IRenderer
{
    public void Initialize(DisplayLayout layout) { }
    public void LoadContent(WallpaperContent content) { }
    public void Start() { }
    public void Stop() { }
    public void UpdateInput(InputState input) { }
}

public sealed class WebRenderer : IRenderer
{
    public void Initialize(DisplayLayout layout) { }
    public void LoadContent(WallpaperContent content) { }
    public void Start() { }
    public void Stop() { }
    public void UpdateInput(InputState input) { }
}

public sealed class VideoRenderer : IRenderer
{
    public void Initialize(DisplayLayout layout) { }
    public void LoadContent(WallpaperContent content) { }
    public void Start() { }
    public void Stop() { }
    public void UpdateInput(InputState input) { }
}

public sealed class ShaderRenderer : IRenderer
{
    public void Initialize(DisplayLayout layout) { }
    public void LoadContent(WallpaperContent content) { }
    public void Start() { }
    public void Stop() { }
    public void UpdateInput(InputState input) { }
}

public sealed class ExternalRenderer : IRenderer
{
    public void Initialize(DisplayLayout layout) { }
    public void LoadContent(WallpaperContent content) { }
    public void Start() { }
    public void Stop() { }
    public void UpdateInput(InputState input) { }
}
