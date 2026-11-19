using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Restores temporary test settings on completion, stop, or script reload.</summary>
public sealed class B2PlayCheckScope : IDisposable
{
    private readonly Dictionary<Behaviour, bool> _enabled = new Dictionary<Behaviour, bool>();
    private readonly bool _background;
    private readonly TypeWriterB1 _writer;
    private readonly bool _deleteEnabled;
    private bool _disposed;

    public B2PlayCheckScope(TypeWriterB1 writer)
    {
        if (!EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode != true)
            throw new InvalidOperationException("Checks may only change a running Play session.");
        _writer = writer;
        _deleteEnabled = writer.fingertipDeleteEnabled;
        _background = Application.runInBackground;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        AssemblyReloadEvents.beforeAssemblyReload += Dispose;
        foreach (var scheduler in UnityEngine.Object.FindObjectsByType<B1EventScheduler>(FindObjectsSortMode.None))
        {
            _enabled[scheduler] = scheduler.enabled;
            scheduler.enabled = false;
        }
        writer.fingertipDeleteEnabled = false;
        Application.runInBackground = true;
    }

    private void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode) Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _enabled) if (entry.Key != null) entry.Key.enabled = entry.Value;
        if (_writer != null) _writer.fingertipDeleteEnabled = _deleteEnabled;
        Application.runInBackground = _background;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
    }
}
