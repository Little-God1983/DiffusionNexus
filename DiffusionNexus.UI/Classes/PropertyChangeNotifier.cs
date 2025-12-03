using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace DiffusionNexus.UI.Classes;

/// <summary>
/// Helper to batch and debounce property change notifications
/// </summary>
public class PropertyChangeNotifier
{
    private readonly INotifyPropertyChanged _owner;
    private readonly Action<string?> _raisePropertyChanged;
    private readonly HashSet<string> _pendingProperties = new();
    private bool _isSuspended;

    public PropertyChangeNotifier(INotifyPropertyChanged owner, Action<string?> raisePropertyChanged)
    {
        _owner = owner;
        _raisePropertyChanged = raisePropertyChanged;
    }

    /// <summary>
    /// Suspend notifications until disposed
    /// </summary>
    public IDisposable SuspendNotifications()
    {
        return new SuspensionScope(this);
    }

    /// <summary>
    /// Queue a property change notification
    /// </summary>
    public void NotifyPropertyChanged(string? propertyName)
    {
        if (_isSuspended)
        {
            if (propertyName != null)
            {
                _pendingProperties.Add(propertyName);
            }
        }
        else
        {
            _raisePropertyChanged(propertyName);
        }
    }

    /// <summary>
    /// Flush all pending notifications
    /// </summary>
    private void Flush()
    {
        if (_pendingProperties.Count == 0)
            return;

        foreach (var property in _pendingProperties)
        {
            _raisePropertyChanged(property);
        }
        _pendingProperties.Clear();
    }

    private class SuspensionScope : IDisposable
    {
        private readonly PropertyChangeNotifier _notifier;
        private bool _disposed;

        public SuspensionScope(PropertyChangeNotifier notifier)
        {
            _notifier = notifier;
            _notifier._isSuspended = true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _notifier._isSuspended = false;
            _notifier.Flush();
        }
    }
}
