using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ForceBorderlessWindowed.Controls;

/// <summary>
/// An image that only decorates (e.g. a program icon next to its name): hidden from screen readers,
/// which would otherwise announce an unnamed "image".
/// </summary>
public class DecorativeImage : Image
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
