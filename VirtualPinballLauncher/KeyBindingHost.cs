using System.Windows.Input;
using System.Windows.Threading;

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

    private readonly Dispatcher dispatcher;
    private readonly ChordListener listener;
    private LowLevelKeyboardHook hook;
    private bool disposed;

    public KeyBindingHost(KeyBindingMap map, Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        listener = new ChordListener(map.Chords);
        listener.ChordPressed += chord =>
        {
            if (map.TryGetChord(chord, out var binding))
                dispatcher.BeginInvoke(() => ChordTriggered?.Invoke(binding));
        };
        listener.ModifierTapped += virtualKey =>
        {
            var key = KeyNames.FromVirtualKey(virtualKey);
            dispatcher.BeginInvoke(() => ModifierTapped?.Invoke(key));
        };
        hook = new LowLevelKeyboardHook(listener.Handle);
    }

    /// <summary>Raised on the UI thread when a bound chord is pressed anywhere.</summary>
    public event Action<ResolvedBinding>? ChordTriggered;

    /// <summary>Raised on the UI thread when a chord modifier is pressed and released on its own.</summary>
    public event Action<Key>? ModifierTapped;

    public bool HookActive => hook.IsActive;

    public bool IsModifierHeld => listener.IsModifierHeld;

    /// <summary>Whether a chord that fired is still held down; safe to call from the UI thread.</summary>
    public bool IsChordHeld(KeyChord chord) => listener.IsChordHeld(chord);

    /// <summary>Installs the hook when there are chords to listen for. Returns false if Windows refused it.</summary>
    public bool Start()
    {
        if (!listener.HasChords) return true;
        return hook.Start();
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

        // The hook missed a key the window received, so reinstall it.
        hook.Dispose();
        hook = new LowLevelKeyboardHook(listener.Handle);
        hook.Start();
    }

    public void Dispose()
    {
        disposed = true;
        hook.Dispose();
    }
}
