using Avalonia.Controls;

namespace Amuse.UI.Linux.Views
{
    public partial class StubView : UserControl
    {
        public StubView(string title)
        {
            InitializeComponent();
            TitleText.Text = title;
        }
    }
}
