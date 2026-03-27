// Focus a DOM element by its ID
window.focusInput = (id) => {
    const el = document.getElementById(id);
    if (el) el.focus();
};

// Scroll an element into view by ID
window.scrollIntoView = (id) => {
    const el = document.getElementById(id);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
};

// localStorage helpers for saved places
window.localStorageGet = (key) => localStorage.getItem(key);
window.localStorageSet = (key, value) => localStorage.setItem(key, value);
