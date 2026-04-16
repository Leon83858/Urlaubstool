using Avalonia.Controls;

namespace Urlaubstool.App;

public partial class InfoWindow : Window
{
    public InfoWindow()
    {
        InitializeComponent();
    }

    public InfoWindow(bool openPatchNotesFirst) : this()
    {
        var tabControl = this.FindControl<TabControl>("InfoTabControl");
        if (tabControl != null)
        {
            tabControl.SelectedIndex = openPatchNotesFirst ? 0 : 1;
        }
    }
}
