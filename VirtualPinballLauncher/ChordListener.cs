namespace VirtualPinballLauncher;

/// <summary>
/// Turns a raw stream of key down/up events into chord presses and modifier taps.
/// The modifier key itself is never swallowed, so a bare press still reaches whatever
/// is focused. Its single-key meaning fires on release, and only if no chord was
/// pressed while it was held. Chorded keys are swallowed for both down and up.
///
/// Key releases can go missing (a game switching display modes, a hook briefly
/// removed), so the listener never trusts its own memory for long: the held modifier
/// is checked against the physical key state on every event, and swallowed keys are
/// forgotten whenever the modifier changes.
///
/// <see cref="Handle"/> is meant to be called from a single thread (the hook thread);
/// <see cref="IsModifierHeld"/> and <see cref="IsChordHeld"/> may be read from any thread.
/// </summary>
public sealed class ChordListener
{
    private readonly HashSet<int> modifiers = new();
    private readonly Dictionary<(int Modifier, int Key), KeyChord> chords = new();
    private readonly HashSet<int> swallowedKeys = new();
    private readonly Func<int, bool> isPhysicallyDown;
    private int heldModifier;
    private bool chordFiredDuringHold;

    public ChordListener(IEnumerable<KeyChord> chordSet, Func<int, bool>? isPhysicallyDown = null)
    {
        this.isPhysicallyDown = isPhysicallyDown ?? KeyState.IsPhysicallyDown;
        foreach (var chord in chordSet)
        {
            if (!chord.IsChord) continue;
            modifiers.Add(chord.ModifierVirtualKey);
            chords[(chord.ModifierVirtualKey, chord.VirtualKey)] = chord;
        }
    }

    /// <summary>Raised on the calling thread when a chord key is pressed while its modifier is held.</summary>
    public event Action<KeyChord>? ChordPressed;

    /// <summary>Raised on the calling thread when a modifier is released without any chord having fired.</summary>
    public event Action<int>? ModifierTapped;

    /// <summary>Raised when a stale held modifier was dropped because the key is no longer physically down.</summary>
    public event Action<int>? StaleModifierDropped;

    public bool IsModifierHeld => Volatile.Read(ref heldModifier) != 0;

    /// <summary>
    /// Whether a chord that fired is still being held: its modifier is the held one and
    /// physically down, and its key has not been released since it fired. Callers cannot
    /// ask Windows about the chord key themselves, because a key the hook swallows never
    /// reaches the key-state table that GetAsyncKeyState reads.
    /// </summary>
    public bool IsChordHeld(KeyChord chord)
    {
        if (!chord.IsChord || Volatile.Read(ref heldModifier) != chord.ModifierVirtualKey) return false;
        lock (swallowedKeys)
        {
            if (!swallowedKeys.Contains(chord.VirtualKey)) return false;
        }
        return isPhysicallyDown(chord.ModifierVirtualKey);
    }

    public bool IsModifier(int virtualKey) => modifiers.Contains(virtualKey);

    public bool HasChords => chords.Count > 0;

    /// <summary>Feeds one key event. Returns true when the event should be swallowed.</summary>
    public bool Handle(int virtualKey, bool isDown)
    {
        var held = Volatile.Read(ref heldModifier);

        // A modifier whose release was never seen must not keep every other key hostage.
        if (held != 0 && virtualKey != held && !isPhysicallyDown(held))
        {
            Volatile.Write(ref heldModifier, 0);
            ClearSwallowed();
            StaleModifierDropped?.Invoke(held);
            held = 0;
        }

        if (isDown)
        {
            if (IsSwallowed(virtualKey))
                return true; // auto-repeat of a chord key that already fired

            // A key that completes the held chord is a chord key first, even if it is a
            // modifier in its own right (5 completes F12+5 and also starts 5+Left).
            if (held != 0 && chords.TryGetValue((held, virtualKey), out var chord))
            {
                chordFiredDuringHold = true;
                lock (swallowedKeys) swallowedKeys.Add(virtualKey);
                ChordPressed?.Invoke(chord);
                return true;
            }

            if (modifiers.Contains(virtualKey))
            {
                if (held == 0)
                {
                    chordFiredDuringHold = false;
                    ClearSwallowed();
                    Volatile.Write(ref heldModifier, virtualKey);
                }
                return false;
            }

            return false;
        }

        if (virtualKey == held)
        {
            Volatile.Write(ref heldModifier, 0);
            ClearSwallowed();
            if (!chordFiredDuringHold)
                ModifierTapped?.Invoke(virtualKey);
            return false;
        }

        lock (swallowedKeys) return swallowedKeys.Remove(virtualKey);
    }

    private bool IsSwallowed(int virtualKey)
    {
        lock (swallowedKeys) return swallowedKeys.Contains(virtualKey);
    }

    private void ClearSwallowed()
    {
        lock (swallowedKeys) swallowedKeys.Clear();
    }
}
