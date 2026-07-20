using System;
using System.Collections.Generic;

namespace ArisenEngine.Core.Automation;

public class CommandManager : ICommandManager
{
    private readonly object m_Gate = new();
    private readonly Stack<ICommand> _undoStack = new();
    private readonly Stack<ICommand> _redoStack = new();
    private int _maxHistorySize = 100;

    public event Action<ICommand>? CommandExecuted;
    public event Action<ICommand>? CommandUndone;
    public event Action<ICommand>? CommandRedone;
    public event Action? StateChanged;

    public bool CanUndo
    {
        get
        {
            lock (m_Gate) return _undoStack.Count > 0;
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (m_Gate) return _redoStack.Count > 0;
        }
    }

    public int MaxHistorySize
    {
        get
        {
            lock (m_Gate) return _maxHistorySize;
        }
        set
        {
            lock (m_Gate) _maxHistorySize = value;
        }
    }

    public void Execute(ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (m_Gate)
        {
            command.Execute();
            _undoStack.Push(command);
            _redoStack.Clear();
        }
        
        CommandExecuted?.Invoke(command);
        StateChanged?.Invoke();
    }

    public void Undo()
    {
        ICommand command;
        lock (m_Gate)
        {
            if (_undoStack.Count == 0) return;

            command = _undoStack.Pop();
            try
            {
                command.Undo();
                _redoStack.Push(command);
            }
            catch
            {
                _undoStack.Push(command);
                throw;
            }
        }

        CommandUndone?.Invoke(command);
        StateChanged?.Invoke();
    }

    public void Redo()
    {
        ICommand command;
        lock (m_Gate)
        {
            if (_redoStack.Count == 0) return;

            command = _redoStack.Pop();
            try
            {
                command.Execute();
                _undoStack.Push(command);
            }
            catch
            {
                _redoStack.Push(command);
                throw;
            }
        }

        CommandRedone?.Invoke(command);
        StateChanged?.Invoke();
    }

    public void Clear()
    {
        lock (m_Gate)
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
        StateChanged?.Invoke();
    }
}
