using System.Windows.Input;
using System.Windows.Threading;
using VirtualPinballLauncher.Input;

namespace VirtualPinballLauncher;

/// <summary>
/// Owns the global keyboard hook for a <see cref="KeyBindingMap"/> and forwards chord
/// presses and modifier taps to the UI thread. Windows removes a low-level hook without
/// telling anyone if it ever stalls, so the host also watches for keys that reached the
/// launcher window without passing through the hook and reinstalls it when that happens.
/// </summary>
public sealed class KeyBindingHost : IDisposable
{
    private static readonly TimeSpan HookSilenceLimit = TimeSpan.FromSeconds(2);

    private readonly KeyBindingMap map;
    private readonly Dispatcher dispatcher;
    private readonly ChordListener listener;
    private LowLevelKeyboardHook hook;
    private bool disposed;

    public KeyBindingHost(KeyBindingMap map, Dispatcher dispatcher)
    {
        this.map = map;
        this.dispatcher = dispatcher;
        listener = new ChordListener(map.Chords);
        listener.ChordPressed += chord =>
        {
            if (map.TryGetChord(chord, out var binding))
            {
                LauncherLog.Info($"Chord {chord} -> {binding.Action}");
                dispatcher.BeginInvoke(() => ChordTriggered?.Invoke(binding));
            }
        };
        listener.ModifierTapped += virtualKey =>
        {
            var key = KeyNames.FromVirtualKey(virtualKey);
            LauncherLog.Info($"Modifier tap {KeyNames.ToName(key)}");
            dispatcher.BeginInvoke(() => ModifierTapped?.Invoke(key));
        };
        listener.StaleModifierDropped += virtualKey =>
            LauncherLog.Info($"Dropped stale modifier {KeyNames.ToName(KeyNames.FromVirtualKey(virtualKey))} (its release was never seen)");
        hook = new LowLevelKeyboardHook(listener.Handle);
    }

    /// <summary>Raised on the UI thread when a bound chord is pressed anywhere.</summary>
    public event Action<ResolvedBinding>? ChordTriggered;

    /// <summary>Raised on the UI thread when a chord modifier is pressed and released on its own.</summary>
    public event Action<Key>? ModifierTapped;

    public bool HookActive => hook.IsActive;

    public bool IsModifierHeld => listener.IsModifierHeld;

    /// <summary>Installs the hook when there are chords to listen for. Returns false if Windows refused it.</summary>
    public bool Start()
    {
        if (!listener.HasChords) return true;
        var ok = hook.Start();
        LauncherLog.Info(ok ? $"Keyboard hook installed for {map.Chords.Count} chord(s)." : "Keyboard hook was refused by Windows.");
        return ok;
    }

    /// <summary>
    /// Call when the launcher window itself receives a key. Every key the window sees
    /// must have passed through the hook first, so a silent hook means it was removed.
    /// </summary>
    public void NoteWindowKeyEvent()
    {
        if (disposed || !listener.HasChords) return;
        var last = hook.LastEventTicks;
        var silentFor = last == 0 ? TimeSpan.MaxValue : TimeSpan.FromTicks(DateTime.UtcNow.Ticks - last);
        if (hook.IsActive && silentFor < HookSilenceLimit) return;

        LauncherLog.Info($"Keyboard hook missed a key the window received (active {hook.IsActive}, silent for {(silentFor == TimeSpan.MaxValue ? "ever" : silentFor.TotalSeconds.ToString("0.0") + "s")}); reinstalling.");
        hook.Dispose();
        hook = new LowLevelKeyboardHook(listener.Handle);
        if (!hook.Start())
            LauncherLog.Info("Keyboard hook reinstall was refused by Windows.");
    }

    public void Dispose()
    {
        disposed = true;
        hook.Dispose();
    }
}
