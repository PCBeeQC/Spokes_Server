export function waitForElement(selector, timeout = 2000) {
    return new Promise((resolve) => {
        const existing = document.querySelector(selector);
        if (existing) {
            return resolve(existing);
        }

        const observer = new MutationObserver(() => {
            const el = document.querySelector(selector);
            if (el) {
                resolve(el);
                observer.disconnect();
            }
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true
        });

        setTimeout(() => {
            observer.disconnect();
            resolve(null);
        }, timeout);
    });
}
