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

    // Never raised (the tray menu has no per-item CanExecute state) - the empty
    // accessors silence CS0067, per the usual ICommand implementation pattern.
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
