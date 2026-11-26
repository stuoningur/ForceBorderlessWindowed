using System.Windows;

namespace ForceBorderlessWindowed.Services;

public interface IWindowPlacementStore
{
    /// <summary>
    /// Restores the window's last size/position (if known) when it opens, and remembers it when it closes.
    /// Call in the window's constructor, after its own Closing handlers are subscribed.
    /// </summary>
    void Track(Window window, string name);
}
