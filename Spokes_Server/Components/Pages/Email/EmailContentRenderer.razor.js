// Sandboxed iframe email renderer
// Uses iframe with sandbox="allow-same-origin" for full HTML document rendering
// with complete style isolation and no script execution.
// This is the approach used by Gmail, Outlook.com, and most production email clients.

export function renderContent(container, htmlContent, dotNetHelper) {
    if (!container) return;

    try {
        // Clear existing content
        container.innerHTML = '';

        if (!htmlContent) return;

    const iframe = document.createElement('iframe');
    // allow-same-origin lets us access contentDocument for auto-sizing
    // No allow-scripts, allow-forms, allow-popups — blocks all dangerous behavior
    iframe.sandbox = 'allow-same-origin';
    iframe.style.cssText = 'width: 100%; border: none; overflow: hidden; min-height: 200px; background: white;';
    
    // Set srcdoc with the email HTML content
    iframe.srcdoc = htmlContent;

    container.appendChild(iframe);

    // Auto-resize iframe to match content height
    iframe.onload = () => {
        try {
            const doc = iframe.contentDocument;
            if (!doc || !doc.body) return;

            // Set base styles on the iframe body for consistent rendering
            const style = doc.createElement('style');
            style.textContent = `
                body { margin: 0; padding: 16px; font-family: Roboto, Helvetica, Arial, sans-serif; line-height: 1.5; }
                img { max-width: 100%; height: auto; }
                a { color: #1a0dab; text-decoration: underline; cursor: pointer; }
            `;
            doc.head.appendChild(style);

            // Intercept link clicks
            const links = doc.querySelectorAll('a[href]');
            links.forEach(link => {
                link.addEventListener('click', (e) => {
                    e.preventDefault();
                    const href = link.getAttribute('href');
                    if (href && dotNetHelper) {
                        dotNetHelper.invokeMethodAsync('HandleLinkClick', href);
                    }
                });
            });

            // Resize function
            const resize = () => {
                const height = doc.documentElement.scrollHeight || doc.body.scrollHeight;
                if (height > 0) {
                    iframe.style.height = height + 'px';
                }
            };

            // Initial resize
            resize();

            // Watch for dynamic content changes (images loading, etc.)
            const resizeObserver = new ResizeObserver(() => resize());
            resizeObserver.observe(doc.body);

            // Also handle images that load after initial render
            const images = doc.querySelectorAll('img');
            images.forEach(img => {
                if (!img.complete) {
                    img.addEventListener('load', resize);
                    img.addEventListener('error', resize);
                }
            });
        } catch (e) {
            console.error('Error in EmailContentRenderer.razor.js iframe load:', e);
            // Cross-origin fallback — set a reasonable default height
            iframe.style.height = '600px';
            iframe.style.overflow = 'auto';
        }
    };
    } catch (e) {
        console.error('Error initializing renderContent:', e);
    }
}
