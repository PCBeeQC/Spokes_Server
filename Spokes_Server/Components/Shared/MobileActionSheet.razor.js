export function initSwipeToDismiss(elementId, dotNetRef) {
    const sheet = document.getElementById(elementId);
    if (!sheet) return;

    let startY = 0;
    let currentY = 0;
    let isDragging = false;
    let startTime = 0;

    const handleTouchStart = (e) => {
        // Allow inner scrollable elements to scroll normally
        let target = e.target;
        let isScrollable = false;
        while (target && target !== sheet) {
            if (target.scrollHeight > target.clientHeight) {
                isScrollable = true;
                break;
            }
            target = target.parentNode;
        }

        if (!isScrollable) {
            startY = e.touches[0].clientY;
            isDragging = true;
            startTime = Date.now();
            sheet.style.transition = 'none';
        }
    };

    const handleTouchMove = (e) => {
        if (!isDragging) return;
        currentY = e.touches[0].clientY - startY;
        
        // Prevent dragging upwards (negative Y)
        if (currentY < 0) {
            currentY = 0;
        } else {
            // Prevent background page scrolling while dragging the sheet
            e.preventDefault(); 
        }
        
        sheet.style.transform = `translateY(${currentY}px)`;
    };

    const handleTouchEnd = (e) => {
        if (!isDragging) return;
        isDragging = false;
        
        const duration = Date.now() - startTime;
        const velocity = currentY / duration;

        // Dismiss if dragged down more than 100px OR swiped fast down (velocity > 0.5)
        if (currentY > 100 || velocity > 0.5) {
            sheet.style.transition = 'transform 0.2s ease-out';
            sheet.style.transform = `translateY(100%)`;
            setTimeout(() => {
                dotNetRef.invokeMethodAsync('CloseSheet');
            }, 200);
        } else {
            // Snap back
            sheet.style.transition = 'transform 0.2s cubic-bezier(0.25, 0.8, 0.25, 1)';
            sheet.style.transform = `translateY(0)`;
        }
    };

    sheet.addEventListener('touchstart', handleTouchStart, { passive: true });
    sheet.addEventListener('touchmove', handleTouchMove, { passive: false });
    sheet.addEventListener('touchend', handleTouchEnd, { passive: true });
}

let actionSheetDotNetHelper = null;
let actionSheetPopStateListener = null;

export function initHistoryState(dotNetRef) {
    if (actionSheetDotNetHelper) {
         cleanupHistoryState(false);
    }
    actionSheetDotNetHelper = dotNetRef;
    
    // Push a dummy state to intercept back navigation
    history.pushState({ actionSheet: true }, "");

    actionSheetPopStateListener = (e) => {
        // This fires when the user hits 'Back'
        if (actionSheetDotNetHelper) {
             actionSheetDotNetHelper.invokeMethodAsync('HandleBack');
             actionSheetDotNetHelper = null;
        }
        if (actionSheetPopStateListener) {
             window.removeEventListener('popstate', actionSheetPopStateListener);
             actionSheetPopStateListener = null;
        }
    };

    window.addEventListener('popstate', actionSheetPopStateListener);
}

export function cleanupHistoryState(goBack) {
    if (actionSheetPopStateListener) {
        window.removeEventListener('popstate', actionSheetPopStateListener);
        actionSheetPopStateListener = null;
    }
    actionSheetDotNetHelper = null;
    
    // If closed via UI (swipe down, tap backdrop, etc.), pop the dummy state to keep history clean
    if (goBack && history.state && history.state.actionSheet) {
        history.back();
    }
}
