using System;
using System.Collections.Generic;

namespace ArisenEngine.Core.Automation;

public class CommandManager : ICommandManager
{
    private readonly Stack<ICommand> _undoStack = new();
    private readonly Stack<ICommand> _redoStack = new();
    private int _maxHistorySize = 100;

    public event Action<ICommand>? CommandExecuted;
    public event Action<ICommand>? CommandUndone;
    public event Action<ICommand>? CommandRedone;
    public event Action? StateChanged;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public int MaxHistorySize
    {
        get => _maxHistorySize;
        set => _maxHistorySize = value;
    }

    public void Execute(ICommand command)
    {
        command.Execute();
        _undoStack.Push(command);
        _redoStack.Clear(); // Invalidate redo stack upon new execution
        
        CommandExecuted?.Invoke(command);
        StateChanged?.Invoke();
    }

    public void Undo()
    {
        if (CanUndo)
        {
            var command = _undoStack.Pop();
            command.Undo();
            _redoStack.Push(command);
            
            CommandUndone?.Invoke(command);
            StateChanged?.Invoke();
        }
    }

    public void Redo()
    {
        if (CanRedo)
        {
            var command = _redoStack.Pop();
            command.Execute();
            _undoStack.Push(command);
            
            CommandRedone?.Invoke(command);
            StateChanged?.Invoke();
        }
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        StateChanged?.Invoke();
    }
}
