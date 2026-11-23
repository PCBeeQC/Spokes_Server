// Keep track of the observer to disconnect it when re-initializing
let currentObserver = null;

export function initInfiniteScroll(containerId, sentinelId, dotNetHelper) {
    const container = document.getElementById(containerId);
    const sentinel = document.getElementById(sentinelId);

    if (currentObserver) {
        currentObserver.disconnect();
        currentObserver = null;
    }

    if (!container || !sentinel) {
        // console.warn('[Email] initInfiniteScroll: Missing elements', { container, sentinel, containerId, sentinelId });
        return;
    }

    currentObserver = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                dotNetHelper.invokeMethodAsync('LoadOlderMessagesJS');
            }
        });
    }, {
        root: container,
        threshold: 0.1
    });

    currentObserver.observe(sentinel);
}

export function scrollToTopOfEmail() {
    const scrollContainer = document.querySelector('.print-scroll');
    if (scrollContainer) {
        scrollContainer.scrollTop = 0;
    }
}

export function printEmail(subject, from, to, date, htmlContent) {
    const iframe = document.createElement('iframe');
    iframe.setAttribute('sandbox', 'allow-same-origin allow-modals');
    iframe.style.position = 'absolute';
    iframe.style.top = '-9999px';
    iframe.style.left = '-9999px';
    iframe.style.width = '1000px';
    iframe.style.height = '1000px';

    // Create a clean header (header fields are HTML-encoded server-side; htmlContent is server-sanitized)
    const headerHtml = `
        <div style="margin-bottom: 20px; border-bottom: 1px solid #ddd; padding-bottom: 15px;">
            <h2 style="margin: 0 0 15px 0;">${subject}</h2>
            <div style="font-size: 14px; color: #555; line-height: 1.6;">
                <div><strong>From:</strong> ${from}</div>
                <div><strong>To:</strong> ${to}</div>
                <div><strong>Date:</strong> ${date}</div>
            </div>
        </div>
    `;

    const documentHtml = `
        <!DOCTYPE html>
        <html>
        <head>
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data: cid: http: https:; style-src 'unsafe-inline'; font-src data:;" />
            <style>
                body { 
                    font-family: Roboto, Helvetica, Arial, sans-serif; 
                    padding: 20px; 
                    line-height: 1.5;
                }
                img { max-width: 100%; height: auto; }
                a { color: #1a0dab; text-decoration: none; }
                @media print {
                    body { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
                }
            </style>
        </head>
        <body>
            ${headerHtml}
            ${htmlContent}
        </body>
        </html>
    `;

    iframe.onload = () => {
        setTimeout(() => {
            if (iframe.contentWindow) {
                iframe.contentWindow.focus();
                iframe.contentWindow.print();
            }
            setTimeout(() => {
                if (document.body.contains(iframe)) {
                    document.body.removeChild(iframe);
                }
            }, 1000);
        }, 250);
    };

    iframe.srcdoc = documentHtml;
    document.body.appendChild(iframe);
}
