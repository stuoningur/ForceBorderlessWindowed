using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ForceBorderlessWindowed.Controls;

/// <summary>
/// A Segoe Fluent Icons glyph (style: IconTextStyle). Decoration only: hidden from screen readers,
/// which would otherwise read the private-use character as gibberish. The text or name next to it
/// always says the same thing.
/// </summary>
public class FontIcon : TextBlock
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
