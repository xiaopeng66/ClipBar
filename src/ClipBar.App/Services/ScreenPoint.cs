namespace ClipBar.App.Services;

/// <summary>A point in physical screen pixels, safe to use in the app's public surface.</summary>
public readonly record struct ScreenPoint(int X, int Y);
