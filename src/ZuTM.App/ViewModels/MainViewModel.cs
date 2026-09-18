// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Collections.ObjectModel;
using ZuTM.App.Mvvm;
using ZuTM.App.Services;


namespace ZuTM.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private VmItemViewModel? _selectedVm;
    private bool _isBusy;
    private string? _banner;

    public VmLibraryService Library { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    public AsyncRelayCommand StartSelectedCommand { get; }

    public AsyncRelayCommand StopSelectedCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public MainViewModel(VmLibraryService library)
    {
        Library = library;
        ReloadCommand = new AsyncRelayCommand(ReloadAsync);
        StartSelectedCommand = new AsyncRelayCommand(() => WithSelectedAsync(vm => Library.StartAsync(vm)),
            () => SelectedVm?.CanStart == true);
        StopSelectedCommand = new AsyncRelayCommand(() => WithSelectedAsync(vm => Library.StopAsync(vm)),
            () => SelectedVm?.CanStop == true);
        OpenFolderCommand = new RelayCommand(() => System.Diagnostics.Process.Start("explorer.exe", Library.Settings.VmFolder));
    }

    public ObservableCollection<VmItemViewModel> VirtualMachines => Library.VirtualMachines;

    public VmItemViewModel? SelectedVm
    {
        get => _selectedVm;
        set
        {
            if (SetProperty(ref _selectedVm, value))
            {
                StartSelectedCommand.RaiseCanExecuteChanged();
                StopSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? Banner
    {
        get => _banner;
        private set
        {
            if (SetProperty(ref _banner, value))
            {
                OnPropertyChanged(nameof(HasBanner));
            }
        }
    }

    public bool HasBanner => Banner is not null;

    public Task LoadAsync() => ReloadAsync();

    public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            await Library.ReloadAsync();
            Banner = Library.LoadError;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task WithSelectedAsync(Func<VmItemViewModel, Task> action)
    {
        if (SelectedVm is not { } vm)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action(vm);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose() => Library.Dispose();
}
