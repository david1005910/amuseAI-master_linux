using Amuse.UI.Linux.Views;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;

namespace Amuse.UI.Linux.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        [ObservableProperty]
        private View _currentView = View.TextToImage;

        [ObservableProperty]
        private ViewCategory _viewCategory = ViewCategory.Image;

        [ObservableProperty]
        private Control _activeContent;

        [ObservableProperty]
        private string _appVersion = "v3.5.3";

        [ObservableProperty]
        private string _statusText = "Ready";

        public MainWindowViewModel()
        {
            NavigateToView(View.TextToImage);
        }

        [RelayCommand]
        private void NavigateCategory(ViewCategory category)
        {
            ViewCategory = category;
            var view = ViewManager.GetCurrentView(category);
            NavigateToView(view);
        }

        [RelayCommand]
        private void Navigate(View view)
        {
            NavigateToView(view);
        }

        private void NavigateToView(View view)
        {
            CurrentView = view;
            ViewCategory = ViewManager.SetCurrentView(view);
            ActiveContent = ViewFactory.Create(view);
        }
    }
}
