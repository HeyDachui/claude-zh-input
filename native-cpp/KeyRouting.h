#pragma once
#include <windows.h>

namespace InputRouting
{
inline bool Printable(UINT key)
{
    return (key >= 'A' && key <= 'Z') || (key >= '0' && key <= '9') || key == VK_SPACE ||
        (key >= VK_NUMPAD0 && key <= VK_DIVIDE) ||
        (key >= VK_OEM_1 && key <= VK_OEM_3) ||
        (key >= VK_OEM_4 && key <= VK_OEM_8) || key == VK_OEM_102;
}

inline bool Eat(UINT key, bool enabled, bool ctrl, bool alt, bool win,
                bool normal, bool ascii, bool composing, bool commands, bool prefix, bool pending = false)
{
    if (!enabled || alt || win) return false;
    if (key == VK_SHIFT || key == VK_LSHIFT || key == VK_RSHIFT) return true;
    if (ctrl) return key == VK_OEM_1;
    if (commands || prefix) return Printable(key) || key == VK_BACK || key == VK_ESCAPE || key == VK_RETURN;
    if (normal && ascii && !pending) return false;
    // A rapid key after Shift must wait in the same ordered engine queue.
    // The previous mode acknowledgement may still be in flight.
    if (pending && Printable(key)) return true;
    if (!normal && key == VK_OEM_3) return true;
    if (key == VK_OEM_1 && !ascii && !composing) return true;
    if (key >= 'A' && key <= 'Z') return true;
    if (key == VK_SPACE || key == VK_RETURN || key == VK_BACK || key == VK_ESCAPE) return composing;
    if (key == VK_PRIOR || key == VK_NEXT || key == VK_UP || key == VK_DOWN ||
        key == VK_LEFT || key == VK_RIGHT || key == VK_HOME || key == VK_END || key == VK_DELETE) return composing;
    // Every printable key in an active composition must reach the engine:
    // zero, underscore, plus, digits and code punctuation included.
    return Printable(key) && (composing || (!normal && ascii));
}
inline UINT LetterOrDigit(UINT key, bool physicalShift, bool caps)
{
    if (key >= 'A' && key <= 'Z') return physicalShift != caps ? key : key + ('a' - 'A');
    if (key >= '0' && key <= '9') return physicalShift ? ")!@#$%^&*("[key - '0'] : key;
    return 0;
}
inline bool LogicalAfterState(bool hasComposition, long long response, long long sent)
{
    return hasComposition || response < sent;
}
}
