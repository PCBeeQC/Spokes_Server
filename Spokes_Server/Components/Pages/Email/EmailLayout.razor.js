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

    console.log('[Email] initInfiniteScroll: Attaching to', container, sentinel);

    currentObserver = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                console.log('[Email] Triggering LoadOlderMessagesJS');
                dotNetHelper.invokeMethodAsync('LoadOlderMessagesJS');
            }
        });
    }, {
        root: container,
        threshold: 0.1
    });

    currentObserver.observe(sentinel);
}

export async function downloadFileFromStream(fileName, contentStreamReference) {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer]);
    const url = URL.createObjectURL(blob);
    const anchorElement = document.createElement('a');
    anchorElement.href = url;
    anchorElement.download = fileName ?? '';
    anchorElement.click();
    anchorElement.remove();
    URL.revokeObjectURL(url);
}

// Robust scroll retention strategies

export function printEmail(subject, from, to, date, htmlContent) {
    const iframe = document.createElement('iframe');
    iframe.style.position = 'absolute';
    iframe.style.top = '-9999px';
    iframe.style.left = '-9999px';
    iframe.style.width = '1000px';
    iframe.style.height = '1000px';
    document.body.appendChild(iframe);

    // Create a clean header
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

    const doc = iframe.contentWindow.document;
    doc.open();
    doc.write(`
        <!DOCTYPE html>
        <html>
        <head>
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
    `);
    doc.close();

    // Set a short delay to allow images and layout to process before triggering print
    setTimeout(() => {
        if (iframe.contentWindow) {
            iframe.contentWindow.focus();
            iframe.contentWindow.print();
        }
        // Cleanup iframe after print dialog is handled
        setTimeout(() => {
            if (document.body.contains(iframe)) {
                document.body.removeChild(iframe);
            }
        }, 1000);
    }, 500);
}
