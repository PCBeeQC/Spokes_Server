// Sandboxed iframe email renderer
// Uses iframe with sandbox="allow-same-origin" for full HTML document rendering
// with complete style isolation, no script execution, Privacy Shield remote image blocking,
// and Smart Dark Mode adaptation.

const TRANSPARENT_PIXEL = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';
const REMOTE_URL_REGEX = /^(https?:)?\/\//i;
const CSS_REMOTE_URL_REGEX = /url\s*\(\s*(['"]?)\s*(https?:)?\/\/[^)'"]*\1\s*\)/gi;
const SKIP_DARK_MODE_TAGS = new Set([
    'IMG', 'VIDEO', 'PICTURE', 'CANVAS', 'SVG', 'SCRIPT', 'STYLE',
    'HEAD', 'META', 'LINK', 'TITLE', 'BR', 'WBR', 'IFRAME', 'OBJECT', 'EMBED'
]);

function parseRgb(colorStr) {
    if (!colorStr) return null;
    const match = colorStr.match(/rgba?\s*\(\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)(?:\s*[,/]\s*(\d+(?:\.\d+)?))?\s*\)/i);
    if (!match) return null;
    return {
        r: Math.min(255, Math.max(0, Math.round(parseFloat(match[1])))),
        g: Math.min(255, Math.max(0, Math.round(parseFloat(match[2])))),
        b: Math.min(255, Math.max(0, Math.round(parseFloat(match[3])))),
        a: match[4] !== undefined ? parseFloat(match[4]) : 1
    };
}

function getLuminance(r, g, b) {
    return (0.299 * r + 0.587 * g + 0.114 * b) / 255;
}

function rgbToHsl(r, g, b) {
    r /= 255;
    g /= 255;
    b /= 255;
    const max = Math.max(r, g, b);
    const min = Math.min(r, g, b);
    let h = 0;
    let s = 0;
    const l = (max + min) / 2;

    if (max !== min) {
        const d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        switch (max) {
            case r: h = ((g - b) / d + (g < b ? 6 : 0)) / 6; break;
            case g: h = ((b - r) / d + 2) / 6; break;
            case b: h = ((r - g) / d + 4) / 6; break;
        }
    }
    return { h: Math.round(h * 360), s: Math.round(s * 100), l: Math.round(l * 100) };
}

function isParentThemeDark() {
    try {
        const rootStyle = getComputedStyle(document.documentElement);
        const surface = (rootStyle.getPropertyValue('--mud-palette-surface') || '').trim();
        if (!surface) return false;

        if (surface.startsWith('#')) {
            let hex = surface.slice(1);
            if (hex.length === 3) {
                hex = hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2];
            }
            if (hex.length >= 6) {
                const r = parseInt(hex.slice(0, 2), 16);
                const g = parseInt(hex.slice(2, 4), 16);
                const b = parseInt(hex.slice(4, 6), 16);
                const luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                return luminance < 128;
            }
        }

        const rgbMatch = surface.match(/rgba?\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/i);
        if (rgbMatch) {
            const r = parseInt(rgbMatch[1], 10);
            const g = parseInt(rgbMatch[2], 10);
            const b = parseInt(rgbMatch[3], 10);
            const luminance = 0.299 * r + 0.587 * g + 0.114 * b;
            return luminance < 128;
        }
    } catch (e) {
        console.warn('Could not detect parent theme:', e);
    }
    return false;
}

function applyDarkModeToDocument(doc, win) {
    if (!doc || !doc.body || !win) return;

    try {
        const elements = doc.body.querySelectorAll('*');
        for (const el of elements) {
            const tag = el.tagName;
            if (SKIP_DARK_MODE_TAGS.has(tag) || el.closest('svg')) {
                continue;
            }

            const computed = win.getComputedStyle(el);
            if (!computed) continue;

            // 1. Inspect and normalize background-color
            const bg = parseRgb(computed.backgroundColor);
            let hasVibrantButtonBg = false;

            if (bg && bg.a > 0.05) {
                const bgLum = getLuminance(bg.r, bg.g, bg.b);
                const bgChroma = Math.max(bg.r, bg.g, bg.b) - Math.min(bg.r, bg.g, bg.b);

                // Keep vibrant medium-luminance buttons/badges intact (e.g. primary blue/green CTA buttons)
                if (bgChroma > 70 && bgLum >= 0.20 && bgLum <= 0.65) {
                    hasVibrantButtonBg = true;
                } else if (bgLum >= 0.82) {
                    // White / near-white backgrounds (including Outlook's <p/span style="background-color: white">)
                    el.style.setProperty('background-color', 'transparent', 'important');
                } else if (bgLum > 0.50) {
                    // Shaded light gray or pastel callout boxes
                    if (bgChroma < 25) {
                        el.style.setProperty('background-color', 'rgba(255, 255, 255, 0.06)', 'important');
                    } else {
                        const hsl = rgbToHsl(bg.r, bg.g, bg.b);
                        const darkSat = Math.min(hsl.s, 55);
                        el.style.setProperty('background-color', `hsl(${hsl.h}, ${darkSat}%, 18%)`, 'important');
                    }
                }
            }

            // 2. Inspect and normalize text color (unless inside a vibrant solid CTA button)
            if (!hasVibrantButtonBg) {
                const fg = parseRgb(computed.color);
                if (fg && fg.a > 0.1) {
                    const fgLum = getLuminance(fg.r, fg.g, fg.b);
                    const fgChroma = Math.max(fg.r, fg.g, fg.b) - Math.min(fg.r, fg.g, fg.b);

                    if (fgLum < 0.50) {
                        if (tag === 'A') {
                            el.style.setProperty('color', '#60a5fa', 'important');
                        } else if (fgChroma < 35) {
                            // Grayscale / black text (e.g. rgb(0,0,0), #000000, black, #333)
                            const targetColor = fgLum < 0.28 ? '#e2e8f0' : '#cbd5e1';
                            el.style.setProperty('color', targetColor, 'important');
                        } else {
                            // Colored dark text (e.g. dark blue headers, dark red warnings) -> preserve hue, boost lightness
                            const hsl = rgbToHsl(fg.r, fg.g, fg.b);
                            const boostedLightness = Math.max(hsl.l, 72);
                            el.style.setProperty('color', `hsl(${hsl.h}, ${hsl.s}%, ${boostedLightness}%)`, 'important');
                        }
                    }
                }
            }

            // 3. Soften overly bright or pitch-black borders (e.g. Outlook reply dividers and table borders)
            for (const side of ['Top', 'Bottom', 'Left', 'Right']) {
                const width = computed[`border${side}Width`];
                const style = computed[`border${side}Style`];
                if (width && width !== '0px' && style && style !== 'none') {
                    const borderCol = parseRgb(computed[`border${side}Color`]);
                    if (borderCol && borderCol.a > 0.1) {
                        const borderLum = getLuminance(borderCol.r, borderCol.g, borderCol.b);
                        if (borderLum > 0.75 || borderLum < 0.12) {
                            el.style.setProperty(`border-${side.toLowerCase()}-color`, '#334155', 'important');
                        }
                    }
                }
            }
        }
    } catch (e) {
        console.warn('Error applying dark mode adjustments to email:', e);
    }
}

function sanitizeRemoteImages(htmlContent) {
    let blocked = false;
    try {
        const parser = new DOMParser();
        const doc = parser.parseFromString(htmlContent, 'text/html');

        doc.querySelectorAll('img').forEach(img => {
            const src = (img.getAttribute('src') || '').trim();
            if (REMOTE_URL_REGEX.test(src)) {
                img.setAttribute('data-blocked-src', src);
                img.setAttribute('src', TRANSPARENT_PIXEL);
                blocked = true;
            }
            const srcset = (img.getAttribute('srcset') || '').trim();
            if (srcset && /(https?:)?\/\//i.test(srcset)) {
                img.removeAttribute('srcset');
                blocked = true;
            }
        });

        doc.querySelectorAll('[background]').forEach(el => {
            const bg = (el.getAttribute('background') || '').trim();
            if (REMOTE_URL_REGEX.test(bg)) {
                el.removeAttribute('background');
                blocked = true;
            }
        });

        doc.querySelectorAll('[style]').forEach(el => {
            const styleVal = el.getAttribute('style') || '';
            if (CSS_REMOTE_URL_REGEX.test(styleVal)) {
                CSS_REMOTE_URL_REGEX.lastIndex = 0;
                el.setAttribute('style', styleVal.replace(CSS_REMOTE_URL_REGEX, 'none'));
                blocked = true;
            }
        });

        doc.querySelectorAll('style').forEach(styleEl => {
            const css = styleEl.textContent || '';
            if (CSS_REMOTE_URL_REGEX.test(css)) {
                CSS_REMOTE_URL_REGEX.lastIndex = 0;
                styleEl.textContent = css.replace(CSS_REMOTE_URL_REGEX, 'none');
                blocked = true;
            }
        });

        if (blocked) {
            return { html: '<!DOCTYPE html>' + doc.documentElement.outerHTML, blocked: true };
        }
    } catch (e) {
        console.warn('Error sanitizing remote images:', e);
    }
    return { html: htmlContent, blocked: false };
}

export function cleanupContent(container) {
    if (!container) return;
    if (container._resizeObserver) {
        try {
            container._resizeObserver.disconnect();
        } catch (e) {}
        container._resizeObserver = null;
    }
    if (container._rafId) {
        cancelAnimationFrame(container._rafId);
        container._rafId = null;
    }
    container.innerHTML = '';
}

export function renderContent(container, htmlContent, dotNetHelper, allowRemoteImages = false, themeOverride = 'auto') {
    if (!container) return;

    try {
        cleanupContent(container);

        if (!htmlContent) {
            if (dotNetHelper) {
                dotNetHelper.invokeMethodAsync('NotifyRemoteImagesBlocked', false);
            }
            return;
        }

        let finalHtml = htmlContent;
        let blocked = false;

        if (!allowRemoteImages) {
            const result = sanitizeRemoteImages(htmlContent);
            finalHtml = result.html;
            blocked = result.blocked;
        }

        if (dotNetHelper) {
            dotNetHelper.invokeMethodAsync('NotifyRemoteImagesBlocked', blocked);
        }

        const parentIsDark = isParentThemeDark();
        const useDarkMode = themeOverride === 'dark' || (themeOverride === 'auto' && parentIsDark);

        const iframe = document.createElement('iframe');
        // allow-same-origin lets us access contentDocument for auto-sizing
        // No allow-scripts, allow-forms, allow-popups — blocks all dangerous behavior
        iframe.sandbox = 'allow-same-origin';
        iframe.style.cssText = `width: 100%; border: none; overflow: hidden; min-height: 200px; background: ${useDarkMode ? 'transparent' : 'white'}; border-radius: 8px; display: block;`;

        let isSetup = false;
        let lastHeight = 0;

        const setupIframe = () => {
            if (isSetup) return;
            try {
                const doc = iframe.contentDocument;
                if (!doc || !doc.body) return;
                isSetup = true;

                const style = doc.createElement('style');
                if (useDarkMode) {
                    style.textContent = `
                        :root { color-scheme: dark; }
                        html, body {
                            height: auto !important;
                            overflow-x: auto;
                            overflow-y: hidden;
                            background-color: transparent !important;
                            color: #e2e8f0 !important;
                        }
                        body {
                            margin: 0;
                            padding: 16px;
                            font-family: Roboto, Helvetica, Arial, sans-serif;
                            line-height: 1.5;
                            word-wrap: break-word;
                        }
                        img { max-width: 100%; height: auto; }
                        a { color: #60a5fa; text-decoration: underline; cursor: pointer; }
                        hr { border-color: #334155 !important; background-color: #334155 !important; color: #334155 !important; }
                        blockquote { border-left: 3px solid #475569; margin-left: 0; padding-left: 12px; color: #94a3b8; }
                    `;
                } else {
                    style.textContent = `
                        html, body { height: auto !important; overflow-x: auto; overflow-y: hidden; }
                        body { margin: 0; padding: 16px; font-family: Roboto, Helvetica, Arial, sans-serif; line-height: 1.5; word-wrap: break-word; background-color: #ffffff; color: #111827; }
                        img { max-width: 100%; height: auto; }
                        a { color: #1a0dab; text-decoration: underline; cursor: pointer; }
                    `;
                }

                if (doc.head) {
                    doc.head.appendChild(style);
                } else {
                    doc.body.prepend(style);
                }

                if (useDarkMode && iframe.contentWindow) {
                    applyDarkModeToDocument(doc, iframe.contentWindow);
                }

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

                // Resize function wrapped in requestAnimationFrame to prevent ResizeObserver loop notifications
                const resize = () => {
                    if (container._rafId) {
                        cancelAnimationFrame(container._rafId);
                    }
                    container._rafId = requestAnimationFrame(() => {
                        container._rafId = null;
                        if (!iframe.contentDocument || !iframe.contentDocument.body) return;
                        const bodyHeight = iframe.contentDocument.body.scrollHeight || 0;
                        const docHeight = iframe.contentDocument.documentElement ? iframe.contentDocument.documentElement.offsetHeight : 0;
                        const height = Math.max(bodyHeight, docHeight, 200);
                        if (Math.abs(height - lastHeight) > 4) {
                            lastHeight = height;
                            iframe.style.height = height + 'px';
                        }
                    });
                };

                // Initial resize
                resize();

                // Watch for dynamic content changes (images loading, etc.)
                const resizeObserver = new ResizeObserver(() => resize());
                resizeObserver.observe(doc.body);
                container._resizeObserver = resizeObserver;

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
                iframe.style.height = '600px';
                iframe.style.overflow = 'auto';
            }
        };

        iframe.onload = setupIframe;
        iframe.srcdoc = finalHtml;
        container.appendChild(iframe);
    } catch (e) {
        console.error('Error initializing renderContent:', e);
    }
}
