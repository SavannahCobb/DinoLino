using DinoLino.Utilities.Modes;
using System.Windows;
using System.Windows.Controls;

namespace DinoLino.Utilities.Modes
{
    public partial class TriangleControlPanel : UserControl
    {
        private readonly GetAngleMode _mode;

        public TriangleControlPanel(GetAngleMode mode)
        {
            InitializeComponent();

            _mode = mode;
            DataContext = mode;

            // The panel is built again every time the tab is opened, so the tool the
            // mode is on is ticked here rather than in the markup.
            if (mode.IsAxisAngleSelected) UI_AxisAngleOption.IsChecked = true;
            else UI_TriangleOption.IsChecked = true;
        }

        private void AngleMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton button) _mode.SelectAngleMethod(button.Tag?.ToString());
        }
    }
}
