using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Zashboard.App.ViewModels;

namespace Zashboard.App.Controls;

/// <summary>
/// Disables controller actions without changing their capability bindings or their surrounding list.
/// </summary>
public sealed partial class UserOperationScope : ContentControl
{
    private Page? _page;
    private ViewModelBase? _viewModel;

    public UserOperationScope()
    {
        DefaultStyleKey = typeof(ContentControl);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Detach();
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this);
            parent is not null;
            parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is Page page)
            {
                _page = page;
                _page.DataContextChanged += OnPageDataContextChanged;
                AttachViewModel();
                return;
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => Detach();

    private void OnPageDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        AttachViewModel();

    private void AttachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = _page?.DataContext as ViewModelBase;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateAvailability();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModelBase.CanStartUserOperation))
        {
            UpdateAvailability();
        }
    }

    private void UpdateAvailability() => IsEnabled = _viewModel?.CanStartUserOperation == true;

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        if (_page is not null)
        {
            _page.DataContextChanged -= OnPageDataContextChanged;
            _page = null;
        }
    }
}
