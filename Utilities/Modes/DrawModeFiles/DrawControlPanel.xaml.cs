using DinoLino.Utilities.Modes;
using System.Windows;
using System.Windows.Controls;

namespace DinoLino.Utilities.Modes  
{                                    
    public partial class DrawControlPanel : UserControl
    {
        private DrawMode _mode;

        public DrawControlPanel(DrawMode mode)
        {
            // Before the XAML is loaded, not after: a button that starts out chosen
            // raises its Checked while the panel is still being built, and the handler
            // that answers it has nothing to tell until the mode is in hand.
            _mode = mode;

            InitializeComponent();
            DataContext = mode;
        }

        private void DrawMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb) _mode.SelectDrawMethod(rb.Tag?.ToString());
        }

        private void Shape_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb) _mode.SelectShape(rb.Tag?.ToString());
        }

        private void LabelKind_Checked(object sender, RoutedEventArgs e)
        {
            if (_mode != null && sender is RadioButton rb)
                _mode.SelectLabelKind(rb.Tag?.ToString());
        }

        private void LineConstraint_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb) _mode.SelectLineConstraint(rb.Tag?.ToString());
        }

        private void DrawAngleValue_TextChanged(object sender, TextChangedEventArgs e)
            => _mode.UpdateAngle(UI_DrawAngleValue.Text);
    }                                
}                                    