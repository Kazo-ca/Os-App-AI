// Kazo AI window helpers: things the browser does and C# cannot.

// Keeps the message list at its end while an answer is written, unless the person scrolled up
// to read something (more than 80 px from the end).
const following = new WeakMap();

export function followEnd(element) {
    if (!element) {
        return;
    }

    if (!following.has(element)) {
        following.set(element, true);
        element.addEventListener('scroll', () => {
            following.set(element, element.scrollHeight - element.scrollTop - element.clientHeight < 80);
        }, { passive: true });
    }

    if (following.get(element)) {
        element.scrollTop = element.scrollHeight;
    }
}

// Enter sends the message (the window's C# handler does it): the browser must not also write a
// new line in the box. Shift+Enter, and Enter while an input method composes, keep their default.
export function bindComposer(textarea) {
    if (!textarea) {
        return;
    }

    textarea.addEventListener('keydown', event => {
        if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
            event.preventDefault();
        }
    });
}
