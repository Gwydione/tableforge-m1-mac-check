using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace E3.Editor;

/// <summary>What a formatting or history key does in a result editor.</summary>
public enum EditorCommand { Bold, Italic, Clear, Undo, Redo }

/// <summary>
/// The editor's keyboard shortcuts, platform-aware. Bold/Italic use the platform's command modifier (Ctrl on Windows and
/// Linux, ⌘ on macOS, from Avalonia's <see cref="PlatformHotkeyConfiguration.CommandModifiers"/>); Undo/Redo use the
/// platform's own gestures (Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z, or ⌘Z / ⌘⇧Z). Clear Formatting is Ctrl+Space as in 1.0.0 —
/// except on macOS, where ⌘Space is Spotlight and never reaches the app, so ⌘\ (the usual "clear formatting" key in macOS
/// editors) is used instead.
/// </summary>
public static class FormattingShortcuts
{
    public static EditorCommand? Match(Visual source, KeyEventArgs e)
    {
        var hotkeys = (source.GetPlatformSettings() ?? Application.Current?.PlatformSettings)?.HotkeyConfiguration;
        return hotkeys is null
            ? Match(KeyModifiers.Control, null, null, e.Key, e.KeyModifiers)
            : Match(hotkeys.CommandModifiers, hotkeys.Undo, hotkeys.Redo, e.Key, e.KeyModifiers);
    }

    /// <summary>
    /// The mapping itself, for one platform's command modifier and undo/redo gestures (null gestures: the Windows/Linux
    /// defaults Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z).
    /// </summary>
    public static EditorCommand? Match(KeyModifiers command, IEnumerable<KeyGesture>? undo, IEnumerable<KeyGesture>? redo, Key key, KeyModifiers modifiers)
    {
        var macOS = command == KeyModifiers.Meta;
        undo ??= [new KeyGesture(Key.Z, KeyModifiers.Control)];
        redo ??= [new KeyGesture(Key.Y, KeyModifiers.Control), new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift)];
        if (undo.Any(g => g.Key == key && g.KeyModifiers == modifiers)) return EditorCommand.Undo;
        if (redo.Any(g => g.Key == key && g.KeyModifiers == modifiers)) return EditorCommand.Redo;

        if (modifiers != command) return null;
        return key switch
        {
            Key.B => EditorCommand.Bold,
            Key.I => EditorCommand.Italic,
            Key.Space when !macOS => EditorCommand.Clear,
            Key.OemBackslash or Key.Oem5 when macOS => EditorCommand.Clear,
            _ => null,
        };
    }
}
