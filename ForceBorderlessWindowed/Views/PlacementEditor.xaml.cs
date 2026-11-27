using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ForceBorderlessWindowed.Views;

/// <summary>Reusable editor for a placement. DataContext: a PlacementEditorViewModel.</summary>
public partial class PlacementEditor : UserControl
{
    public PlacementEditor()
    {
        InitializeComponent();
    }

    /// <summary>The presets button opens its menu below itself on a click, like the Add button of the Games tab.</summary>
    private void OnPresetsClick(object sender, RoutedEventArgs e)
    {
        if (PresetsButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = PresetsButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
