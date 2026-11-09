using System.Windows.Input;

namespace Sexy;

/// <summary>Minimal ICommand implementation for tray menu items (the Win32 popup menu calls commands, not Click handlers).</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
